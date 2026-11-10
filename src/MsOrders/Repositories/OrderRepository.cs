using System.Text.Json;
using Dapper;
using MySqlConnector;
using MsOrders.Domain;

namespace MsOrders.Repositories;

public sealed class OrderRepository(IConfiguration configuration)
{
    public async Task<MySqlConnection> Open(CancellationToken ct = default)
    {
        var connection = new MySqlConnection(configuration.GetConnectionString("Orders")
            ?? throw new InvalidOperationException("ConnectionStrings__Orders is required"));
        try { await connection.OpenAsync(ct); return connection; }
        catch { await connection.DisposeAsync(); throw; }
    }
    public async Task Initialize(CancellationToken ct)
    {
        await using var db = await Open(ct);
        await db.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS orders (
                Id varchar(36) PRIMARY KEY, UserId varchar(100) NOT NULL,
                Status varchar(20) NOT NULL, ItemsJson JSON NOT NULL,
                Total decimal(20,2) NULL, FailureReason varchar(500) NULL,
                CreatedAt datetime(6) NOT NULL, UpdatedAt datetime(6) NOT NULL,
                INDEX ix_status (Status, CreatedAt));
            CREATE TABLE IF NOT EXISTS outbox_events (
                Id varchar(36) PRIMARY KEY, EventType varchar(100) NOT NULL,
                Body JSON NOT NULL, CreatedAt datetime(6) NOT NULL,
                PublishedAt datetime(6) NULL, INDEX ix_pending (PublishedAt, CreatedAt));
            """);
    }
    public async Task<Order?> Find(string id)
    {
        await using var db = await Open();
        return await db.QuerySingleOrDefaultAsync<Order>("SELECT * FROM orders WHERE Id=@id", new { id });
    }
    public async Task<IEnumerable<Order>> List(int offset, int limit)
    {
        await using var db = await Open();
        return await db.QueryAsync<Order>("SELECT * FROM orders ORDER BY CreatedAt DESC, Id LIMIT @limit OFFSET @offset", new { offset, limit });
    }
    public async Task<Order> Create(OrderRequest request)
    {
        var order = new Order { Id = Guid.NewGuid().ToString(), UserId = request.UserId.Trim(),
            ItemsJson = JsonSerializer.Serialize(request.Items), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        await using var db = await Open();
        await db.ExecuteAsync("INSERT INTO orders (Id,UserId,Status,ItemsJson,CreatedAt,UpdatedAt) VALUES (@Id,@UserId,@Status,@ItemsJson,@CreatedAt,@UpdatedAt)", order);
        return order;
    }
    public async Task<Order?> Update(string id, OrderRequest request)
    {
        await using var db = await Open();
        await using var tx = await db.BeginTransactionAsync();
        var order = await db.QuerySingleOrDefaultAsync<Order>("SELECT * FROM orders WHERE Id=@id FOR UPDATE", new { id }, tx);
        if (order is null) return null;
        if (order.Status != "DRAFT") throw new OrderConflictException("Only DRAFT orders can be edited");
        order.UserId = request.UserId.Trim(); order.ItemsJson = JsonSerializer.Serialize(request.Items); order.UpdatedAt = DateTime.UtcNow;
        await db.ExecuteAsync("UPDATE orders SET UserId=@UserId,ItemsJson=@ItemsJson,UpdatedAt=@UpdatedAt WHERE Id=@Id", order, tx);
        await tx.CommitAsync();
        return order;
    }
    public async Task<bool> Delete(string id)
    {
        await using var db = await Open();
        await using var tx = await db.BeginTransactionAsync();
        var order = await db.QuerySingleOrDefaultAsync<Order>("SELECT * FROM orders WHERE Id=@id FOR UPDATE", new { id }, tx);
        if (order is null) return false;
        if (order.Status != "DRAFT") throw new OrderConflictException("Only DRAFT orders can be deleted; confirmed orders keep their audit history");
        await db.ExecuteAsync("DELETE FROM orders WHERE Id=@id", new { id }, tx);
        await tx.CommitAsync(); return true;
    }
    public async Task<Order?> Submit(string id)
    {
        await using var db = await Open();
        await db.ExecuteAsync("UPDATE orders SET Status='PENDING',UpdatedAt=UTC_TIMESTAMP(6) WHERE Id=@id AND Status='DRAFT'", new { id });
        return await Find(id);
    }
}

using System.Text.Json;
using Dapper;
using MsOrders.Domain;
using MsOrders.Repositories;

namespace MsOrders.Services;

public sealed class OrderWorker(OrderRepository repository, CatalogClient catalog, ILogger<OrderWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await ProcessOne(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "Order processing deferred; PENDING orders will be retried"); }
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }
    public async Task ProcessOne(CancellationToken ct)
    {
        await using var db = await repository.Open(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var order = await db.QueryFirstOrDefaultAsync<Order>("SELECT * FROM orders WHERE Status='PENDING' ORDER BY UpdatedAt,Id LIMIT 1 FOR UPDATE SKIP LOCKED", transaction: tx);
        if (order is null) return;
        ReservationResult result;
        try { result = await catalog.Reserve(order, ct); }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // Unknown outcome: retry the SAME immutable order ID, never reject on a timeout.
            await db.ExecuteAsync("UPDATE orders SET UpdatedAt=UTC_TIMESTAMP(6) WHERE Id=@Id", order, tx);
            await tx.CommitAsync(ct);
            throw;
        }
        order.Status = result.Accepted ? "CONFIRMED" : "REJECTED";
        order.Total = result.Accepted ? result.Total : null;
        order.FailureReason = result.Reason;
        await db.ExecuteAsync("UPDATE orders SET Status=@Status,Total=@Total,FailureReason=@FailureReason,UpdatedAt=UTC_TIMESTAMP(6) WHERE Id=@Id", order, tx);
        var eventId = Guid.NewGuid().ToString();
        var eventType = result.Accepted ? "order.confirmed" : "order.rejected";
        var body = JsonSerializer.Serialize(new { event_id = eventId, event_type = eventType, order_id = order.Id,
            user_id = order.UserId, timestamp = DateTime.UtcNow, payload = new { status = order.Status, total = order.Total,
                items = JsonSerializer.Deserialize<object>(order.ItemsJson), reason = order.FailureReason } });
        await db.ExecuteAsync("INSERT INTO outbox_events (Id,EventType,Body,CreatedAt) VALUES (@eventId,@eventType,@body,UTC_TIMESTAMP(6))", new { eventId, eventType, body }, tx);
        await tx.CommitAsync(ct);
        logger.LogInformation("[{Pod}] Order {Id}: {Status}", Environment.MachineName, order.Id, order.Status);
    }
}

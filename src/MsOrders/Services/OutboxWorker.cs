using System.Text;
using Dapper;
using MsOrders.Repositories;
using RabbitMQ.Client;

namespace MsOrders.Services;

public sealed class OutboxWorker(OrderRepository repository, IConfiguration config, ILogger<OutboxWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await PublishOne(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "Outbox delivery deferred"); }
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }
    private async Task PublishOne(CancellationToken ct)
    {
        await using var db = await repository.Open(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var evt = await db.QueryFirstOrDefaultAsync<OutboxMessage>("SELECT Id,EventType,Body FROM outbox_events WHERE PublishedAt IS NULL ORDER BY CreatedAt,Id LIMIT 1 FOR UPDATE SKIP LOCKED", transaction: tx);
        if (evt is null) return;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var token = timeout.Token;
        var factory = new ConnectionFactory { Uri = new Uri(config["RabbitMQ:Url"] ?? throw new InvalidOperationException("RabbitMQ__Url is required")), AutomaticRecoveryEnabled = false };
        await using var connection = await factory.CreateConnectionAsync(token);
        await using var channel = await connection.CreateChannelAsync(new CreateChannelOptions(true, true), token);
        var exchange = config["RabbitMQ:Exchange"] ?? "app_events_exchange";
        var queue = config["RabbitMQ:Queue"] ?? "pedidos_events";
        await channel.ExchangeDeclareAsync(exchange, ExchangeType.Topic, durable: true, cancellationToken: token);
        await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false, cancellationToken: token);
        await channel.QueueBindAsync(queue, exchange, "order.*", cancellationToken: token);
        await channel.BasicPublishAsync(exchange, evt.EventType, mandatory: true,
            new BasicProperties { Persistent = true, ContentType = "application/json", MessageId = evt.Id }, Encoding.UTF8.GetBytes(evt.Body), token);
        await db.ExecuteAsync("UPDATE outbox_events SET PublishedAt=UTC_TIMESTAMP(6) WHERE Id=@Id", evt, tx);
        await tx.CommitAsync(ct);
        logger.LogInformation("Published event {Id} ({Type})", evt.Id, evt.EventType);
    }
    private sealed class OutboxMessage
    {
        public string Id { get; set; } = "";
        public string EventType { get; set; } = "";
        public string Body { get; set; } = "";
    }
}

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Shared.Contracts;
using Shared.Messaging;

sealed class OrdersOutboxDispatcher(
    IServiceScopeFactory scopeFactory,
    IEventPublisher publisher,
    ILogger<OrdersOutboxDispatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<OrdersDb>();
            var messages = await db.OutboxMessages
                .Where(message => message.PublishedAt == null)
                .OrderBy(message => message.Id)
                .Take(20)
                .ToListAsync(stoppingToken);

            if (messages.Count == 0)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), stoppingToken);
                continue;
            }

            foreach (var message in messages)
            {
                try
                {
                    await publisher.PublishAsync(Deserialize(message), stoppingToken);
                    message.PublishedAt = DateTimeOffset.UtcNow;
                    await db.SaveChangesAsync(stoppingToken);
                }
                catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogError(exception, "Could not publish outbox event {EventId} ({EventType}).",
                        message.EventId, message.EventType);
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                    break;
                }
            }
        }
    }

    private static IIntegrationEvent Deserialize(OutboxMessage message) => message.EventType switch
    {
        nameof(OrderPlaced) => JsonSerializer.Deserialize<OrderPlaced>(message.Payload)
            ?? throw new InvalidOperationException($"Outbox event {message.EventId} has an empty payload."),
        nameof(StockReserved) => JsonSerializer.Deserialize<StockReserved>(message.Payload)
            ?? throw new InvalidOperationException($"Outbox event {message.EventId} has an empty payload."),
        nameof(StockReleaseRequested) => JsonSerializer.Deserialize<StockReleaseRequested>(message.Payload)
            ?? throw new InvalidOperationException($"Outbox event {message.EventId} has an empty payload."),
        nameof(PaymentCharged) => JsonSerializer.Deserialize<PaymentCharged>(message.Payload)
            ?? throw new InvalidOperationException($"Outbox event {message.EventId} has an empty payload."),
        nameof(PaymentFailed) => JsonSerializer.Deserialize<PaymentFailed>(message.Payload)
            ?? throw new InvalidOperationException($"Outbox event {message.EventId} has an empty payload."),
        nameof(OrderConfirmed) => JsonSerializer.Deserialize<OrderConfirmed>(message.Payload)
            ?? throw new InvalidOperationException($"Outbox event {message.EventId} has an empty payload."),
        nameof(OrderCancelled) => JsonSerializer.Deserialize<OrderCancelled>(message.Payload)
            ?? throw new InvalidOperationException($"Outbox event {message.EventId} has an empty payload."),
        _ => throw new InvalidOperationException($"Unsupported outbox event type '{message.EventType}'.")
    };

}

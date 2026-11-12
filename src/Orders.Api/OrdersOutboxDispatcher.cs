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
                    await PublishAsync(message, stoppingToken);
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

    private Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken) =>
        message.EventType switch
        {
            nameof(OrderPlaced) => PublishAsync<OrderPlaced>(message, cancellationToken),
            nameof(StockReserved) => PublishAsync<StockReserved>(message, cancellationToken),
            nameof(StockReleaseRequested) => PublishAsync<StockReleaseRequested>(message, cancellationToken),
            nameof(PaymentCharged) => PublishAsync<PaymentCharged>(message, cancellationToken),
            nameof(PaymentFailed) => PublishAsync<PaymentFailed>(message, cancellationToken),
            nameof(OrderConfirmed) => PublishAsync<OrderConfirmed>(message, cancellationToken),
            nameof(OrderCancelled) => PublishAsync<OrderCancelled>(message, cancellationToken),
            _ => throw new InvalidOperationException($"Unsupported outbox event type '{message.EventType}'.")
        };

    private async Task PublishAsync<TEvent>(OutboxMessage message, CancellationToken cancellationToken)
        where TEvent : class, IIntegrationEvent
    {
        var integrationEvent = JsonSerializer.Deserialize<TEvent>(message.Payload)
            ?? throw new InvalidOperationException($"Outbox event {message.EventId} has an empty payload.");
        await publisher.PublishAsync(integrationEvent, cancellationToken);
    }
}

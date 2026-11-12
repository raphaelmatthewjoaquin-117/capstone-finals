using Microsoft.EntityFrameworkCore;
using Generated = Orders.Api.Generated;
using Shared.Contracts;
using Shared.Messaging;

sealed class OrderPlacedConsumer(
    IConfiguration configuration,
    IProcessedMessageStore processedMessages,
    ILogger<OrderPlacedConsumer> logger,
    IServiceScopeFactory scopeFactory)
    : RabbitMqConsumer<OrderPlaced>(configuration, processedMessages, logger)
{
    protected override async Task HandleAsync(OrderPlaced message, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<OrdersDb>();
        var order = await db.Orders.Include(item => item.History)
            .SingleOrDefaultAsync(item => item.Id == message.OrderId, cancellationToken)
            ?? throw new InvalidOperationException($"Order {message.OrderId} was not found.");

        if (order.Status is "Confirmed" or "Cancelled") return;

        var inventory = services.GetRequiredService<Generated.InventoryApiClient>();
        var payments = services.GetRequiredService<Generated.PaymentsApiClient>();
        if (order.Status == "Pending")
        {
            var catalog = services.GetRequiredService<Generated.CatalogApiClient>();
            Generated.ProductDto? product;
            try
            {
                product = await catalog.GetProductAsync(message.ProductId, cancellationToken);
            }
            catch (Generated.ApiException exception) when (exception.StatusCode == StatusCodes.Status404NotFound)
            {
                product = null;
            }

            if (product is null || !product.Active)
            {
                await CancelAsync(db, order, message,
                    "Product was not found or is inactive.", cancellationToken);
                return;
            }

            order.Total = product.Price * message.Quantity;
            bool reserved;
            try
            {
                var result = await inventory.ReserveStockAsync(new Generated.ReserveStockRequest
                {
                    OrderId = order.Id,
                    ProductId = message.ProductId,
                    Quantity = message.Quantity
                }, cancellationToken);
                reserved = result.Reserved;
            }
            catch (Generated.ApiException exception) when (exception.StatusCode == StatusCodes.Status409Conflict)
            {
                reserved = false;
            }

            if (!reserved)
            {
                await CancelAsync(db, order, message, "Insufficient stock.", cancellationToken);
                return;
            }

            order.AddStatus("StockReserved", "Inventory reservation succeeded.");
            db.OutboxMessages.Add(OutboxMessage.From(new StockReserved(
                Guid.NewGuid(), DateTimeOffset.UtcNow, message.CorrelationId, order.Id)));
            await db.SaveChangesAsync(cancellationToken);
        }

        if (order.Status == "StockReserved")
        {
            var payment = await payments.ChargePaymentAsync(new Generated.ChargeRequest
            {
                OrderId = order.Id,
                Amount = order.Total,
                SimulateFailure = message.SimulatePaymentFailure
            }, cancellationToken);
            if (!payment.Succeeded)
            {
                await inventory.ReleaseStockAsync(order.Id, cancellationToken);
                var reason = payment.FailureReason ?? "Payment was declined.";
                await CancelAsync(db, order, message, reason, cancellationToken, paymentFailed: true);
                return;
            }

            order.AddStatus("PaymentCharged", "Payment charge succeeded.");
            db.OutboxMessages.Add(OutboxMessage.From(new PaymentCharged(
                Guid.NewGuid(), DateTimeOffset.UtcNow, message.CorrelationId, order.Id, order.Total)));
            await db.SaveChangesAsync(cancellationToken);
        }

        if (order.Status == "PaymentCharged")
        {
            bool committed;
            try
            {
                committed = (await inventory.CommitStockAsync(order.Id, cancellationToken)).Committed;
            }
            catch (Generated.ApiException exception) when (
                exception.StatusCode is StatusCodes.Status404NotFound or StatusCodes.Status409Conflict)
            {
                committed = false;
            }

            if (!committed)
            {
                await payments.RefundPaymentAsync(order.Id, cancellationToken);
                try
                {
                    await inventory.ReleaseStockAsync(order.Id, cancellationToken);
                }
                catch (Generated.ApiException exception) when (
                    exception.StatusCode is StatusCodes.Status404NotFound or StatusCodes.Status409Conflict)
                {
                    logger.LogWarning(exception, "Could not release stock for cancelled order {OrderId}.", order.Id);
                }
                await CancelAsync(db, order, message, "Inventory commit failed; payment refunded.",
                    cancellationToken, releaseRequested: true);
                return;
            }

            order.AddStatus("Confirmed", "Payment charged and stock committed.");
            db.OutboxMessages.Add(OutboxMessage.From(new OrderConfirmed(
                Guid.NewGuid(), DateTimeOffset.UtcNow, message.CorrelationId, order.Id)));
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Order {OrderId} completed with correlation {CorrelationId}.",
                order.Id, message.CorrelationId);
        }
    }

    private static async Task CancelAsync(
        OrdersDb db,
        Order order,
        OrderPlaced message,
        string reason,
        CancellationToken cancellationToken,
        bool paymentFailed = false,
        bool releaseRequested = false)
    {
        order.AddStatus("Cancelled", reason);
        if (paymentFailed)
        {
            db.OutboxMessages.Add(OutboxMessage.From(new PaymentFailed(
                Guid.NewGuid(), DateTimeOffset.UtcNow, message.CorrelationId, order.Id, reason)));
            releaseRequested = true;
        }
        if (releaseRequested)
        {
            db.OutboxMessages.Add(OutboxMessage.From(new StockReleaseRequested(
                Guid.NewGuid(), DateTimeOffset.UtcNow, message.CorrelationId, order.Id)));
        }
        db.OutboxMessages.Add(OutboxMessage.From(new OrderCancelled(
            Guid.NewGuid(), DateTimeOffset.UtcNow, message.CorrelationId, order.Id, reason)));
        await db.SaveChangesAsync(cancellationToken);
    }
}

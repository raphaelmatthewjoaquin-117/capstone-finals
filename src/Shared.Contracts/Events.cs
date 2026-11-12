namespace Shared.Contracts;

public interface IIntegrationEvent
{
    Guid EventId { get; }
    DateTimeOffset OccurredAt { get; }
    Guid CorrelationId { get; }
}

public sealed record OrderPlaced(Guid EventId, DateTimeOffset OccurredAt, Guid CorrelationId, Guid OrderId, Guid ProductId, int Quantity, bool SimulatePaymentFailure = false) : IIntegrationEvent;
public sealed record StockReserved(Guid EventId, DateTimeOffset OccurredAt, Guid CorrelationId, Guid OrderId) : IIntegrationEvent;
public sealed record StockReleaseRequested(Guid EventId, DateTimeOffset OccurredAt, Guid CorrelationId, Guid OrderId) : IIntegrationEvent;
public sealed record PaymentCharged(Guid EventId, DateTimeOffset OccurredAt, Guid CorrelationId, Guid OrderId, decimal Amount) : IIntegrationEvent;
public sealed record PaymentFailed(Guid EventId, DateTimeOffset OccurredAt, Guid CorrelationId, Guid OrderId, string Reason) : IIntegrationEvent;
public sealed record OrderConfirmed(Guid EventId, DateTimeOffset OccurredAt, Guid CorrelationId, Guid OrderId) : IIntegrationEvent;
public sealed record OrderCancelled(Guid EventId, DateTimeOffset OccurredAt, Guid CorrelationId, Guid OrderId, string Reason) : IIntegrationEvent;

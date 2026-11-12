namespace Shared.Contracts;

public sealed record ProductDto(Guid Id, string Name, decimal Price, bool Active);
public sealed record CreateProductRequest(string Name, decimal Price);
public sealed record StockDto(Guid ProductId, int OnHand, int Reserved);
public sealed record AdjustStockRequest(int Delta);
public sealed record PlaceOrderRequest(Guid ProductId, int Quantity, bool SimulatePaymentFailure = false);
public sealed record OrderDto(Guid Id, string Status, decimal Total, IReadOnlyList<OrderStatusDto> History);
public sealed record OrderStatusDto(string Status, DateTimeOffset At, string? Detail);
public sealed record ReserveStockRequest(Guid OrderId, Guid ProductId, int Quantity);
public sealed record ReleaseStockRequest(Guid OrderId);
public sealed record ChargeRequest(Guid OrderId, decimal Amount, bool SimulateFailure);
public sealed record PaymentResult(Guid OrderId, bool Succeeded, string? FailureReason);
public sealed record RegisterRequest(string Email, string Password, string Role = "Customer");
public sealed record LoginRequest(string Email, string Password);
public sealed record TokenResponse(string AccessToken, DateTimeOffset ExpiresAt, string Role);

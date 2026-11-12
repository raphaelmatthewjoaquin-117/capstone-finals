using Asp.Versioning;
using Microsoft.EntityFrameworkCore;
using Generated = Orders.Api.Generated;
using Shared.Contracts;
using Shared.Messaging;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddApiErrorHandling();
builder.Services.AddVersionedApiDocumentation("Orders API");
var databasePath = Environment.GetEnvironmentVariable("ORDERS_DB") ?? "orders.db";
builder.Services.AddDbContext<OrdersDb>(options => options.UseSqlite($"Data Source={databasePath}"));
builder.Services.AddEventMessaging(builder.Configuration);
var messagingEnabled = builder.Configuration.GetValue<bool>("Messaging:Enabled");
if (messagingEnabled)
{
    builder.Services.AddSingleton<IProcessedMessageStore, OrdersProcessedMessageStore>();
    builder.Services.AddHostedService<OrdersOutboxDispatcher>();
    builder.Services.AddHostedService<OrderPlacedConsumer>();
}
builder.Services.AddHttpContextAccessor();
builder.Services.AddTransient<CorrelationIdHandler>();
builder.Services.AddHttpClient<Generated.CatalogApiClient>(client =>
    client.BaseAddress = CreateServiceBaseAddress("CATALOG_URL", "http://localhost:5101"))
    .AddHttpMessageHandler<CorrelationIdHandler>().AddStandardResilienceHandler();
builder.Services.AddHttpClient<Generated.InventoryApiClient>(client =>
    client.BaseAddress = CreateServiceBaseAddress("INVENTORY_URL", "http://localhost:5102"))
    .AddHttpMessageHandler<CorrelationIdHandler>().AddStandardResilienceHandler();
builder.Services.AddHttpClient<Generated.PaymentsApiClient>(client =>
    client.BaseAddress = CreateServiceBaseAddress("PAYMENTS_URL", "http://localhost:5104"))
    .AddHttpMessageHandler<CorrelationIdHandler>().AddStandardResilienceHandler();

var app = builder.Build();
var versionSet = app.NewApiVersionSet().HasApiVersion(new ApiVersion(1, 0)).Build();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseApiErrorHandling();
app.UseVersionedApiDocumentation();
await using (var scope = app.Services.CreateAsyncScope())
{
    await scope.ServiceProvider.GetRequiredService<OrdersDb>().Database.MigrateAsync();
}

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
app.MapPost("/api/v{version:apiVersion}/orders", async Task<IResult> (
    PlaceOrderRequest request, OrdersDb db, Generated.CatalogApiClient catalog,
    Generated.InventoryApiClient inventory, Generated.PaymentsApiClient payments,
    IEventPublisher events, HttpContext context, ILogger<Program> logger) =>
{
    if (request.Quantity is < 1 or > 100)
        return Results.Problem("Quantity must be between 1 and 100.", statusCode: 400);

    var correlationId = Guid.Parse((string)context.Items["CorrelationId"]!);
    var order = new Order { ProductId = request.ProductId, Quantity = request.Quantity };
    order.AddStatus("Pending", "Order accepted.");
    db.Orders.Add(order);
    if (messagingEnabled)
    {
        db.OutboxMessages.Add(OutboxMessage.From(new OrderPlaced(
            Guid.NewGuid(), DateTimeOffset.UtcNow, correlationId, order.Id,
            request.ProductId, request.Quantity, request.SimulatePaymentFailure)));
        await db.SaveChangesAsync();
        return Results.Accepted($"/api/v1/orders/{order.Id}", ToDto(order));
    }

    await db.SaveChangesAsync();
    await events.PublishAsync(new OrderPlaced(Guid.NewGuid(), DateTimeOffset.UtcNow, correlationId,
        order.Id, request.ProductId, request.Quantity, request.SimulatePaymentFailure));

    Generated.ProductDto? product;
    try
    {
        product = await catalog.GetProductAsync(request.ProductId);
    }
    catch (Generated.ApiException exception) when (exception.StatusCode == StatusCodes.Status404NotFound)
    {
        product = null;
    }
    if (product is null || !product.Active)
    {
        order.AddStatus("Cancelled", "Product was not found or is inactive.");
        await db.SaveChangesAsync();
        await events.PublishAsync(new OrderCancelled(Guid.NewGuid(), DateTimeOffset.UtcNow, correlationId, order.Id, "Product was not found or is inactive."));
        return Results.Conflict(ToDto(order));
    }

    order.Total = product.Price * request.Quantity;
    bool reserved;
    try
    {
        var reservation = await inventory.ReserveStockAsync(new Generated.ReserveStockRequest
        {
            OrderId = order.Id,
            ProductId = request.ProductId,
            Quantity = request.Quantity
        });
        reserved = reservation.Reserved;
    }
    catch (Generated.ApiException exception) when (exception.StatusCode == StatusCodes.Status409Conflict)
    {
        reserved = false;
    }
    if (!reserved)
    {
        order.AddStatus("Cancelled", "Insufficient stock.");
        await db.SaveChangesAsync();
        await events.PublishAsync(new OrderCancelled(Guid.NewGuid(), DateTimeOffset.UtcNow, correlationId, order.Id, "Insufficient stock."));
        return Results.Conflict(ToDto(order));
    }
    order.AddStatus("StockReserved", "Inventory reservation succeeded.");
    await db.SaveChangesAsync();
    await events.PublishAsync(new StockReserved(Guid.NewGuid(), DateTimeOffset.UtcNow, correlationId, order.Id));

    var payment = await payments.ChargePaymentAsync(new Generated.ChargeRequest
    {
        OrderId = order.Id,
        Amount = order.Total,
        SimulateFailure = request.SimulatePaymentFailure
    });
    if (!payment.Succeeded)
    {
        await inventory.ReleaseStockAsync(order.Id);
        order.AddStatus("Cancelled", payment.FailureReason ?? "Payment was declined; stock reservation released.");
        await db.SaveChangesAsync();
        await events.PublishAsync(new PaymentFailed(Guid.NewGuid(), DateTimeOffset.UtcNow, correlationId, order.Id, payment.FailureReason ?? "Payment declined."));
        await events.PublishAsync(new StockReleaseRequested(Guid.NewGuid(), DateTimeOffset.UtcNow, correlationId, order.Id));
        await events.PublishAsync(new OrderCancelled(Guid.NewGuid(), DateTimeOffset.UtcNow, correlationId, order.Id, payment.FailureReason ?? "Payment declined."));
        return Results.Conflict(ToDto(order));
    }
    await events.PublishAsync(new PaymentCharged(Guid.NewGuid(), DateTimeOffset.UtcNow, correlationId, order.Id, order.Total));

    var committed = await TryCommitStockAsync(inventory, order.Id);
    if (!committed)
    {
        await payments.RefundPaymentAsync(order.Id);
        await TryReleaseStockAsync(inventory, order.Id);
        order.AddStatus("Cancelled", "Inventory commit failed; payment refunded.");
        await db.SaveChangesAsync();
        await events.PublishAsync(new StockReleaseRequested(Guid.NewGuid(), DateTimeOffset.UtcNow, correlationId, order.Id));
        await events.PublishAsync(new OrderCancelled(Guid.NewGuid(), DateTimeOffset.UtcNow, correlationId, order.Id, "Inventory commit failed."));
        return Results.Conflict(ToDto(order));
    }

    order.AddStatus("Confirmed", "Payment charged and stock committed.");
    await db.SaveChangesAsync();
    await events.PublishAsync(new OrderConfirmed(Guid.NewGuid(), DateTimeOffset.UtcNow, correlationId, order.Id));
    logger.LogInformation("Order {OrderId} completed with correlation {CorrelationId}", order.Id, correlationId);
    return Results.Created($"/api/v1/orders/{order.Id}", ToDto(order));
}).WithApiVersionSet(versionSet).MapToApiVersion(new ApiVersion(1, 0));
app.MapGet("/api/v{version:apiVersion}/orders/{id:guid}", async Task<IResult> (Guid id, OrdersDb db) =>
{
    var order = await db.Orders.Include(x => x.History).SingleOrDefaultAsync(x => x.Id == id);
    return order is null ? Results.NotFound() : Results.Ok(ToDto(order));
}).WithApiVersionSet(versionSet).MapToApiVersion(new ApiVersion(1, 0));
app.Run();

static Uri CreateServiceBaseAddress(string environmentVariable, string defaultUrl)
{
    var serviceUrl = Environment.GetEnvironmentVariable(environmentVariable) ?? defaultUrl;
    return new Uri($"{serviceUrl.TrimEnd('/')}/api/v1/");
}

static async Task<bool> TryCommitStockAsync(Generated.InventoryApiClient inventory, Guid orderId)
{
    try
    {
        return (await inventory.CommitStockAsync(orderId)).Committed;
    }
    catch (Generated.ApiException exception) when (
        exception.StatusCode is StatusCodes.Status404NotFound or StatusCodes.Status409Conflict)
    {
        return false;
    }
}

static async Task<bool> TryReleaseStockAsync(Generated.InventoryApiClient inventory, Guid orderId)
{
    try
    {
        return (await inventory.ReleaseStockAsync(orderId)).Released;
    }
    catch (Generated.ApiException exception) when (
        exception.StatusCode is StatusCodes.Status404NotFound or StatusCodes.Status409Conflict)
    {
        return false;
    }
}

static OrderDto ToDto(Order order) =>
    new(order.Id, order.Status, order.Total, order.History.OrderBy(x => x.At)
        .Select(x => new OrderStatusDto(x.Status, x.At, x.Detail)).ToArray());

sealed class CorrelationIdHandler(IHttpContextAccessor accessor) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var id = accessor.HttpContext?.Items["CorrelationId"]?.ToString() ?? CorrelationIdContext.Current;
        if (!string.IsNullOrEmpty(id)) request.Headers.TryAddWithoutValidation("X-Correlation-ID", id);
        return base.SendAsync(request, cancellationToken);
    }
}

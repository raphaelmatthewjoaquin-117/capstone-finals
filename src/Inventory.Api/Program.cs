using Asp.Versioning;
using Microsoft.EntityFrameworkCore;
using Shared.Contracts;
using Shared.Messaging;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddApiErrorHandling();
builder.Services.AddVersionedApiDocumentation("Inventory API");
var databasePath = Environment.GetEnvironmentVariable("INVENTORY_DB") ?? "inventory.db";
builder.Services.AddDbContext<InventoryDb>(options => options.UseSqlite($"Data Source={databasePath}"));

var app = builder.Build();
var versionSet = app.NewApiVersionSet().HasApiVersion(new ApiVersion(1, 0)).Build();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseApiErrorHandling();
app.UseVersionedApiDocumentation();
await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<InventoryDb>();
    await db.Database.MigrateAsync();
    if (!await db.Stock.AnyAsync())
    {
        db.Stock.AddRange(
            new StockItem { ProductId = Guid.Parse("11111111-1111-1111-1111-111111111111"), OnHand = 20 },
            new StockItem { ProductId = Guid.Parse("22222222-2222-2222-2222-222222222222"), OnHand = 30 });
        await db.SaveChangesAsync();
    }
}

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
app.MapGet("/api/v{version:apiVersion}/stock/{productId:guid}", async Task<IResult> (Guid productId, InventoryDb db) =>
{
    var stock = await db.Stock.FindAsync(productId);
    return stock is null ? Results.NotFound() : Results.Ok(new StockDto(stock.ProductId, stock.OnHand, stock.Reserved));
}).WithApiVersionSet(versionSet).MapToApiVersion(new ApiVersion(1, 0));
app.MapPost("/api/v{version:apiVersion}/stock/{productId:guid}/adjust", async Task<IResult> (Guid productId, AdjustStockRequest request, InventoryDb db) =>
{
    var stock = await db.Stock.FindAsync(productId);
    if (stock is null)
    {
        if (request.Delta < 0) return Results.Problem("Stock cannot be negative.", statusCode: 409);
        stock = new StockItem { ProductId = productId };
        db.Stock.Add(stock);
    }
    if (stock.OnHand + request.Delta < stock.Reserved)
        return Results.Problem("Adjustment would reduce stock below reserved quantity.", statusCode: 409);
    stock.OnHand += request.Delta;
    await db.SaveChangesAsync();
    return Results.Ok(new StockDto(stock.ProductId, stock.OnHand, stock.Reserved));
}).WithApiVersionSet(versionSet).MapToApiVersion(new ApiVersion(1, 0));
app.MapPost("/api/v{version:apiVersion}/reservations", async Task<IResult> (ReserveStockRequest request, InventoryDb db) =>
{
    var existing = await db.Reservations.FindAsync(request.OrderId);
    if (existing is not null) return Results.Ok(new { reserved = !existing.Released });
    var stock = await db.Stock.FindAsync(request.ProductId);
    if (request.Quantity <= 0 || stock is null || stock.OnHand - stock.Reserved < request.Quantity)
        return Results.Problem("Insufficient stock.", statusCode: 409);
    stock.Reserved += request.Quantity;
    db.Reservations.Add(new StockReservation { OrderId = request.OrderId, ProductId = request.ProductId, Quantity = request.Quantity });
    await db.SaveChangesAsync();
    return Results.Ok(new { reserved = true });
}).WithApiVersionSet(versionSet).MapToApiVersion(new ApiVersion(1, 0));
app.MapPost("/api/v{version:apiVersion}/reservations/{orderId:guid}/release", async Task<IResult> (Guid orderId, InventoryDb db) =>
{
    var reservation = await db.Reservations.FindAsync(orderId);
    if (reservation is null || reservation.Released) return Results.Ok(new { released = true });
    if (reservation.Committed) return Results.Conflict();
    var stock = await db.Stock.FindAsync(reservation.ProductId);
    if (stock is null) return Results.Problem("Reserved stock record is missing.", statusCode: 500);
    stock.Reserved -= reservation.Quantity;
    reservation.Released = true;
    await db.SaveChangesAsync();
    return Results.Ok(new { released = true });
}).WithApiVersionSet(versionSet).MapToApiVersion(new ApiVersion(1, 0));
app.MapPost("/api/v{version:apiVersion}/reservations/{orderId:guid}/commit", async Task<IResult> (Guid orderId, InventoryDb db) =>
{
    var reservation = await db.Reservations.FindAsync(orderId);
    if (reservation is null) return Results.NotFound();
    if (reservation.Committed) return Results.Ok(new { committed = true });
    if (reservation.Released) return Results.Conflict();
    var stock = await db.Stock.FindAsync(reservation.ProductId);
    if (stock is null) return Results.Problem("Reserved stock record is missing.", statusCode: 500);
    stock.Reserved -= reservation.Quantity;
    stock.OnHand -= reservation.Quantity;
    reservation.Committed = true;
    await db.SaveChangesAsync();
    return Results.Ok(new { committed = true });
}).WithApiVersionSet(versionSet).MapToApiVersion(new ApiVersion(1, 0));
app.Run();

sealed class StockItem
{
    public Guid ProductId { get; set; }
    public int OnHand { get; set; }
    public int Reserved { get; set; }
}

sealed class StockReservation
{
    public Guid OrderId { get; set; }
    public Guid ProductId { get; set; }
    public int Quantity { get; set; }
    public bool Released { get; set; }
    public bool Committed { get; set; }
}

sealed class InventoryDb(DbContextOptions<InventoryDb> options) : DbContext(options)
{
    public DbSet<StockItem> Stock => Set<StockItem>();
    public DbSet<StockReservation> Reservations => Set<StockReservation>();
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<StockItem>().HasKey(x => x.ProductId);
        modelBuilder.Entity<StockReservation>().HasKey(x => x.OrderId);
    }
}

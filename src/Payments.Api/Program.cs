using Asp.Versioning;
using Microsoft.EntityFrameworkCore;
using Shared.Contracts;
using Shared.Messaging;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddApiErrorHandling();
builder.Services.AddVersionedApiDocumentation("Payments API");
var databasePath = Environment.GetEnvironmentVariable("PAYMENTS_DB") ?? "payments.db";
builder.Services.AddDbContext<PaymentsDb>(options => options.UseSqlite($"Data Source={databasePath}"));

var app = builder.Build();
var versionSet = app.NewApiVersionSet().HasApiVersion(new ApiVersion(1, 0)).Build();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseApiErrorHandling();
app.UseVersionedApiDocumentation();
await using (var scope = app.Services.CreateAsyncScope())
{
    await scope.ServiceProvider.GetRequiredService<PaymentsDb>().Database.MigrateAsync();
}

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
app.MapPost("/api/v{version:apiVersion}/charges", async (ChargeRequest request, PaymentsDb db) =>
{
    var prior = await db.Charges.FindAsync(request.OrderId);
    if (prior is not null) return Results.Ok(new PaymentResult(prior.OrderId, prior.Succeeded, prior.FailureReason));
    var charge = new Charge
    {
        OrderId = request.OrderId,
        Amount = request.Amount,
        Succeeded = !request.SimulateFailure,
        FailureReason = request.SimulateFailure ? "Payment failure was requested." : null
    };
    db.Charges.Add(charge);
    await db.SaveChangesAsync();
    return Results.Ok(new PaymentResult(charge.OrderId, charge.Succeeded, charge.FailureReason));
}).WithApiVersionSet(versionSet).MapToApiVersion(new ApiVersion(1, 0));
app.MapPost("/api/v{version:apiVersion}/refunds/{orderId:guid}", async Task<IResult> (Guid orderId, PaymentsDb db) =>
{
    var charge = await db.Charges.FindAsync(orderId);
    if (charge is null || !charge.Succeeded) return Results.NotFound();
    charge.Refunded = true;
    await db.SaveChangesAsync();
    return Results.Ok(new { refunded = true });
}).WithApiVersionSet(versionSet).MapToApiVersion(new ApiVersion(1, 0));
app.Run();

sealed class Charge
{
    public Guid OrderId { get; set; }
    public decimal Amount { get; set; }
    public bool Succeeded { get; set; }
    public bool Refunded { get; set; }
    public string? FailureReason { get; set; }
}

sealed class PaymentsDb(DbContextOptions<PaymentsDb> options) : DbContext(options)
{
    public DbSet<Charge> Charges => Set<Charge>();
    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<Charge>().HasKey(x => x.OrderId);
}

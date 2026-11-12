using Asp.Versioning;
using Microsoft.EntityFrameworkCore;
using Shared.Contracts;
using Shared.Messaging;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddApiErrorHandling();
builder.Services.AddVersionedApiDocumentation("Catalog API");
var databasePath = Environment.GetEnvironmentVariable("CATALOG_DB") ?? "catalog.db";
builder.Services.AddDbContext<CatalogDb>(options => options.UseSqlite($"Data Source={databasePath}"));

var app = builder.Build();
var versionSet = app.NewApiVersionSet().HasApiVersion(new ApiVersion(1, 0)).Build();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseApiErrorHandling();
app.UseVersionedApiDocumentation();
await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<CatalogDb>();
    await db.Database.MigrateAsync();
    if (!await db.Products.AnyAsync())
    {
        db.Products.AddRange(
            new Product { Id = Guid.Parse("11111111-1111-1111-1111-111111111111"), Name = "Coffee", Price = 12.50m, Active = true },
            new Product { Id = Guid.Parse("22222222-2222-2222-2222-222222222222"), Name = "Tea", Price = 8.25m, Active = true });
        await db.SaveChangesAsync();
    }
}

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
var injectedFailuresRemaining = 0;
bool TryConsumeInjectedFailure()
{
    var current = Volatile.Read(ref injectedFailuresRemaining);
    while (current > 0)
    {
        var observed = Interlocked.CompareExchange(ref injectedFailuresRemaining, current - 1, current);
        if (observed == current) return true;
        current = observed;
    }
    return false;
}
app.MapPost("/api/v{version:apiVersion}/chaos/fail-next/{count:int}", (int count) =>
{
    if (!app.Environment.IsDevelopment()) return Results.NotFound();
    if (count is < 1 or > 5) return Results.Problem("Count must be between 1 and 5.", statusCode: 400);
    Interlocked.Exchange(ref injectedFailuresRemaining, count);
    return Results.Ok(new { failuresScheduled = count });
}).WithApiVersionSet(versionSet).MapToApiVersion(new ApiVersion(1, 0));
app.MapGet("/api/v{version:apiVersion}/products", async (CatalogDb db) =>
    Results.Ok(await db.Products.Where(x => x.Active).OrderBy(x => x.Name)
        .Select(x => new ProductDto(x.Id, x.Name, x.Price, x.Active)).ToListAsync()))
    .WithApiVersionSet(versionSet).MapToApiVersion(new ApiVersion(1, 0));
app.MapGet("/api/v{version:apiVersion}/products/{id:guid}", async Task<IResult> (Guid id, CatalogDb db) =>
{
    if (TryConsumeInjectedFailure())
        return Results.Problem("Development fault injection.", statusCode: 503);
    var product = await db.Products.FindAsync(id);
    return product is null || !product.Active
        ? Results.NotFound()
        : Results.Ok(new ProductDto(product.Id, product.Name, product.Price, product.Active));
}).WithApiVersionSet(versionSet).MapToApiVersion(new ApiVersion(1, 0));
app.MapPost("/api/v{version:apiVersion}/products", async (CreateProductRequest request, CatalogDb db) =>
{
    if (string.IsNullOrWhiteSpace(request.Name) || request.Price <= 0)
        return Results.Problem("A product name and positive price are required.", statusCode: 400);
    var product = new Product { Name = request.Name.Trim(), Price = request.Price, Active = true };
    db.Products.Add(product);
    await db.SaveChangesAsync();
    return Results.Created($"/api/v1/products/{product.Id}",
        new ProductDto(product.Id, product.Name, product.Price, product.Active));
}).WithApiVersionSet(versionSet).MapToApiVersion(new ApiVersion(1, 0));
app.MapPut("/api/v{version:apiVersion}/products/{id:guid}", async Task<IResult> (Guid id, CreateProductRequest request, CatalogDb db) =>
{
    var product = await db.Products.FindAsync(id);
    if (product is null) return Results.NotFound();
    if (string.IsNullOrWhiteSpace(request.Name) || request.Price <= 0)
        return Results.Problem("A product name and positive price are required.", statusCode: 400);
    product.Name = request.Name.Trim();
    product.Price = request.Price;
    await db.SaveChangesAsync();
    return Results.Ok(new ProductDto(product.Id, product.Name, product.Price, product.Active));
}).WithApiVersionSet(versionSet).MapToApiVersion(new ApiVersion(1, 0));
app.Run();

sealed class Product
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
    public bool Active { get; set; }
}

sealed class CatalogDb(DbContextOptions<CatalogDb> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();
}

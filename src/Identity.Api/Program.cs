using Asp.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Shared.Contracts;
using Shared.Messaging;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddApiErrorHandling();
builder.Services.AddVersionedApiDocumentation("Identity API");
var databasePath = Environment.GetEnvironmentVariable("IDENTITY_DB") ?? "identity.db";
builder.Services.AddDbContext<IdentityDb>(options => options.UseSqlite($"Data Source={databasePath}"));
var signingKey = Environment.GetEnvironmentVariable("JWT_SIGNING_KEY")
    ?? builder.Configuration["Jwt:SigningKey"]
    ?? (builder.Environment.IsDevelopment() ? "local-development-signing-key-change-me-0123456789" : null);
if (string.IsNullOrWhiteSpace(signingKey) || Encoding.UTF8.GetByteCount(signingKey) < 32)
    throw new InvalidOperationException("Set JWT_SIGNING_KEY to a secret of at least 32 bytes.");
var issuer = Environment.GetEnvironmentVariable("JWT_ISSUER") ?? "store-platform";
var audience = Environment.GetEnvironmentVariable("JWT_AUDIENCE") ?? "storefront";

var app = builder.Build();
var versionSet = app.NewApiVersionSet().HasApiVersion(new ApiVersion(1, 0)).Build();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseApiErrorHandling();
app.UseVersionedApiDocumentation();
await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<IdentityDb>();
    await db.Database.MigrateAsync();
    var adminEmail = Environment.GetEnvironmentVariable("SEED_ADMIN_EMAIL");
    var adminPassword = Environment.GetEnvironmentVariable("SEED_ADMIN_PASSWORD");
    if (!string.IsNullOrWhiteSpace(adminEmail) && !string.IsNullOrWhiteSpace(adminPassword) &&
        !await db.Users.AnyAsync(x => x.Email == adminEmail.Trim().ToLowerInvariant()))
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        db.Users.Add(new UserAccount
        {
            Email = adminEmail.Trim().ToLowerInvariant(),
            PasswordSalt = Convert.ToBase64String(salt),
            PasswordHash = HashPassword(adminPassword, salt),
            Role = "Admin"
        });
        await db.SaveChangesAsync();
    }
}

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
app.MapPost("/api/v{version:apiVersion}/register", async Task<IResult> (RegisterRequest request, IdentityDb db) =>
{
    var email = request.Email.Trim().ToLowerInvariant();
    if (email.Length is < 3 or > 254 || !email.Contains('@') || request.Password.Length < 10)
        return Results.Problem("Provide a valid email and a password of at least 10 characters.", statusCode: 400);
    if (await db.Users.AnyAsync(x => x.Email == email))
        return Results.Problem("An account already exists for this email.", statusCode: 409);

    var salt = RandomNumberGenerator.GetBytes(16);
    var user = new UserAccount
    {
        Email = email,
        PasswordSalt = Convert.ToBase64String(salt),
        PasswordHash = HashPassword(request.Password, salt),
        Role = "Customer"
    };
    db.Users.Add(user);
    await db.SaveChangesAsync();
    return Results.Created("/api/v1/register", new { user.Email, user.Role });
}).WithApiVersionSet(versionSet).MapToApiVersion(new ApiVersion(1, 0));
app.MapPost("/api/v{version:apiVersion}/login", async Task<IResult> (LoginRequest request, IdentityDb db) =>
{
    var email = request.Email.Trim().ToLowerInvariant();
    var user = await db.Users.SingleOrDefaultAsync(x => x.Email == email);
    if (user is null || !VerifyPassword(request.Password, user.PasswordSalt, user.PasswordHash))
        return Results.Problem("Invalid email or password.", statusCode: 401);
    var expires = DateTimeOffset.UtcNow.AddHours(2);
    var token = CreateToken(user, issuer, audience, signingKey, expires);
    return Results.Ok(new TokenResponse(token, expires, user.Role));
}).WithApiVersionSet(versionSet).MapToApiVersion(new ApiVersion(1, 0));
app.Run();

static string HashPassword(string password, byte[] salt) =>
    Convert.ToBase64String(Rfc2898DeriveBytes.Pbkdf2(password, salt, 310_000, HashAlgorithmName.SHA256, 32));

static bool VerifyPassword(string password, string salt, string expected) =>
    CryptographicOperations.FixedTimeEquals(
        Convert.FromBase64String(HashPassword(password, Convert.FromBase64String(salt))),
        Convert.FromBase64String(expected));

static string CreateToken(UserAccount user, string issuer, string audience, string signingKey, DateTimeOffset expires)
{
    static string Encode(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    var header = Encode(Encoding.UTF8.GetBytes("""{"alg":"HS256","typ":"JWT"}"""));
    var payload = Encode(JsonSerializer.SerializeToUtf8Bytes(new
    {
        sub = user.Id.ToString(),
        email = user.Email,
        role = user.Role,
        iss = issuer,
        aud = audience,
        nbf = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        exp = expires.ToUnixTimeSeconds()
    }));
    var unsigned = $"{header}.{payload}";
    using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(signingKey));
    return $"{unsigned}.{Encode(hmac.ComputeHash(Encoding.ASCII.GetBytes(unsigned)))}";
}

sealed class UserAccount
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Email { get; set; } = "";
    public string PasswordSalt { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string Role { get; set; } = "Customer";
}

sealed class IdentityDb(DbContextOptions<IdentityDb> options) : DbContext(options)
{
    public DbSet<UserAccount> Users => Set<UserAccount>();
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<UserAccount>().HasIndex(x => x.Email).IsUnique();
        modelBuilder.Entity<UserAccount>().Property(x => x.Email).HasMaxLength(254);
    }
}

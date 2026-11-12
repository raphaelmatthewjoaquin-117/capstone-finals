using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Ocelot.DependencyInjection;
using Ocelot.Middleware;
using Shared.Messaging;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddApiErrorHandling();
var ocelotFile = builder.Environment.IsDevelopment() ? "ocelot.Local.json" : "ocelot.Docker.json";
builder.Configuration.AddJsonFile(ocelotFile, optional: false, reloadOnChange: true);
var signingKey = Environment.GetEnvironmentVariable("JWT_SIGNING_KEY")
    ?? builder.Configuration["Jwt:SigningKey"]
    ?? (builder.Environment.IsDevelopment() ? "local-development-signing-key-change-me-0123456789" : null);
if (string.IsNullOrWhiteSpace(signingKey) || Encoding.UTF8.GetByteCount(signingKey) < 32)
    throw new InvalidOperationException("Set JWT_SIGNING_KEY to a secret of at least 32 bytes.");
var issuer = Environment.GetEnvironmentVariable("JWT_ISSUER") ?? "store-platform";
var audience = Environment.GetEnvironmentVariable("JWT_AUDIENCE") ?? "storefront";

builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
    policy.WithOrigins("http://localhost:5200").AllowAnyHeader().AllowAnyMethod()));
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidIssuer = issuer,
        ValidateAudience = true,
        ValidAudience = audience,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
        ValidateLifetime = true,
        RoleClaimType = "role",
        ClockSkew = TimeSpan.FromSeconds(30)
    };
});
builder.Services.AddAuthorization();
builder.Services.AddOcelot(builder.Configuration);

var app = builder.Build();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseApiErrorHandling();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
await app.UseOcelot();
app.Run();

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Shared.Messaging;

public static class CorrelationIdContext
{
    private static readonly AsyncLocal<string?> CurrentValue = new();

    public static string? Current => CurrentValue.Value;

    public static IDisposable Push(string correlationId)
    {
        var previous = CurrentValue.Value;
        CurrentValue.Value = correlationId;
        return new RestoreContext(previous);
    }

    private sealed class RestoreContext(string? previous) : IDisposable
    {
        public void Dispose() => CurrentValue.Value = previous;
    }
}

public sealed class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = context.Request.Headers["X-Correlation-ID"].FirstOrDefault();
        if (!Guid.TryParse(correlationId, out _)) correlationId = Guid.NewGuid().ToString();
        context.Items["CorrelationId"] = correlationId;
        context.Request.Headers["X-Correlation-ID"] = correlationId;
        context.Response.Headers["X-Correlation-ID"] = correlationId;

        using (CorrelationIdContext.Push(correlationId))
        using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
            await next(context);
    }
}

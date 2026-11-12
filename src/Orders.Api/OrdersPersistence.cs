using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Shared.Contracts;
using Shared.Messaging;

sealed class Order
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProductId { get; set; }
    public int Quantity { get; set; }
    public decimal Total { get; set; }
    public string Status { get; set; } = "Pending";
    public List<OrderStatus> History { get; set; } = [];

    public void AddStatus(string status, string detail)
    {
        Status = status;
        History.Add(new OrderStatus { Status = status, At = DateTimeOffset.UtcNow, Detail = detail });
    }
}

sealed class OrderStatus
{
    public int Id { get; set; }
    public Guid OrderId { get; set; }
    public string Status { get; set; } = "";
    public DateTimeOffset At { get; set; }
    public string? Detail { get; set; }
}

sealed class OutboxMessage
{
    public long Id { get; set; }
    public Guid EventId { get; set; }
    public string EventType { get; set; } = "";
    public string Payload { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }

    public static OutboxMessage From(IIntegrationEvent message) => new()
    {
        EventId = message.EventId,
        EventType = message.GetType().Name,
        Payload = JsonSerializer.Serialize(message, message.GetType()),
        CreatedAt = message.OccurredAt
    };
}

sealed class ProcessedMessage
{
    public Guid EventId { get; set; }
    public DateTimeOffset ProcessedAt { get; set; }
}

sealed class OrdersDb(DbContextOptions<OrdersDb> options) : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderStatus> StatusHistory => Set<OrderStatus>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<ProcessedMessage> ProcessedMessages => Set<ProcessedMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OutboxMessage>().HasIndex(message => message.EventId).IsUnique();
        modelBuilder.Entity<ProcessedMessage>().HasKey(message => message.EventId);
    }
}

sealed class OrdersProcessedMessageStore(IServiceScopeFactory scopeFactory) : IProcessedMessageStore
{
    public async Task<bool> IsProcessedAsync(Guid eventId, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrdersDb>();
        return await db.ProcessedMessages.AnyAsync(message => message.EventId == eventId, cancellationToken);
    }

    public async Task RecordProcessedAsync(Guid eventId, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrdersDb>();
        db.ProcessedMessages.Add(new ProcessedMessage { EventId = eventId, ProcessedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(cancellationToken);
    }
}

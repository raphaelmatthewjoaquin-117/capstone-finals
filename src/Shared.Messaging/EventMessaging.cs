using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Shared.Contracts;

namespace Shared.Messaging;

public interface IEventPublisher
{
    Task PublishAsync<TEvent>(TEvent message, CancellationToken cancellationToken = default)
        where TEvent : IIntegrationEvent;
}

public interface IProcessedMessageStore
{
    Task<bool> IsProcessedAsync(Guid eventId, CancellationToken cancellationToken);
    Task RecordProcessedAsync(Guid eventId, CancellationToken cancellationToken);
}

public abstract class RabbitMqConsumer<TEvent>(
    IConfiguration configuration,
    IProcessedMessageStore processedMessages,
    ILogger logger) : BackgroundService
    where TEvent : class, IIntegrationEvent
{
    private const string Exchange = "store.events";

    protected abstract Task HandleAsync(TEvent message, CancellationToken cancellationToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var factory = RabbitMqConfiguration.CreateFactory(configuration);
        await using var connection = await factory.CreateConnectionAsync(stoppingToken);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);
        var queue = $"{typeof(TEvent).Name.ToLowerInvariant()}.queue";
        var deadLetterExchange = "store.events.dlx";

        await channel.ExchangeDeclareAsync(Exchange, ExchangeType.Topic, durable: true, cancellationToken: stoppingToken);
        await channel.ExchangeDeclareAsync(deadLetterExchange, ExchangeType.Direct, durable: true, cancellationToken: stoppingToken);
        await channel.QueueDeclareAsync($"{queue}.dlq", durable: true, exclusive: false, autoDelete: false, arguments: null, cancellationToken: stoppingToken);
        await channel.QueueBindAsync($"{queue}.dlq", deadLetterExchange, $"{queue}.dlq", cancellationToken: stoppingToken);
        await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                ["x-dead-letter-exchange"] = deadLetterExchange,
                ["x-dead-letter-routing-key"] = $"{queue}.dlq"
            }, cancellationToken: stoppingToken);
        await channel.QueueBindAsync(queue, Exchange, typeof(TEvent).Name, cancellationToken: stoppingToken);
        await channel.BasicQosAsync(0, 1, global: false, cancellationToken: stoppingToken);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, delivery) =>
        {
            try
            {
                var message = JsonSerializer.Deserialize<TEvent>(delivery.Body.Span)
                    ?? throw new InvalidOperationException($"Could not deserialize {typeof(TEvent).Name}.");
                using (CorrelationIdContext.Push(message.CorrelationId.ToString()))
                using (logger.BeginScope(new Dictionary<string, object>
                {
                    ["CorrelationId"] = message.CorrelationId,
                    ["EventId"] = message.EventId
                }))
                {
                    if (!await processedMessages.IsProcessedAsync(message.EventId, stoppingToken))
                    {
                        await HandleAsync(message, stoppingToken);
                        await processedMessages.RecordProcessedAsync(message.EventId, stoppingToken);
                    }
                }
                await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, cancellationToken: stoppingToken);
            }
            catch (Exception exception)
            {
                var correlationId = delivery.BasicProperties.CorrelationId ?? "unknown";
                using (CorrelationIdContext.Push(correlationId))
                using (logger.BeginScope(new Dictionary<string, object>
                {
                    ["CorrelationId"] = correlationId,
                    ["EventId"] = delivery.BasicProperties.MessageId ?? "unknown"
                }))
                    logger.LogError(exception, "Failed handling {EventType}; dead-lettering delivery {DeliveryTag}.",
                        typeof(TEvent).Name, delivery.DeliveryTag);
                await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: false, cancellationToken: stoppingToken);
            }
        };
        await channel.BasicConsumeAsync(queue, autoAck: false, consumerTag: "", noLocal: false, exclusive: false,
            arguments: null, consumer: consumer, cancellationToken: stoppingToken);

        await Task.Delay(Timeout.Infinite, stoppingToken);
    }
}

public sealed class RabbitMqEventPublisher(IConfiguration configuration, ILogger<RabbitMqEventPublisher> logger)
    : IEventPublisher, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IConnection? _connection;
    private IChannel? _channel;
    private const string Exchange = "store.events";

    public async Task PublishAsync<TEvent>(TEvent message, CancellationToken cancellationToken = default)
        where TEvent : IIntegrationEvent
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_channel is null)
            {
                var factory = RabbitMqConfiguration.CreateFactory(configuration);
                _connection = await factory.CreateConnectionAsync(cancellationToken);
                _channel = await _connection.CreateChannelAsync(
                    new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true),
                    cancellationToken);
                await _channel.ExchangeDeclareAsync(Exchange, ExchangeType.Topic, durable: true, cancellationToken: cancellationToken);
            }

            var queue = $"{typeof(TEvent).Name.ToLowerInvariant()}.queue";
            const string deadLetterExchange = "store.events.dlx";
            await _channel.ExchangeDeclareAsync(deadLetterExchange, ExchangeType.Direct, durable: true, cancellationToken: cancellationToken);
            await _channel.QueueDeclareAsync($"{queue}.dlq", durable: true, exclusive: false, autoDelete: false,
                arguments: null, cancellationToken: cancellationToken);
            await _channel.QueueBindAsync($"{queue}.dlq", deadLetterExchange, $"{queue}.dlq", cancellationToken: cancellationToken);
            await _channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false,
                arguments: new Dictionary<string, object?>
                {
                    ["x-dead-letter-exchange"] = deadLetterExchange,
                    ["x-dead-letter-routing-key"] = $"{queue}.dlq"
                }, cancellationToken: cancellationToken);
            await _channel.QueueBindAsync(queue, Exchange, typeof(TEvent).Name, cancellationToken: cancellationToken);

            var properties = new BasicProperties
            {
                Persistent = true,
                ContentType = "application/json",
                MessageId = message.EventId.ToString(),
                CorrelationId = message.CorrelationId.ToString(),
                Headers = new Dictionary<string, object?>
                {
                    ["X-Correlation-ID"] = message.CorrelationId.ToString()
                }
            };
            await _channel.BasicPublishAsync(Exchange, typeof(TEvent).Name, mandatory: true, properties,
                JsonSerializer.SerializeToUtf8Bytes(message), cancellationToken);
            using (CorrelationIdContext.Push(message.CorrelationId.ToString()))
            using (logger.BeginScope(new Dictionary<string, object>
            {
                ["CorrelationId"] = message.CorrelationId,
                ["EventId"] = message.EventId
            }))
                logger.LogDebug("Published {EventType}.", typeof(TEvent).Name);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_channel is not null) await _channel.DisposeAsync();
        if (_connection is not null) await _connection.DisposeAsync();
        _gate.Dispose();
    }
}

public sealed class DisabledEventPublisher(ILogger<DisabledEventPublisher> logger) : IEventPublisher
{
    public Task PublishAsync<TEvent>(TEvent message, CancellationToken cancellationToken = default)
        where TEvent : IIntegrationEvent
    {
        logger.LogInformation("Messaging is disabled; {EventType} {EventId} was not published.",
            typeof(TEvent).Name, message.EventId);
        return Task.CompletedTask;
    }
}

public static class EventMessagingExtensions
{
    public static IServiceCollection AddEventMessaging(this IServiceCollection services, IConfiguration configuration)
    {
        if (configuration.GetValue<bool>("Messaging:Enabled"))
            services.AddSingleton<IEventPublisher, RabbitMqEventPublisher>();
        else
            services.AddSingleton<IEventPublisher, DisabledEventPublisher>();
        return services;
    }
}

internal static class RabbitMqConfiguration
{
    public static ConnectionFactory CreateFactory(IConfiguration configuration) => new()
    {
        HostName = Environment.GetEnvironmentVariable("RABBITMQ_HOST") ?? configuration["RabbitMQ:Host"] ?? "localhost",
        Port = int.TryParse(Environment.GetEnvironmentVariable("RABBITMQ_PORT") ?? configuration["RabbitMQ:Port"], out var port)
            ? port : AmqpTcpEndpoint.UseDefaultPort,
        UserName = Environment.GetEnvironmentVariable("RABBITMQ_USER") ?? configuration["RabbitMQ:User"] ?? "guest",
        Password = Environment.GetEnvironmentVariable("RABBITMQ_PASSWORD") ?? configuration["RabbitMQ:Password"] ?? "guest",
        VirtualHost = Environment.GetEnvironmentVariable("RABBITMQ_VHOST") ?? configuration["RabbitMQ:VirtualHost"] ?? "/"
    };
}

using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using Reservoir.BuildingBlocks.Serialization;

namespace Reservoir.BuildingBlocks.Messaging;

/// <summary>
/// Single-channel topic-exchange publisher with publisher confirms.
/// Designed to be registered as a singleton; the channel is not thread-safe so
/// publishes are serialised through an internal lock.
///
/// Declares <c>RabbitMqOptions.Exchange</c> (topic, durable) on startup — an
/// idempotent call, so it is safe for every service to declare it. Dead-letter
/// exchanges are a consumer concern (each service owns its own
/// <c>&lt;service&gt;.dlx</c>) and are declared by the consumers, not here.
/// </summary>
public sealed class RabbitMqEventPublisher : IEventPublisher, IDisposable
{
    private readonly RabbitMqOptions _options;
    private readonly ILogger<RabbitMqEventPublisher> _logger;
    private readonly IConnection _connection;
    private readonly IModel _channel;
    private readonly object _gate = new();

    public RabbitMqEventPublisher(IOptions<RabbitMqOptions> options, ILogger<RabbitMqEventPublisher> logger)
    {
        _options = options.Value;
        _logger = logger;

        var factory = new ConnectionFactory
        {
            HostName = _options.HostName,
            Port = _options.Port,
            UserName = _options.UserName,
            Password = _options.Password,
            VirtualHost = _options.VirtualHost,
            AutomaticRecoveryEnabled = true,
            DispatchConsumersAsync = false,
        };

        _connection = factory.CreateConnection(_options.PublisherClientName);
        _channel = _connection.CreateModel();

        _channel.ExchangeDeclare(_options.Exchange, ExchangeType.Topic, durable: true, autoDelete: false);

        _channel.ConfirmSelect();

        _logger.LogInformation(
            "RabbitMQ publisher '{Client}' connected to {Host}:{Port}, exchange {Exchange}",
            _options.PublisherClientName, _options.HostName, _options.Port, _options.Exchange);
    }

    public void Publish<TEvent>(string routingKey, TEvent payload, Guid messageId, DateTimeOffset occurredAt)
        where TEvent : class
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(payload, EventJsonOptions.Web);

        lock (_gate)
        {
            var props = _channel.CreateBasicProperties();
            props.ContentType = "application/json";
            props.ContentEncoding = "utf-8";
            props.DeliveryMode = 2;
            props.MessageId = messageId.ToString();
            props.Timestamp = new AmqpTimestamp(occurredAt.ToUnixTimeSeconds());
            props.Type = routingKey;

            _channel.BasicPublish(
                exchange: _options.Exchange,
                routingKey: routingKey,
                mandatory: false,
                basicProperties: props,
                body: body);

            _channel.WaitForConfirmsOrDie(TimeSpan.FromSeconds(5));
        }

        _logger.LogInformation(
            "Published {RoutingKey} eventId={EventId} bytes={Bytes}",
            routingKey, messageId, body.Length);
    }

    public void Dispose()
    {
        try { _channel?.Close(); } catch { }
        try { _connection?.Close(); } catch { }
        _channel?.Dispose();
        _connection?.Dispose();
    }
}

using DashboardApi.Handlers;
using DashboardApi.Hubs;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Reservoir.BuildingBlocks.Messaging;

namespace DashboardApi.Consumer;

/// <summary>
/// Single-queue fan-in consumer for every status-bearing routing key. Each
/// inbound message is translated by <see cref="StatusTranslator"/> and pushed
/// to connected SignalR clients via <see cref="IOrderStatusBroadcaster"/>.
///
/// Broadcast failures are logged and acked. No retry queues, no DLQ — see
/// ADR-007 for the rationale.
/// </summary>
public sealed class StatusEventsConsumer : BackgroundService
{
    private readonly RabbitMqOptions _broker;
    private readonly DashboardConsumerOptions _consumer;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<StatusEventsConsumer> _logger;

    private IConnection? _connection;
    private IModel? _channel;
    private string? _consumerTag;

    public StatusEventsConsumer(
        IOptions<RabbitMqOptions> broker,
        IOptions<DashboardConsumerOptions> consumer,
        IServiceScopeFactory scopeFactory,
        ILogger<StatusEventsConsumer> logger)
    {
        _broker = broker.Value;
        _consumer = consumer.Value;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var factory = new ConnectionFactory
        {
            HostName = _broker.HostName,
            Port = _broker.Port,
            UserName = _broker.UserName,
            Password = _broker.Password,
            VirtualHost = _broker.VirtualHost,
            AutomaticRecoveryEnabled = true,
            DispatchConsumersAsync = true,
        };

        _connection = factory.CreateConnection("dashboard-api-consumer");
        _channel = _connection.CreateModel();

        DeclareTopology(_channel);

        _channel.BasicQos(prefetchSize: 0, prefetchCount: _consumer.PrefetchCount, global: false);

        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.Received += OnMessageReceivedAsync;
        _consumerTag = _channel.BasicConsume(queue: _consumer.QueueName, autoAck: false, consumer);

        _logger.LogInformation(
            "StatusEventsConsumer started on queue '{Queue}' (bound to {Count} routing keys, prefetch={Prefetch})",
            _consumer.QueueName, _consumer.SubscribesTo.Count, _consumer.PrefetchCount);

        stoppingToken.Register(() => _logger.LogInformation("StatusEventsConsumer stopping"));
        return Task.CompletedTask;
    }

    private async Task OnMessageReceivedAsync(object? sender, BasicDeliverEventArgs ea)
    {
        var routingKey = ea.RoutingKey;
        var messageId = ea.BasicProperties?.MessageId ?? "(none)";

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var translator = scope.ServiceProvider.GetRequiredService<StatusTranslator>();
            var broadcaster = scope.ServiceProvider.GetRequiredService<IOrderStatusBroadcaster>();

            var notification = translator.Translate(routingKey, ea.Body.ToArray());
            if (notification is null)
            {
                _logger.LogWarning(
                    "No translator for routing key '{RoutingKey}' (messageId={MessageId}); acking and skipping",
                    routingKey, messageId);
            }
            else
            {
                await broadcaster.BroadcastAsync(notification, CancellationToken.None);
                _logger.LogInformation(
                    "Broadcast {Status} for order {OrderId} from {RoutingKey}",
                    notification.Status, notification.OrderId, routingKey);
            }

            _channel!.BasicAck(ea.DeliveryTag, multiple: false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to broadcast status for messageId={MessageId} on '{RoutingKey}'; acking anyway " +
                "(broadcast failures aren't recoverable — next event will refresh the order)",
                messageId, routingKey);
            try { _channel!.BasicAck(ea.DeliveryTag, multiple: false); } catch { }
        }
    }

    private void DeclareTopology(IModel channel)
    {
        channel.ExchangeDeclare(_broker.Exchange, ExchangeType.Topic, durable: true, autoDelete: false);

        channel.QueueDeclare(_consumer.QueueName, durable: true, exclusive: false, autoDelete: false);

        foreach (var routingKey in _consumer.SubscribesTo)
        {
            channel.QueueBind(_consumer.QueueName, _broker.Exchange, routingKey);
        }

        _logger.LogInformation(
            "Topology declared: queue {Queue} bound to {Exchange} for {Keys}",
            _consumer.QueueName, _broker.Exchange, string.Join(", ", _consumer.SubscribesTo));
    }

    public override void Dispose()
    {
        try
        {
            if (_channel is { IsOpen: true } && _consumerTag is not null)
                _channel.BasicCancel(_consumerTag);
        }
        catch { }
        try { _channel?.Close(); } catch { }
        try { _connection?.Close(); } catch { }
        _channel?.Dispose();
        _connection?.Dispose();
        base.Dispose();
    }
}

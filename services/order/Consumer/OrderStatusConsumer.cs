using Microsoft.Extensions.Options;
using OrderService.Handlers;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Reservoir.BuildingBlocks.Messaging;

namespace OrderService.Consumer;

/// <summary>
/// Multi-binding fan-in consumer that mirrors the dashboard-api's
/// <c>StatusEventsConsumer</c>, but persists the new status to the
/// <c>orders.orders</c> row instead of broadcasting over SignalR.
///
/// Idempotency is enforced inside <see cref="OrderStatusEventHandler"/> via
/// <c>orders.processed_event_ids</c>; duplicate events are acked and skipped.
/// Persistent failures (DB down, malformed payload) are nack'd to
/// <c>orders.dlx</c> -> <c>order.status.dlq</c>.
/// </summary>
public sealed class OrderStatusConsumer : BackgroundService
{
    private readonly RabbitMqOptions _broker;
    private readonly OrderStatusConsumerOptions _consumer;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OrderStatusConsumer> _logger;

    private IConnection? _connection;
    private IModel? _channel;
    private string? _consumerTag;

    public OrderStatusConsumer(
        IOptions<RabbitMqOptions> broker,
        IOptions<OrderStatusConsumerOptions> consumer,
        IServiceScopeFactory scopeFactory,
        ILogger<OrderStatusConsumer> logger)
    {
        _broker = broker.Value;
        _consumer = consumer.Value;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
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

        _connection = await OpenWithRetryAsync(factory, "order-service-consumer", stoppingToken);
        _channel = _connection.CreateModel();

        DeclareTopology(_channel);

        _channel.BasicQos(prefetchSize: 0, prefetchCount: _consumer.PrefetchCount, global: false);

        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.Received += OnMessageReceivedAsync;
        _consumerTag = _channel.BasicConsume(queue: _consumer.QueueName, autoAck: false, consumer);

        _logger.LogInformation(
            "OrderStatusConsumer started on queue '{Queue}' (bound to {Count} routing keys, prefetch={Prefetch})",
            _consumer.QueueName, _consumer.SubscribesTo.Count, _consumer.PrefetchCount);

        stoppingToken.Register(() => _logger.LogInformation("OrderStatusConsumer stopping"));
    }

    private async Task<IConnection> OpenWithRetryAsync(ConnectionFactory factory, string clientName, CancellationToken ct)
    {
        const int maxAttempts = 10;
        var delay = TimeSpan.FromSeconds(2);
        for (var attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return factory.CreateConnection(clientName);
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                _logger.LogWarning(
                    "RabbitMQ not yet reachable for {ClientName} (attempt {Attempt}/{Max}): {Message}. Retrying in {Delay}s…",
                    clientName, attempt, maxAttempts, ex.Message, delay.TotalSeconds);
                await Task.Delay(delay, ct);
            }
        }
    }

    private async Task OnMessageReceivedAsync(object? sender, BasicDeliverEventArgs ea)
    {
        var routingKey = ea.RoutingKey;
        var messageId = ea.BasicProperties?.MessageId ?? "(none)";

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var handler = scope.ServiceProvider.GetRequiredService<OrderStatusEventHandler>();

            await handler.HandleAsync(routingKey, ea.Body.ToArray(), CancellationToken.None);

            _channel!.BasicAck(ea.DeliveryTag, multiple: false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to process status event {MessageId} on '{RoutingKey}'; nacking to DLX",
                messageId, routingKey);
            try { _channel!.BasicNack(ea.DeliveryTag, multiple: false, requeue: false); }
            catch (Exception nackEx) { _logger.LogError(nackEx, "Failed to nack message {MessageId}", messageId); }
        }
    }

    private void DeclareTopology(IModel channel)
    {
        channel.ExchangeDeclare(_broker.Exchange, ExchangeType.Topic, durable: true, autoDelete: false);
        channel.ExchangeDeclare(_broker.DeadLetterExchange, ExchangeType.Fanout, durable: true, autoDelete: false);

        var primaryArgs = new Dictionary<string, object>
        {
            { "x-dead-letter-exchange", _broker.DeadLetterExchange },
        };

        channel.QueueDeclare(_consumer.QueueName, durable: true, exclusive: false, autoDelete: false, arguments: primaryArgs);

        foreach (var routingKey in _consumer.SubscribesTo)
        {
            channel.QueueBind(_consumer.QueueName, _broker.Exchange, routingKey);
        }

        channel.QueueDeclare(_consumer.DeadLetterQueue, durable: true, exclusive: false, autoDelete: false);
        channel.QueueBind(_consumer.DeadLetterQueue, _broker.DeadLetterExchange, routingKey: string.Empty);

        _logger.LogInformation(
            "Topology declared: queue {Queue} bound to {Exchange} for {Keys}; DLQ {Dlq} bound to {Dlx}",
            _consumer.QueueName, _broker.Exchange, string.Join(", ", _consumer.SubscribesTo),
            _consumer.DeadLetterQueue, _broker.DeadLetterExchange);
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

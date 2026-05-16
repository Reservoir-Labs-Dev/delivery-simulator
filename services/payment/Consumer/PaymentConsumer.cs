using Microsoft.Extensions.Options;
using PaymentService.Handlers;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Reservoir.BuildingBlocks.Messaging;

namespace PaymentService.Consumer;

public sealed class PaymentConsumer : BackgroundService
{
    private readonly RabbitMqOptions _broker;
    private readonly PaymentConsumerOptions _consumer;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PaymentConsumer> _logger;

    private IConnection? _connection;
    private IModel? _channel;
    private string? _consumerTag;

    public PaymentConsumer(
        IOptions<RabbitMqOptions> broker,
        IOptions<PaymentConsumerOptions> consumer,
        IServiceScopeFactory scopeFactory,
        ILogger<PaymentConsumer> logger)
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

        _connection = factory.CreateConnection("payment-service-consumer");
        _channel = _connection.CreateModel();

        DeclareTopology(_channel);

        _channel.BasicQos(prefetchSize: 0, prefetchCount: _consumer.PrefetchCount, global: false);

        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.Received += OnMessageReceivedAsync;
        _consumerTag = _channel.BasicConsume(queue: _consumer.QueueName, autoAck: false, consumer);

        _logger.LogInformation(
            "PaymentConsumer started on queue '{Queue}' (routingKey '{RoutingKey}', prefetch={Prefetch})",
            _consumer.QueueName, _consumer.ConsumesRoutingKey, _consumer.PrefetchCount);

        stoppingToken.Register(() => _logger.LogInformation("PaymentConsumer stopping"));
        return Task.CompletedTask;
    }

    private async Task OnMessageReceivedAsync(object? sender, BasicDeliverEventArgs ea)
    {
        var messageId = ea.BasicProperties?.MessageId ?? "(none)";
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var handler = scope.ServiceProvider.GetRequiredService<OrderCreatedHandler>();

            var body = ea.Body.ToArray();
            await handler.HandleAsync(body, ea.BasicProperties, CancellationToken.None);

            _channel!.BasicAck(ea.DeliveryTag, multiple: false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to process message {MessageId} on '{RoutingKey}'; nacking to DLX",
                messageId, ea.RoutingKey);
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
        channel.QueueBind(_consumer.QueueName, _broker.Exchange, _consumer.ConsumesRoutingKey);

        DeclareRetryQueue(channel, "payment.retry.1", 1_000);
        DeclareRetryQueue(channel, "payment.retry.2", 2_000);
        DeclareRetryQueue(channel, "payment.retry.3", 4_000);

        channel.QueueDeclare(_consumer.DeadLetterQueue, durable: true, exclusive: false, autoDelete: false);
        channel.QueueBind(_consumer.DeadLetterQueue, _broker.DeadLetterExchange, routingKey: string.Empty);

        _logger.LogInformation(
            "Topology declared: queue {Queue} bound to {Exchange}/{RoutingKey}, DLQ {Dlq} bound to {Dlx}",
            _consumer.QueueName, _broker.Exchange, _consumer.ConsumesRoutingKey,
            _consumer.DeadLetterQueue, _broker.DeadLetterExchange);
    }

    private void DeclareRetryQueue(IModel channel, string queueName, int ttlMs)
    {
        var args = new Dictionary<string, object>
        {
            { "x-message-ttl", ttlMs },
            { "x-dead-letter-exchange", _broker.Exchange },
            { "x-dead-letter-routing-key", _consumer.ConsumesRoutingKey },
        };
        channel.QueueDeclare(queueName, durable: true, exclusive: false, autoDelete: false, arguments: args);
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

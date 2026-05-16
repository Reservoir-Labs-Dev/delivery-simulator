using System.Collections.Concurrent;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Reservoir.BuildingBlocks.Messaging;

namespace Reservoir.TestSupport;

/// <summary>
/// Helper for integration tests: opens an exclusive, auto-delete queue bound to a
/// topic exchange + routing key, captures every delivered message, and lets the
/// test pull them off synchronously with <see cref="WaitForOne"/>.
/// </summary>
public sealed class RabbitMqVerifierQueue : IDisposable
{
    private readonly IConnection _connection;
    private readonly IModel _channel;
    private readonly string _queueName;
    private readonly BlockingCollection<(IBasicProperties Props, byte[] Body)> _received = new();

    public string QueueName => _queueName;

    private RabbitMqVerifierQueue(IConnection connection, IModel channel, string queueName)
    {
        _connection = connection;
        _channel = channel;
        _queueName = queueName;

        var consumer = new EventingBasicConsumer(_channel);
        consumer.Received += (_, ea) =>
        {
            _received.Add((ea.BasicProperties, ea.Body.ToArray()));
            _channel.BasicAck(ea.DeliveryTag, multiple: false);
        };
        _channel.BasicConsume(_queueName, autoAck: false, consumer);
    }

    public static RabbitMqVerifierQueue Open(
        RabbitMqOptions options,
        string routingKey,
        string clientName = "test-verifier")
    {
        var factory = new ConnectionFactory
        {
            HostName = options.HostName,
            Port = options.Port,
            UserName = options.UserName,
            Password = options.Password,
            VirtualHost = options.VirtualHost,
            ClientProvidedName = clientName,
        };

        var connection = factory.CreateConnection();
        var channel = connection.CreateModel();
        channel.ExchangeDeclare(options.Exchange, ExchangeType.Topic, durable: true, autoDelete: false);

        var queue = channel.QueueDeclare(
            queue: string.Empty,
            durable: false,
            exclusive: true,
            autoDelete: true).QueueName;

        channel.QueueBind(queue, options.Exchange, routingKey);

        return new RabbitMqVerifierQueue(connection, channel, queue);
    }

    public (IBasicProperties Props, byte[] Body) WaitForOne(TimeSpan timeout)
    {
        if (!_received.TryTake(out var item, timeout))
            throw new TimeoutException($"No message received on {_queueName} within {timeout}.");
        return item;
    }

    public void Dispose()
    {
        try { _channel?.Close(); } catch { }
        try { _connection?.Close(); } catch { }
        _channel?.Dispose();
        _connection?.Dispose();
        _received.Dispose();
    }
}

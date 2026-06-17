using DeliveryService.Consumer;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using Reservoir.BuildingBlocks.Messaging;

namespace DeliveryService.Recovery;

/// <summary>
/// DOG-134 recovery use case. Opens a short-lived broker connection and drains
/// <c>delivery.dlq</c> back onto <c>orders.exchange</c>/<c>order.ready</c> via the
/// shared <see cref="DlqReplayer"/>, so a now-healed consumer reprocesses each
/// preserved message. Lives outside <c>Program.cs</c> (per the service's
/// route-maps-in-Program, logic-in-a-handler convention) so the broker wiring is
/// isolated and the endpoint stays a one-line delegation.
/// </summary>
public sealed class DlqReplayHandler
{
    private readonly RabbitMqOptions _broker;
    private readonly DeliveryConsumerOptions _consumer;
    private readonly ILogger<DlqReplayHandler> _logger;

    public DlqReplayHandler(
        IOptions<RabbitMqOptions> broker,
        IOptions<DeliveryConsumerOptions> consumer,
        ILogger<DlqReplayHandler> logger)
    {
        _broker = broker.Value;
        _consumer = consumer.Value;
        _logger = logger;
    }

    /// <summary>The dead-letter queue this handler drains.</summary>
    public string DeadLetterQueue => _consumer.DeadLetterQueue;

    /// <summary>
    /// Drains the delivery DLQ once and returns the number of messages replayed.
    /// Idempotent and zero-loss — see <see cref="DlqReplayer"/>.
    /// </summary>
    public int Replay()
    {
        var factory = new ConnectionFactory
        {
            HostName = _broker.HostName,
            Port = _broker.Port,
            UserName = _broker.UserName,
            Password = _broker.Password,
            VirtualHost = _broker.VirtualHost,
        };

        using var connection = factory.CreateConnection("delivery-dlq-replayer");
        using var channel = connection.CreateModel();

        return DlqReplayer.Drain(
            channel,
            _consumer.DeadLetterQueue,
            _broker.Exchange,
            _consumer.ConsumesRoutingKey,
            _logger);
    }
}

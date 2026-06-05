namespace Reservoir.BuildingBlocks.Messaging;

/// <summary>
/// Shared RabbitMQ connection + topic exchange settings used by every service's
/// publisher. Consumers add their own consumer-specific options on top
/// (queue name, prefetch, routing key to subscribe to).
/// </summary>
public class RabbitMqOptions
{
    public const string SectionName = "RabbitMq";

    public string HostName { get; set; } = "localhost";
    public int Port { get; set; } = 5672;
    public string UserName { get; set; } = "reservoir";
    public string Password { get; set; } = "reservoir";
    public string VirtualHost { get; set; } = "/";

    public string Exchange { get; set; } = "orders.exchange";

    /// <summary>
    /// The service's own dead-letter exchange (<c>&lt;service&gt;.dlx</c>), declared and
    /// used only by that service's consumer. Each service sets this to its own value
    /// (e.g. "payment.dlx") so a dead-lettered message lands only in that service's DLQ,
    /// never in another service's. The publisher does not use or declare it.
    /// </summary>
    public string DeadLetterExchange { get; set; } = "orders.dlx";

    /// <summary>
    /// Name reported to RabbitMQ as <c>ClientProvidedName</c> for the publisher connection.
    /// Useful when inspecting the broker UI to know which service opened a connection.
    /// Each service should set this to its own name (e.g. "order-service-publisher").
    /// </summary>
    public string PublisherClientName { get; set; } = "publisher";
}

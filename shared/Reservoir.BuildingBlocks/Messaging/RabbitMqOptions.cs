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
    public string DeadLetterExchange { get; set; } = "orders.dlx";

    /// <summary>
    /// Name reported to RabbitMQ as <c>ClientProvidedName</c> for the publisher connection.
    /// Useful when inspecting the broker UI to know which service opened a connection.
    /// Each service should set this to its own name (e.g. "order-service-publisher").
    /// </summary>
    public string PublisherClientName { get; set; } = "publisher";
}

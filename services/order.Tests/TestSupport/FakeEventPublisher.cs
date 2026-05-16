using OrderService.Messaging;

namespace OrderService.Tests.TestSupport;

internal sealed class FakeEventPublisher : IEventPublisher
{
    public List<PublishedEvent> Published { get; } = new();

    public void Publish<TEvent>(string routingKey, TEvent payload, Guid messageId, DateTimeOffset occurredAt)
        where TEvent : class
    {
        Published.Add(new PublishedEvent(routingKey, payload, messageId, occurredAt));
    }

    public record PublishedEvent(string RoutingKey, object Payload, Guid MessageId, DateTimeOffset OccurredAt);
}

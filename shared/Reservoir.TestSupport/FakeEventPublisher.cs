using Reservoir.BuildingBlocks.Messaging;

namespace Reservoir.TestSupport;

public sealed class FakeEventPublisher : IEventPublisher
{
    public List<PublishedEvent> Published { get; } = new();

    public void Publish<TEvent>(string routingKey, TEvent payload, Guid messageId, DateTimeOffset occurredAt)
        where TEvent : class
    {
        Published.Add(new PublishedEvent(routingKey, payload, messageId, occurredAt));
    }

    public sealed record PublishedEvent(string RoutingKey, object Payload, Guid MessageId, DateTimeOffset OccurredAt);
}

namespace Reservoir.BuildingBlocks.Messaging;

public interface IEventPublisher
{
    void Publish<TEvent>(string routingKey, TEvent payload, Guid messageId, DateTimeOffset occurredAt)
        where TEvent : class;
}

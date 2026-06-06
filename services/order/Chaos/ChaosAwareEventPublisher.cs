using System.Text.Json;
using Reservoir.BuildingBlocks.Contracts;
using Reservoir.BuildingBlocks.Messaging;
using Reservoir.BuildingBlocks.Persistence;

namespace OrderService.Chaos;

/// <summary>
/// Decorator over <see cref="IEventPublisher"/> that implements the DOG-46
/// "duplicate_events" chaos scenario. On every <c>order.created</c> publish,
/// reads the <c>duplicate_events</c> row from <c>chaos.chaos_config</c>; when
/// <c>enabled = true</c>, republishes the same payload (with the SAME
/// <c>messageId</c>) <c>params.count</c> times (default 3) so downstream
/// consumers' idempotency guards (ProcessedEventId) get exercised.
/// Other routing keys pass through untouched.
/// </summary>
/// <remarks>
/// <see cref="IEventPublisher.Publish"/> is synchronous, so the async chaos
/// reader is unwrapped with <c>GetAwaiter().GetResult()</c>. The reader is a
/// single <c>AsNoTracking</c> projection — cheap, and order creation is not a
/// hot path — but we still guard the chaos check so a transient reader fault
/// can never block a real publish.
/// </remarks>
public sealed class ChaosAwareEventPublisher : IEventPublisher
{
    /// <summary>Fallback duplicate count when the chaos row enables the
    /// scenario but omits <c>count</c>. Matches the DOG-46 plan text.</summary>
    public const int DefaultDuplicateCount = 3;

    private readonly IEventPublisher _inner;
    private readonly IChaosConfigReader _chaos;
    private readonly ILogger<ChaosAwareEventPublisher> _logger;

    public ChaosAwareEventPublisher(
        IEventPublisher inner,
        IChaosConfigReader chaos,
        ILogger<ChaosAwareEventPublisher> logger)
    {
        _inner = inner;
        _chaos = chaos;
        _logger = logger;
    }

    public void Publish<TEvent>(string routingKey, TEvent payload, Guid messageId, DateTimeOffset occurredAt)
        where TEvent : class
    {
        if (routingKey != RoutingKeys.OrderCreated)
        {
            _inner.Publish(routingKey, payload, messageId, occurredAt);
            return;
        }

        var count = ResolveDuplicateCount();
        if (count <= 1)
        {
            _inner.Publish(routingKey, payload, messageId, occurredAt);
            return;
        }

        _logger.LogWarning(
            "Chaos [{Scenario}] active: publishing {Count} copies of {RoutingKey} message {MessageId}.",
            ChaosScenarios.DuplicateEvents, count, routingKey, messageId);

        for (var i = 0; i < count; i++)
        {
            _inner.Publish(routingKey, payload, messageId, occurredAt);
        }
    }

    /// <summary>
    /// Returns the resolved duplicate count: 1 (publish once) when the chaos
    /// row is missing/disabled or the reader throws, or the configured count
    /// (clamped &gt;= 1) when the scenario is enabled.
    /// </summary>
    private int ResolveDuplicateCount()
    {
        ChaosConfigSnapshot? snapshot;
        try
        {
            snapshot = _chaos.GetAsync(ChaosScenarios.DuplicateEvents, CancellationToken.None)
                .GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Chaos [{Scenario}] reader failed; defaulting to single publish.",
                ChaosScenarios.DuplicateEvents);
            return 1;
        }

        if (snapshot is not { Enabled: true })
        {
            return 1;
        }

        var count = ParseCount(snapshot.ParamsJson);
        return count < 1 ? 1 : count;
    }

    private static int ParseCount(string paramsJson)
    {
        if (string.IsNullOrWhiteSpace(paramsJson))
            return DefaultDuplicateCount;

        try
        {
            using var doc = JsonDocument.Parse(paramsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return DefaultDuplicateCount;

            if (!doc.RootElement.TryGetProperty("count", out var prop))
                return DefaultDuplicateCount;

            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var v) && v >= 1)
                return v;

            return DefaultDuplicateCount;
        }
        catch (JsonException)
        {
            return DefaultDuplicateCount;
        }
    }
}

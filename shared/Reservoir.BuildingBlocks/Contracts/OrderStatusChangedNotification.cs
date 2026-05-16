namespace Reservoir.BuildingBlocks.Contracts;

/// <summary>
/// The SignalR notification the dashboard hub pushes to connected frontend
/// clients on every order state transition. Shape matches ARCH-002 § 4.
///
/// This does <b>not</b> travel over RabbitMQ — it is produced by
/// dashboard-api as the translated form of the corresponding domain event.
/// </summary>
public sealed record OrderStatusChangedNotification(
    Guid OrderId,
    string Status,
    DateTimeOffset UpdatedAt,
    string SourceService,
    int AttemptNumber,
    bool RetryExhausted,
    Dictionary<string, object> Metadata);

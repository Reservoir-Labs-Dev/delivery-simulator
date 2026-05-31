namespace Reservoir.BuildingBlocks.Contracts;

/// <summary>
/// The SignalR notification the dashboard hub pushes to connected frontend
/// clients on every order state transition. Shape matches ARCH-002 § 4.
///
/// This does <b>not</b> travel over RabbitMQ — it is produced by
/// dashboard-api as the translated form of the corresponding domain event.
/// </summary>
/// <remarks>
/// <para>
/// DOG-40 added <see cref="RetryCount"/> and <see cref="Outcome"/>:
/// <c>RetryCount</c> is the number of retries (= <see cref="AttemptNumber"/> - 1, clamped at 0);
/// <c>Outcome</c> is <c>SUCCESS</c>, <c>FAILED</c>, or <c>DLQ</c> — see
/// <see cref="EventOutcome"/>.
/// </para>
/// <para>
/// <see cref="AttemptNumber"/> and <see cref="RetryExhausted"/> are kept for
/// backwards compatibility with the React dashboard's existing rendering;
/// new clients should prefer the DOG-40 fields.
/// </para>
/// </remarks>
public sealed record OrderStatusChangedNotification(
    Guid OrderId,
    string Status,
    DateTimeOffset UpdatedAt,
    string SourceService,
    int AttemptNumber,
    bool RetryExhausted,
    Dictionary<string, object> Metadata,
    int RetryCount,
    string Outcome);

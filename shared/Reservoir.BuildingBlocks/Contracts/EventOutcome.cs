namespace Reservoir.BuildingBlocks.Contracts;

/// <summary>
/// Terminal outcome of the handler step that produced an event. Carried on
/// every domain event since DOG-40 and surfaced on the SignalR
/// <see cref="OrderStatusChangedNotification"/> as <c>Outcome</c>.
/// </summary>
public static class EventOutcome
{
    /// <summary>Handler step completed successfully.</summary>
    public const string Success = "SUCCESS";

    /// <summary>Handler step failed but retries remain — message will be retried.</summary>
    public const string Failed = "FAILED";

    /// <summary>Handler step failed and exhausted retries — message routed to DLQ.</summary>
    public const string Dlq = "DLQ";
}

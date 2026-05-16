namespace DeliveryService.Simulation;

public sealed record DeliveryOutcome(bool Success, int DelayMs, string? FailureReason)
{
    public static DeliveryOutcome Succeeded(int delayMs) => new(true, delayMs, null);
    public static DeliveryOutcome Failed(string reason, int delayMs) => new(false, delayMs, reason);
}

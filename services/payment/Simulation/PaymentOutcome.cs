namespace PaymentService.Simulation;

public sealed record PaymentOutcome(bool Success, int DelayMs, string? FailureReason)
{
    public static PaymentOutcome Succeeded(int delayMs) => new(true, delayMs, null);
    public static PaymentOutcome Failed(string reason, int delayMs) => new(false, delayMs, reason);
}

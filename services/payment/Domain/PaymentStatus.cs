namespace PaymentService.Domain;

public static class PaymentStatus
{
    public const string Succeeded = "SUCCEEDED";
    public const string Failed = "FAILED";
}

public static class PaymentFailureReason
{
    public const string PaymentDeclined = "PAYMENT_DECLINED";
    public const string Timeout = "TIMEOUT";
    public const string ChaosInjected = "CHAOS_INJECTED";
}

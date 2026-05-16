namespace DeliveryService.Domain;

public static class DeliveryStatus
{
    public const string InProgress = "IN_PROGRESS";
    public const string Completed = "COMPLETED";
    public const string Failed = "FAILED";
}

public static class DeliveryFailureReason
{
    public const string DriverUnavailable = "DRIVER_UNAVAILABLE";
    public const string AddressInvalid = "ADDRESS_INVALID";
    public const string ChaosInjected = "CHAOS_INJECTED";
}

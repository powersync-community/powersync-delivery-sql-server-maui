namespace DeliveryApp.Services;

// Shapes of the token endpoint's JSON bodies (snake_case on the wire, see ApiClient). The app's own rows
// (stops and deliveries) live in Data/DeliveryDbContext.cs.

public static class StopStatus
{
    public const string Assigned = "assigned";
    public const string Delivered = "delivered";
    public const string Failed = "failed";
    public const string Canceled = "canceled";
}

public static class Outcomes
{
    public const string Delivered = "delivered";
    public const string Failed = "failed";
}

record TokenRequest(string DriverId);
public record TokenResponse(string Token, DateTimeOffset ExpiresAt);

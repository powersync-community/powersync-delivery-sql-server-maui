using System.Net;

namespace DeliveryApp.Services;

// Shapes of the API's JSON bodies (snake_case on the wire, see ApiClient). The app's own rows
// (stops and deliveries) live in Data/AppSchema.cs.

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
public record TokenResponse(string Token, DateTime ExpiresAt);
// recorded_at is passed through as the local row has it (ISO 8601 UTC text).
record DeliveryRequest(string Id, string Outcome, string? Note, string RecordedAt);
record ErrorResponse(string? Error);

/// <summary>A request that failed. <see cref="StatusCode"/> is null when the server couldn't be reached.</summary>
public class ApiException(HttpStatusCode? statusCode, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public HttpStatusCode? StatusCode { get; } = statusCode;
}

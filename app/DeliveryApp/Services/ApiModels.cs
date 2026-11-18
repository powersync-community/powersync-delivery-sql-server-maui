using System.Net;

namespace DeliveryApp.Services;

// Shapes of the API's JSON bodies (snake_case on the wire, see ApiClient).

public record Stop(Guid Id, DateOnly RouteDate, int Sequence, string Recipient, string Address, string Status, DateTime UpdatedAt)
{
    public bool IsCanceled => Status == StopStatus.Canceled;
}

public record Delivery(Guid Id, Guid StopId, string Outcome, string? Note, DateTime RecordedAt);

public record StopDetail(Stop Stop, List<Delivery> Deliveries);

public record DeliveryResult(Stop Stop, Delivery Delivery);

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
record TokenResponse(string Token, DateTime ExpiresAt);
record DeliveryRequest(string Outcome, string? Note);
record ErrorResponse(string? Error);

/// <summary>A request that failed. <see cref="StatusCode"/> is null when the server couldn't be reached.</summary>
public class ApiException(HttpStatusCode? statusCode, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public HttpStatusCode? StatusCode { get; } = statusCode;

    /// <summary>A network error, a timeout or a 5xx: the driver should check their connection and try again.</summary>
    public bool IsConnectionProblem => StatusCode is null || (int)StatusCode >= 500;
}

using DeliveryApi.Data;

namespace DeliveryApi.Endpoints;

// Request and response bodies. Property names are serialized as snake_case (see Program.cs).

public record TokenRequest(string? DriverId);
public record TokenResponse(string Token, DateTime ExpiresAt);

public record DeliveryRequest(string? Outcome, string? Note);

public record StopDto(Guid Id, DateOnly RouteDate, int Sequence, string Recipient, string Address, string Status, DateTime UpdatedAt)
{
    public static StopDto From(Stop s) => new(s.Id, s.RouteDate, s.Sequence, s.Recipient, s.Address, s.Status, s.UpdatedAt);
}

public record DeliveryDto(Guid Id, Guid StopId, string Outcome, string? Note, DateTime RecordedAt)
{
    public static DeliveryDto From(Delivery d) => new(d.Id, d.StopId, d.Outcome, d.Note, d.RecordedAt);
}

public record StopDetailResponse(StopDto Stop, IReadOnlyList<DeliveryDto> Deliveries);
public record DeliveryResponse(StopDto Stop, DeliveryDto Delivery);

public record ErrorResponse(string Error);

public static class ApiResults
{
    public static IResult Error(int statusCode, string message) =>
        Results.Json(new ErrorResponse(message), statusCode: statusCode);

    public static IResult BadRequest(string message) => Error(StatusCodes.Status400BadRequest, message);
    public static IResult NotFound(string message = "Stop not found.") => Error(StatusCodes.Status404NotFound, message);
    public static IResult Conflict(string message) => Error(StatusCodes.Status409Conflict, message);
}

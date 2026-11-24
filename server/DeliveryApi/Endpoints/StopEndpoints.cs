using DeliveryApi.Auth;
using DeliveryApi.Data;
using Microsoft.EntityFrameworkCore;

namespace DeliveryApi.Endpoints;

public static class StopEndpoints
{
    public const int MaxNoteLength = 500;

    public static void MapStopEndpoints(this IEndpointRouteBuilder app)
    {
        var stops = app.MapGroup("/api/stops").RequireAuthorization();

        stops.MapGet("/", ListStops);
        stops.MapGet("/{id:guid}", GetStop);
        stops.MapPost("/{id:guid}/deliveries", RecordDelivery);
    }

    /// <summary>Today's stops for the driver, in route order.</summary>
    static async Task<IResult> ListStops(HttpContext http, DeliveryDbContext db)
    {
        var driverId = http.User.DriverId();
        var today = RouteSeeder.Today();
        var stops = await db.Stops
            .AsNoTracking()
            .Where(s => s.DriverId == driverId && s.RouteDate == today)
            .OrderBy(s => s.Sequence)
            .ToListAsync();
        return Results.Ok(stops.Select(StopDto.From));
    }

    static async Task<IResult> GetStop(Guid id, HttpContext http, DeliveryDbContext db)
    {
        var driverId = http.User.DriverId();
        var stop = await db.Stops.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id.ToString() && s.DriverId == driverId);
        if (stop is null)
            return ApiResults.NotFound();

        var deliveries = await db.Deliveries
            .AsNoTracking()
            .Where(d => d.StopId == id && d.DriverId == driverId)
            .OrderBy(d => d.RecordedAt)
            .ToListAsync();
        return Results.Ok(new StopDetailResponse(StopDto.From(stop), deliveries.Select(DeliveryDto.From).ToList()));
    }

    /// <summary>
    /// Records a delivery attempt. The business rule lives here, on the server: a stop that dispatch
    /// canceled, or that is already delivered, can't take another outcome. A failed stop can be retried.
    /// </summary>
    static async Task<IResult> RecordDelivery(
        Guid id, DeliveryRequest request, HttpContext http, DeliveryDbContext db, ILoggerFactory loggers)
    {
        if (!Outcomes.IsValid(request.Outcome))
            return ApiResults.BadRequest($"outcome must be \"{Outcomes.Delivered}\" or \"{Outcomes.Failed}\".");
        var outcome = request.Outcome!;

        var note = request.Note?.Trim();
        if (string.IsNullOrEmpty(note))
            note = null;
        else if (note.Length > MaxNoteLength)
            return ApiResults.BadRequest($"note must be at most {MaxNoteLength} characters.");

        var driverId = http.User.DriverId();

        await using var tx = await db.Database.BeginTransactionAsync();

        // Bumping updated_at first also locks the stop row until we commit, so a concurrent cancel from
        // dispatch can't slip in between the status check below and the insert.
        var updated = await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE dbo.stops SET updated_at = SYSUTCDATETIME() WHERE id = {id} AND driver_id = {driverId}");
        if (updated == 0)
            return ApiResults.NotFound();

        var stop = await db.Stops.SingleAsync(s => s.Id == id.ToString() && s.DriverId == driverId);
        switch (stop.Status)
        {
            case StopStatus.Canceled:
                return ApiResults.Conflict("Dispatch canceled this stop.");
            case StopStatus.Delivered:
                return ApiResults.Conflict("This stop is already delivered.");
        }

        var delivery = new Delivery { StopId = id, DriverId = driverId, Outcome = outcome, Note = note };
        db.Deliveries.Add(delivery);
        stop.Status = outcome;
        await db.SaveChangesAsync(); // Reads back the database-generated delivery ID and recorded_at.

        await tx.CommitAsync();

        loggers.CreateLogger("DeliveryApi.Stops").LogInformation("Stop {Id}: {Outcome}", id, outcome);

        return Results.Created($"/api/stops/{id}", new DeliveryResponse(StopDto.From(stop), DeliveryDto.From(delivery)));
    }
}

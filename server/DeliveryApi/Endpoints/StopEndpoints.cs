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
        var stop = await db.Stops.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id && s.DriverId == driverId);
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
    /// Safe to repeat: a client that sends its own delivery ID gets 200 for a delivery that's already here.
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

        var stop = await db.Stops.SingleAsync(s => s.Id == id && s.DriverId == driverId);

        // Uploads are retried (e.g. the connection dropped after we committed), so the same delivery can
        // arrive twice. If we already have it, say so, before the status rule: a retry of an accepted
        // "delivered" must not be told the stop is already delivered.
        // (Two copies arriving at once queue on the stop lock above, so the second one also lands here.)
        // Production: an ID that exists for another stop or driver fails the insert below with a 500, which
        // the client retries forever. Check for it and return 400 instead.
        if (request.Id is { } deliveryId &&
            await db.Deliveries.AsNoTracking().SingleOrDefaultAsync(d => d.Id == deliveryId && d.StopId == id && d.DriverId == driverId) is { } existing)
        {
            return Results.Ok(new DeliveryResponse(StopDto.From(stop), DeliveryDto.From(existing)));
        }

        switch (stop.Status)
        {
            case StopStatus.Canceled:
                return ApiResults.Conflict("Dispatch canceled this stop.");
            case StopStatus.Delivered:
                return ApiResults.Conflict("This stop is already delivered.");
        }

        // The client's ID and tap time, if it sent them. Guid.Empty and default(DateTime) mean "not set",
        // so the database defaults (NEWSEQUENTIALID(), SYSUTCDATETIME()) apply as before.
        // Production: check recorded_at against the server clock; a device with a wrong clock sends a wrong time.
        var delivery = new Delivery
        {
            Id = request.Id ?? Guid.Empty,
            StopId = id,
            DriverId = driverId,
            Outcome = outcome,
            Note = note,
            RecordedAt = request.RecordedAt?.UtcDateTime ?? default,
        };
        db.Deliveries.Add(delivery);
        stop.Status = outcome;
        await db.SaveChangesAsync(); // Reads back the delivery ID and recorded_at if the database generated them.

        await tx.CommitAsync();

        loggers.CreateLogger("DeliveryApi.Stops").LogInformation("Stop {Id}: {Outcome}", id, outcome);

        return Results.Created($"/api/stops/{id}", new DeliveryResponse(StopDto.From(stop), DeliveryDto.From(delivery)));
    }
}

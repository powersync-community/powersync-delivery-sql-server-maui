using System.Linq.Expressions;
using CommunityToolkit.Datasync.Server;
using CommunityToolkit.Datasync.Server.EntityFrameworkCore;
using DeliveryApi.Auth;
using DeliveryApi.Data;
using DeliveryApi.Endpoints;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DeliveryApi.Controllers;

// Datasync table controllers: the app pulls and pushes stops and deliveries here (GET/POST/PUT/DELETE
// /tables/stop and /tables/delivery, OData queries). Same JWT auth as the rest of the API. Per-driver
// filtering and the business rule live in the access control providers below.

[Authorize]
[Route("tables/[controller]")]
public class StopController : TableController<Stop>
{
    public StopController(DeliveryDbContext db, IHttpContextAccessor http, ILogger<StopController> logger)
        : base(new EntityTableRepository<Stop>(db), new StopAccessControl(http))
    {
        Logger = logger;
        // Refusals that aren't the business rule (e.g. a driver creating a stop) get 403, not the default 401.
        Options = new TableControllerOptions { UnauthorizedStatusCode = StatusCodes.Status403Forbidden };
    }
}

[Authorize]
[Route("tables/[controller]")]
public class DeliveryController : TableController<Delivery>
{
    public DeliveryController(DeliveryDbContext db, IHttpContextAccessor http, ILogger<DeliveryController> logger)
        : base(new EntityTableRepository<Delivery>(db), new DeliveryAccessControl(db, http, logger))
    {
        Logger = logger;
        Options = new TableControllerOptions { UnauthorizedStatusCode = StatusCodes.Status403Forbidden };
    }
}

/// <summary>Each driver sees only their own rows: the driver ID comes from the token's <c>sub</c>, never from the client.</summary>
public abstract class DriverAccessControl<T>(IHttpContextAccessor http) : AccessControlProvider<T> where T : ITableData
{
    protected string? DriverId => http.HttpContext?.User.DriverId();

    /// <summary>A refusal with a reason the app can show, in the API's usual error shape.</summary>
    protected static HttpException Refuse(int status, string message) => new(status, message) { Payload = new ErrorResponse(message) };
}

/// <summary>
/// Stops are dispatch's. A driver may read their own, and change one thing: the status, to delivered or
/// failed, while the stop is still open. That's the business rule's half that lives on the stop.
/// </summary>
public class StopAccessControl(IHttpContextAccessor http) : DriverAccessControl<Stop>(http)
{
    Stop? _stored;

    public override Expression<Func<Stop, bool>> GetDataView()
    {
        var driverId = DriverId;
        return driverId is null ? _ => false : s => s.DriverId == driverId;
    }

    public override ValueTask<bool> IsAuthorizedAsync(TableOperation operation, Stop? entity, CancellationToken cancellationToken = default)
    {
        if (operation is TableOperation.Query or TableOperation.Read)
            return ValueTask.FromResult(true);
        if (operation is not TableOperation.Update || entity is null)
            return ValueTask.FromResult(false); // Dispatch creates and cancels stops; drivers don't.

        // entity is the stored stop, before the change. This runs before the If-Match (412) check.
        _stored = entity;
        return entity.Status switch
        {
            StopStatus.Canceled => throw Refuse(StatusCodes.Status409Conflict, "Dispatch canceled this stop."),
            StopStatus.Delivered => throw Refuse(StatusCodes.Status409Conflict, "This stop is already delivered."),
            _ => ValueTask.FromResult(true),
        };
    }

    public override ValueTask PreCommitHookAsync(TableOperation operation, Stop entity, CancellationToken cancellationToken = default)
    {
        if (operation is TableOperation.Update && _stored is { } stored)
        {
            if (!Outcomes.IsValid(entity.Status))
                throw Refuse(StatusCodes.Status400BadRequest, $"status must be \"{Outcomes.Delivered}\" or \"{Outcomes.Failed}\".");
            // Only the status is the driver's to change; everything else stays as stored.
            entity.DriverId = stored.DriverId;
            entity.RouteDate = stored.RouteDate;
            entity.Sequence = stored.Sequence;
            entity.Recipient = stored.Recipient;
            entity.Address = stored.Address;
        }
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Deliveries are the driver's attempts. A driver may add one to their own stop while it's open (the business
/// rule: not on a canceled or already delivered stop), and never change or delete one.
/// </summary>
public class DeliveryAccessControl(DeliveryDbContext db, IHttpContextAccessor http, ILogger logger) : DriverAccessControl<Delivery>(http)
{
    public override Expression<Func<Delivery, bool>> GetDataView()
    {
        var driverId = DriverId;
        return driverId is null ? _ => false : d => d.DriverId == driverId;
    }

    public override async ValueTask<bool> IsAuthorizedAsync(TableOperation operation, Delivery? entity, CancellationToken cancellationToken = default)
    {
        if (operation is TableOperation.Query or TableOperation.Read)
            return true;
        if (operation is not TableOperation.Create || entity is null)
            return false;

        // Production: unlike the old POST endpoint, this check and the insert aren't in one transaction with a
        // lock on the stop, so a cancel from dispatch landing in between isn't caught.
        var stopId = entity.StopId.ToString();
        var stop = await db.Stops.AsNoTracking().SingleOrDefaultAsync(s => s.Id == stopId && s.DriverId == DriverId, cancellationToken);
        return stop?.Status switch
        {
            null => throw Refuse(StatusCodes.Status404NotFound, "Stop not found."),
            StopStatus.Canceled => throw Refuse(StatusCodes.Status409Conflict, "Dispatch canceled this stop."),
            StopStatus.Delivered => throw Refuse(StatusCodes.Status409Conflict, "This stop is already delivered."),
            _ => true,
        };
    }

    public override ValueTask PreCommitHookAsync(TableOperation operation, Delivery entity, CancellationToken cancellationToken = default)
    {
        // The driver comes from the token, whatever the client sent. recorded_at is the device's tap time.
        // Production: check recorded_at against the server clock; a device with a wrong clock sends a wrong time.
        entity.DriverId = DriverId!;
        return ValueTask.CompletedTask;
    }

    public override ValueTask PostCommitHookAsync(TableOperation operation, Delivery entity, CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Stop {Id}: {Outcome}", entity.StopId, entity.Outcome);
        return ValueTask.CompletedTask;
    }
}

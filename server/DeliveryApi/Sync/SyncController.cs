using DeliveryApi.Auth;
using Dotmim.Sync;
using Dotmim.Sync.Web.Server;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeliveryApi.Sync;

/// <summary>
/// The sync endpoint, as Dotmim.Sync's docs host it: a controller whose POST hands every request to the
/// WebServerAgent. One sync is a handful of POSTs, each with the API's usual bearer token.
/// </summary>
[Authorize]
[ApiController]
[Route("api/sync")]
public class SyncController(WebServerAgent agent, ILogger<SyncController> logger) : ControllerBase
{
    [HttpPost]
    public Task Post()
    {
        var driverId = User.DriverId();

        // Filter parameters normally come from the client. Set this one from the token instead, on every
        // request, so a client can't ask for another driver's rows. (The docs' example only adds it when the
        // client didn't send one, which lets the client pick.)
        agent.OnHttpGettingRequest(args =>
        {
            args.Context.Parameters ??= new SyncParameters();
            args.Context.Parameters.Remove(DeliverySync.DriverParameter);
            args.Context.Parameters.Add(DeliverySync.DriverParameter, driverId);
        });
        // ...and take it out again before answering. The client keys its local sync state on the parameters
        // it sent (none), and fails if the server sends back different ones.
        agent.OnHttpSendingResponse(args => args.Context.Parameters?.Remove(DeliverySync.DriverParameter));

        // The business rule, on the rows this client uploads.
        agent.RemoteOrchestrator.OnRowsChangesApplying(args => DeliverySync.ApplyRulesAsync(args, driverId, logger));

        return agent.HandleRequestAsync(HttpContext);
    }
}

using DeliveryApi.Auth;
using DeliveryApi.Data;

namespace DeliveryApi.Endpoints;

public static class AuthEndpoints
{
    public const int MaxDriverIdLength = 64;

    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        // DEMO ONLY: this stands in for real sign-in. The app makes up a driver ID and we issue a token
        // for it without asking any questions, so anyone can act as any driver. A real app would only
        // issue tokens to drivers who have signed in (e.g. via an identity provider).
        app.MapPost("/api/auth/token", async (TokenRequest request, TokenService tokens, DeliveryDbContext db) =>
        {
            var driverId = request.DriverId?.Trim();
            if (string.IsNullOrEmpty(driverId))
                return ApiResults.BadRequest("driver_id is required.");
            if (driverId.Length > MaxDriverIdLength)
                return ApiResults.BadRequest($"driver_id must be at most {MaxDriverIdLength} characters.");

            await RouteSeeder.SeedTodayIfEmptyAsync(db, driverId);

            var (token, expiresAt) = tokens.CreateToken(driverId);
            return Results.Ok(new TokenResponse(token, expiresAt));
        });

        // The public signing key, so other services can verify our tokens without sharing a secret.
        app.MapGet("/.well-known/jwks.json", (TokenService tokens) => Results.Ok(tokens.Jwks()));
    }
}

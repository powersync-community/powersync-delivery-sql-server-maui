using System.Text.Json;
using DeliveryApi;
using DeliveryApi.Auth;
using DeliveryApi.Data;
using DeliveryApi.Endpoints;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var settings = Settings.FromEnvironment();
var (signingKey, keyGenerated) = TokenService.LoadOrCreateKey(settings.JwtKeyPath);

var builder = WebApplication.CreateBuilder(args);

// Local development only: plain HTTP on port 7070 (ASPNETCORE_URLS overrides it, e.g. in a container).
if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
    builder.WebHost.UseUrls("http://localhost:7070");

builder.Services.AddSingleton(settings);
builder.Services.AddDbContext<DeliveryDbContext>(o => o.UseSqlServer(settings.ConnectionString));

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
});
// Send unreadable request bodies through the exception handler below, so they get the error shape too.
builder.Services.Configure<RouteHandlerOptions>(o => o.ThrowOnBadRequest = true);

// Auth: RS256 JWTs whose `sub` is the driver ID (see AuthEndpoints for how they're issued).
// The API checks its own tokens with the key directly; other services can use /.well-known/jwks.json.
builder.Services.AddSingleton(new TokenService(signingKey));
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.MapInboundClaims = false; // Keep `sub` as `sub`.
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = TokenService.Issuer,
            ValidateAudience = true,
            ValidAudience = TokenService.Audience,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = signingKey,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
        };
        o.Events = new JwtBearerEvents
        {
            // Use the API's error shape for 401s.
            OnChallenge = async context =>
            {
                context.HandleResponse();
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new ErrorResponse("A valid bearer token is required."));
            },
        };
    });
builder.Services.AddAuthorization();

var app = builder.Build();

if (keyGenerated)
    app.Logger.LogInformation("Generated JWT signing key at {Path}", settings.JwtKeyPath);

// Unhandled errors (and unreadable request bodies) also use the { "error": "..." } shape.
app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
{
    var error = context.Features.Get<IExceptionHandlerFeature>()?.Error;
    var (status, message) = error is BadHttpRequestException bad
        ? (bad.StatusCode, "The request body is not valid JSON.")
        : (StatusCodes.Status500InternalServerError, "Something went wrong on the server.");
    context.Response.StatusCode = status;
    await context.Response.WriteAsJsonAsync(new ErrorResponse(message));
}));

app.UseAuthentication();
app.UseAuthorization();

app.MapAuthEndpoints();
app.MapStopEndpoints();

app.Run();

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace DeliveryApp.Services;

public static class AppConfig
{
    /// <summary>The iOS simulator reaches the Mac's localhost directly.</summary>
    public const string ApiBaseUrl = "http://localhost:7070";

    /// <summary>Dotmim.Sync's endpoint, hosted in the same API (SyncController).</summary>
    public const string SyncUrl = ApiBaseUrl + "/api/sync";
}

/// <summary>
/// The REST API, as the app now uses it: only for a token. Reads and writes go to the local database,
/// and Dotmim.Sync moves rows to and from the server (see Data/Database.cs).
/// </summary>
public class ApiClient
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    readonly HttpClient _http = new()
    {
        BaseAddress = new Uri(AppConfig.ApiBaseUrl),
        Timeout = TimeSpan.FromSeconds(15),
    };
    readonly DriverIdentity _identity;
    readonly SemaphoreSlim _tokenLock = new(1, 1);
    TokenResponse? _token;

    public ApiClient(DriverIdentity identity) => _identity = identity;

    /// <summary>
    /// The API's bearer token for this driver. Kept in memory and fetched again a few minutes before it
    /// expires. (Asking for one also seeds today's route, see the server's RouteSeeder.)
    /// </summary>
    public async Task<string> GetTokenAsync()
    {
        await _tokenLock.WaitAsync();
        try
        {
            if (_token is null || _token.ExpiresAt < DateTime.UtcNow.AddMinutes(5))
            {
                var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/token")
                {
                    Content = JsonContent.Create(new TokenRequest(_identity.DriverId), options: Json),
                };
                using var response = await SendRawAsync(request);
                await EnsureSuccessAsync(response);
                _token = await response.Content.ReadFromJsonAsync<TokenResponse>(Json)
                    ?? throw new ApiException(response.StatusCode, "The server sent an empty response.");
            }
            return _token.Token;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    /// <summary>Forgets the token (after a 401). The next call fetches a new one.</summary>
    public void ClearToken() => _token = null;

    async Task<HttpResponseMessage> SendRawAsync(HttpRequestMessage request)
    {
        using (request)
        {
            try
            {
                return await _http.SendAsync(request);
            }
            catch (HttpRequestException ex)
            {
                throw new ApiException(null, "Couldn't reach the server.", ex);
            }
            catch (TaskCanceledException ex)
            {
                throw new ApiException(null, "The server took too long to respond.", ex);
            }
        }
    }

    static async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
            return;

        string? error = null;
        try
        {
            error = (await response.Content.ReadFromJsonAsync<ErrorResponse>(Json))?.Error;
        }
        catch (Exception)
        {
            // Not our error shape; fall back to the status code.
        }
        throw new ApiException(response.StatusCode, error ?? $"The server returned HTTP {(int)response.StatusCode}.");
    }
}

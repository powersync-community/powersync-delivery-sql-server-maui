using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace DeliveryApp.Services;

public static class AppConfig
{
    /// <summary>The iOS simulator reaches the Mac's localhost directly.</summary>
    public const string ApiBaseUrl = "http://localhost:7070";

    /// <summary>The PowerSync Service, which streams the driver's stops and deliveries to the device.</summary>
    public const string PowerSyncUrl = "http://localhost:8080";
}

/// <summary>
/// The REST API, as the upload path sees it: a token, and the POST that records a delivery.
/// Reads no longer go through here; the app reads its local database. Used only by DeliveryConnector.
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
    string? _token;

    public ApiClient(DriverIdentity identity) => _identity = identity;

    /// <summary>
    /// Records a delivery attempt with the device's ID and tap time. Safe to repeat: the server answers
    /// 200 for a delivery it already has. It still decides whether the stop can take one (409 if not).
    /// </summary>
    public Task RecordDeliveryAsync(string stopId, string deliveryId, string outcome, string? note, string recordedAt) =>
        SendAsync(Post($"/api/stops/{stopId}/deliveries", new DeliveryRequest(deliveryId, outcome, note, recordedAt)));

    /// <summary>Fetches a new token (PowerSync accepts it too) and keeps it for API calls.</summary>
    public async Task<TokenResponse> FetchTokenAsync()
    {
        await _tokenLock.WaitAsync();
        try
        {
            using var response = await SendRawAsync(Post("/api/auth/token", new TokenRequest(_identity.DriverId)), token: null);
            await EnsureSuccessAsync(response);
            var result = await response.Content.ReadFromJsonAsync<TokenResponse>(Json)
                ?? throw new ApiException(response.StatusCode, "The server sent an empty response.");
            _token = result.Token;
            return result;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    /// <summary>Forgets the token (after a 401). The next call fetches a new one.</summary>
    public void ClearToken() => _token = null;

    static HttpRequestMessage Post<T>(string path, T body) =>
        new(HttpMethod.Post, path) { Content = JsonContent.Create(body, options: Json) };

    async Task SendAsync(HttpRequestMessage request)
    {
        var token = _token ?? (await FetchTokenAsync()).Token;
        using var response = await SendRawAsync(request, token);
        await EnsureSuccessAsync(response);
    }

    async Task<HttpResponseMessage> SendRawAsync(HttpRequestMessage request, string? token)
    {
        using (request)
        {
            if (token is not null)
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
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

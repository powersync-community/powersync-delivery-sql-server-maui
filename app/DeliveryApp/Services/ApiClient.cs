using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace DeliveryApp.Services;

public static class AppConfig
{
    /// <summary>The iOS simulator reaches the Mac's localhost directly.</summary>
    public const string ApiBaseUrl = "http://localhost:7070";
}

/// <summary>
/// Typed access to the delivery API. Online only: every call goes to the server, nothing is cached,
/// and failures surface as <see cref="ApiException"/>. All writes go through this class.
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

    /// <summary>Today's stops, in route order.</summary>
    public Task<List<Stop>> GetStopsAsync(CancellationToken ct = default) =>
        SendAsync<List<Stop>>(() => new HttpRequestMessage(HttpMethod.Get, "/api/stops"), ct);

    /// <summary>One stop and its earlier delivery attempts.</summary>
    public Task<StopDetail> GetStopAsync(Guid stopId, CancellationToken ct = default) =>
        SendAsync<StopDetail>(() => new HttpRequestMessage(HttpMethod.Get, $"/api/stops/{stopId}"), ct);

    /// <summary>Records a delivery attempt. The server decides whether the stop can still take one.</summary>
    public Task<DeliveryResult> RecordDeliveryAsync(Guid stopId, string outcome, string? note, CancellationToken ct = default) =>
        SendAsync<DeliveryResult>(() => Post($"/api/stops/{stopId}/deliveries", new DeliveryRequest(outcome, note)), ct);

    static HttpRequestMessage Post<T>(string path, T body) =>
        new(HttpMethod.Post, path) { Content = JsonContent.Create(body, options: Json) };

    /// <summary>Sends an authenticated request. On 401, fetches a new token and retries once.</summary>
    async Task<T> SendAsync<T>(Func<HttpRequestMessage> createRequest, CancellationToken ct)
    {
        var token = await GetTokenAsync(forceRefresh: false, ct);
        using var response = await SendRawAsync(createRequest(), token, ct);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            token = await GetTokenAsync(forceRefresh: true, ct);
            using var retry = await SendRawAsync(createRequest(), token, ct);
            return await ReadAsync<T>(retry, ct);
        }
        return await ReadAsync<T>(response, ct);
    }

    async Task<string> GetTokenAsync(bool forceRefresh, CancellationToken ct)
    {
        await _tokenLock.WaitAsync(ct);
        try
        {
            if (_token is null || forceRefresh)
            {
                var request = Post("/api/auth/token", new TokenRequest(_identity.DriverId));
                using var response = await SendRawAsync(request, token: null, ct);
                _token = (await ReadAsync<TokenResponse>(response, ct)).Token;
            }
            return _token;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    async Task<HttpResponseMessage> SendRawAsync(HttpRequestMessage request, string? token, CancellationToken ct)
    {
        using (request)
        {
            if (token is not null)
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            try
            {
                return await _http.SendAsync(request, ct);
            }
            catch (HttpRequestException ex)
            {
                throw new ApiException(null, "Couldn't reach the server.", ex);
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                throw new ApiException(null, "The server took too long to respond.", ex);
            }
        }
    }

    static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return await response.Content.ReadFromJsonAsync<T>(Json, ct)
                ?? throw new ApiException(response.StatusCode, "The server sent an empty response.");
        }

        string? error = null;
        try
        {
            error = (await response.Content.ReadFromJsonAsync<ErrorResponse>(Json, ct))?.Error;
        }
        catch (Exception)
        {
            // Not our error shape; fall back to the status code.
        }
        throw new ApiException(response.StatusCode, error ?? $"The server returned HTTP {(int)response.StatusCode}.");
    }
}

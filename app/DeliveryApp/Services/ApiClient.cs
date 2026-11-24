using System.Net.Http.Json;
using System.Text.Json;

namespace DeliveryApp.Services;

public static class AppConfig
{
    /// <summary>The iOS simulator reaches the Mac's localhost directly.</summary>
    public const string ApiBaseUrl = "http://localhost:7070";
}

/// <summary>
/// The REST API's token endpoint. Reads and writes no longer go through here: the app uses its local
/// database, and Datasync talks to the API's table controllers (see Data/DeliveryDbContext.cs).
/// </summary>
public class ApiClient(DriverIdentity identity)
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

    public string DriverId => identity.DriverId;

    /// <summary>A new token for this driver. Throws (e.g. HttpRequestException) when the API can't be reached.</summary>
    public async Task<TokenResponse> FetchTokenAsync()
    {
        using var response = await _http.PostAsJsonAsync("/api/auth/token", new TokenRequest(identity.DriverId), Json);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<TokenResponse>(Json)
            ?? throw new HttpRequestException("The server sent an empty response.");
    }
}

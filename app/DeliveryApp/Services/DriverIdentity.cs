namespace DeliveryApp.Services;

/// <summary>
/// The made-up driver ID this app signs in as. Generated on first launch and kept in Preferences,
/// so it survives restarts. (Stands in for real sign-in; see the server's AuthEndpoints.)
/// </summary>
public class DriverIdentity
{
    const string Key = "driver_id";

    /// <summary>A short, readable ID like <c>driver-3f9a1c2e</c>, easy to pass to <c>dispatch.sh --driver</c>.</summary>
    public string DriverId { get; } = Load();

    static string Load()
    {
        var driverId = Preferences.Default.Get<string?>(Key, null);
        if (string.IsNullOrEmpty(driverId))
        {
            driverId = "driver-" + Guid.NewGuid().ToString("N")[..8];
            Preferences.Default.Set(Key, driverId);
        }
        return driverId;
    }
}

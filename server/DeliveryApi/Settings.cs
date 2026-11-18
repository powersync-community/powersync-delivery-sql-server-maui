namespace DeliveryApi;

/// <summary>Settings read from environment variables (see .env.template).</summary>
public record Settings(string ConnectionString, string JwtKeyPath)
{
    public const string DefaultJwtKeyPath = "keys/jwt-signing.pem";

    public static Settings FromEnvironment()
    {
        static string Get(string name) => Environment.GetEnvironmentVariable(name)?.Trim() ?? "";

        var settings = new Settings(
            ConnectionString: Get("CONNECTION_STRING"),
            JwtKeyPath: Get("JWT_KEY_PATH") is { Length: > 0 } path ? path : DefaultJwtKeyPath);

        if (settings.ConnectionString.Length == 0)
            throw new InvalidOperationException("CONNECTION_STRING is not set. Copy .env.template to .env and run scripts/run-server.sh.");

        return settings;
    }
}

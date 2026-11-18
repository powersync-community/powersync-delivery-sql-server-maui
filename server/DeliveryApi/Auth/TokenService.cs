using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace DeliveryApi.Auth;

/// <summary>
/// Issues RS256 tokens and publishes the public key as a JWKS, the way an identity provider such as
/// Entra ID does. Anything that needs to check our tokens can fetch /.well-known/jwks.json.
/// </summary>
public class TokenService(RsaSecurityKey signingKey)
{
    public const string KeyId = "delivery-api-1";
    public const string Issuer = "delivery-api";
    public const string Audience = "delivery-api";
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

    public RsaSecurityKey SigningKey { get; } = signingKey;

    /// <summary>
    /// Loads the RSA private key (PEM) from <paramref name="path"/>, or generates a 2048-bit key and
    /// writes it there if the file doesn't exist. The same file is reused on every start, so tokens
    /// survive a restart. For local development only: no key rotation, no secrets store.
    /// </summary>
    public static (RsaSecurityKey Key, bool Generated) LoadOrCreateKey(string path)
    {
        var rsa = RSA.Create();
        var generated = false;
        if (File.Exists(path))
        {
            rsa.ImportFromPem(File.ReadAllText(path));
        }
        else
        {
            rsa.KeySize = 2048;
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            File.WriteAllText(path, rsa.ExportPkcs8PrivateKeyPem());
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            generated = true;
        }
        return (new RsaSecurityKey(rsa) { KeyId = KeyId }, generated);
    }

    public (string Token, DateTime ExpiresAt) CreateToken(string driverId)
    {
        var expiresAt = DateTime.UtcNow.Add(Lifetime);
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity([new Claim(JwtRegisteredClaimNames.Sub, driverId)]),
            Issuer = Issuer,
            Audience = Audience,
            Expires = expiresAt,
            SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.RsaSha256),
        });
        return (token, expiresAt);
    }

    /// <summary>The public half of the signing key, as a JSON Web Key Set.</summary>
    public JwksResponse Jwks()
    {
        var parameters = SigningKey.Rsa.ExportParameters(includePrivateParameters: false);
        return new JwksResponse([
            new Jwk(
                Kty: "RSA",
                Use: "sig",
                Alg: SecurityAlgorithms.RsaSha256,
                Kid: KeyId,
                N: Base64UrlEncoder.Encode(parameters.Modulus),
                E: Base64UrlEncoder.Encode(parameters.Exponent)),
        ]);
    }
}

public record Jwk(string Kty, string Use, string Alg, string Kid, string N, string E);
public record JwksResponse(IReadOnlyList<Jwk> Keys);

public static class DriverExtensions
{
    /// <summary>The driver ID from the token's <c>sub</c> claim. Every query is scoped to it.</summary>
    public static string DriverId(this ClaimsPrincipal user) =>
        user.FindFirstValue(JwtRegisteredClaimNames.Sub)
        ?? throw new InvalidOperationException("Authenticated request without a sub claim.");
}

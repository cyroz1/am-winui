using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AmNative.Services;

internal static class DeveloperTokenSigner
{
    private const string P256CurveOid = "1.2.840.10045.3.1.7";

    public static void ValidatePrivateKey(string privateKeyPem)
    {
        using var key = ImportPrivateKey(privateKeyPem);
    }

    public static string CreateToken(
        string teamId,
        string keyId,
        string privateKeyPem,
        DateTimeOffset now,
        string? origin = null,
        TimeSpan? lifetime = null)
    {
        using var key = ImportPrivateKey(privateKeyPem);
        var issuedAt = now.ToUnixTimeSeconds();
        var tokenLifetime = lifetime ?? TimeSpan.FromHours(1);
        if (tokenLifetime <= TimeSpan.Zero || tokenLifetime > TimeSpan.FromSeconds(15_777_000))
        {
            throw new ArgumentOutOfRangeException(nameof(lifetime), "Apple Music developer tokens can be valid for at most six months.");
        }

        var expiresAt = now.Add(tokenLifetime).ToUnixTimeSeconds();
        var header = JsonSerializer.SerializeToUtf8Bytes(new
        {
            alg = "ES256",
            kid = keyId,
            typ = "JWT"
        });
        var payload = string.IsNullOrWhiteSpace(origin)
            ? JsonSerializer.SerializeToUtf8Bytes(new
            {
                iss = teamId,
                iat = issuedAt,
                exp = expiresAt
            })
            : JsonSerializer.SerializeToUtf8Bytes(new
            {
                iss = teamId,
                iat = issuedAt,
                exp = expiresAt,
                origin = new[] { origin }
            });
        var unsignedToken = $"{Base64UrlEncode(header)}.{Base64UrlEncode(payload)}";
        var signature = key.SignData(
            Encoding.ASCII.GetBytes(unsignedToken),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return $"{unsignedToken}.{Base64UrlEncode(signature)}";
    }

    private static ECDsa ImportPrivateKey(string privateKeyPem)
    {
        if (string.IsNullOrWhiteSpace(privateKeyPem) || privateKeyPem.Length > 8192)
        {
            throw new ArgumentException("Select a valid Apple Music .p8 private key file.", nameof(privateKeyPem));
        }

        var key = ECDsa.Create();
        try
        {
            key.ImportFromPem(privateKeyPem);
            var parameters = key.ExportParameters(includePrivateParameters: true);
            if (key.KeySize != 256 || parameters.D is null || parameters.Curve.Oid.Value != P256CurveOid)
            {
                throw new ArgumentException("The Apple Music private key must be an ES256 P-256 .p8 key.", nameof(privateKeyPem));
            }

            return key;
        }
        catch (ArgumentException)
        {
            key.Dispose();
            throw;
        }
        catch (CryptographicException exception)
        {
            key.Dispose();
            throw new ArgumentException("The selected file is not a readable Apple Music .p8 private key.", nameof(privateKeyPem), exception);
        }
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;

namespace UIBackend.Api.Security;

public sealed class JwksKeyProvider
{
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<JwksKeyProvider> _logger;
    private readonly object _lock = new();

    private List<SecurityKey> _cachedJwksKeys = new();
    private DateTime _lastFetchUtc = DateTime.MinValue;

    public JwksKeyProvider(
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        ILogger<JwksKeyProvider> logger)
    {
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public IEnumerable<SecurityKey> ResolveSigningKeys(string? kid)
    {
        var keys = new List<SecurityKey>();

        // 1. Static RSA Public Key PEM if configured
        var staticKey = LoadStaticPublicKey();
        if (staticKey != null)
        {
            keys.Add(staticKey);
        }

        // 2. Dynamic JWKS keys fetched from Auth /.well-known/jwks.json
        var needsRefresh = false;
        lock (_lock)
        {
            var expired = DateTime.UtcNow - _lastFetchUtc > TimeSpan.FromMinutes(15);
            var unknownKid = !string.IsNullOrWhiteSpace(kid) &&
                             !keys.Any(k => k.KeyId == kid) &&
                             !_cachedJwksKeys.Any(k => k.KeyId == kid);

            if ((expired || unknownKid) && DateTime.UtcNow - _lastFetchUtc > TimeSpan.FromSeconds(15))
            {
                needsRefresh = true;
            }
            else
            {
                keys.AddRange(_cachedJwksKeys);
            }
        }

        if (needsRefresh)
        {
            RefreshJwksSync();
            lock (_lock)
            {
                keys.AddRange(_cachedJwksKeys);
            }
        }

        return keys;
    }

    private SecurityKey? LoadStaticPublicKey()
    {
        try
        {
            var pem = _configuration["Jwt:PublicKeyPem"] ?? Environment.GetEnvironmentVariable("JWT_PUBLIC_KEY_PEM");
            if (string.IsNullOrWhiteSpace(pem))
            {
                var path = _configuration["Jwt:PublicKeyPath"] ?? Environment.GetEnvironmentVariable("JWT_PUBLIC_KEY_PATH");
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    pem = File.ReadAllText(path);
                }
            }

            if (string.IsNullOrWhiteSpace(pem)) return null;

            var rsa = RSA.Create();
            rsa.ImportFromPem(pem.Replace("\\n", "\n").Trim());
            var kid = _configuration["Jwt:KeyId"];
            return new RsaSecurityKey(rsa)
            {
                KeyId = string.IsNullOrWhiteSpace(kid) ? null : kid
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse configured RSA public key in UIBackend API.");
            return null;
        }
    }

    private void RefreshJwksSync()
    {
        var jwksUrl = _configuration["Jwt:JwksUrl"] ?? "https://visionx.samstack.site/auth/.well-known/jwks.json";
        if (string.IsNullOrWhiteSpace(jwksUrl)) return;

        try
        {
            var client = _httpClientFactory.CreateClient("JwksClient");
            var task = client.GetStringAsync(jwksUrl);
            task.Wait(TimeSpan.FromSeconds(5));

            if (task.IsCompletedSuccessfully)
            {
                var json = task.Result;
                var keySet = new JsonWebKeySet(json);
                lock (_lock)
                {
                    _cachedJwksKeys = keySet.GetSigningKeys().ToList();
                    _lastFetchUtc = DateTime.UtcNow;
                }
                _logger.LogInformation("Fetched {Count} signing keys from JWKS URL: {Url}", _cachedJwksKeys.Count, jwksUrl);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not refresh JWKS keys from {Url}: {Message}", jwksUrl, ex.Message);
        }
    }
}

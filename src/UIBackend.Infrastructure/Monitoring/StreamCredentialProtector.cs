using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using UIBackend.Application.Interfaces;

namespace UIBackend.Infrastructure.Monitoring;

public class StreamCredentialProtector : IStreamCredentialProtector
{
    private readonly byte[] _key;

    public StreamCredentialProtector(IConfiguration configuration)
    {
        var rawKey = configuration["Monitoring:CredentialEncryptionKey"]
            ?? Environment.GetEnvironmentVariable("STREAM_CREDENTIAL_KEY")
            ?? "VisionX_Default_Dev_Stream_Key_2026_ChangeInProd!";

        using var sha = SHA256.Create();
        _key = sha.ComputeHash(Encoding.UTF8.GetBytes(rawKey));
    }

    public string? EncryptCredentials(string? username, string? password)
    {
        if (string.IsNullOrEmpty(username) && string.IsNullOrEmpty(password))
        {
            return null;
        }

        var payload = JsonSerializer.Serialize(new { u = username ?? string.Empty, p = password ?? string.Empty });
        var plainBytes = Encoding.UTF8.GetBytes(payload);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipherBytes = new byte[plainBytes.Length];
        var tag = new byte[16];

        using var aes = new AesGcm(_key, 16);
        aes.Encrypt(nonce, plainBytes, cipherBytes, tag);

        var combined = new byte[nonce.Length + tag.Length + cipherBytes.Length];
        Buffer.BlockCopy(nonce, 0, combined, 0, nonce.Length);
        Buffer.BlockCopy(tag, 0, combined, nonce.Length, tag.Length);
        Buffer.BlockCopy(cipherBytes, 0, combined, nonce.Length + tag.Length, cipherBytes.Length);

        return Convert.ToBase64String(combined);
    }

    public (string? Username, string? Password) DecryptCredentials(string? encryptedBlob)
    {
        if (string.IsNullOrWhiteSpace(encryptedBlob))
        {
            return (null, null);
        }

        try
        {
            var combined = Convert.FromBase64String(encryptedBlob);
            if (combined.Length < 28) return (null, null);

            var nonce = combined.AsSpan(0, 12);
            var tag = combined.AsSpan(12, 16);
            var cipherBytes = combined.AsSpan(28);
            var plainBytes = new byte[cipherBytes.Length];

            using var aes = new AesGcm(_key, 16);
            aes.Decrypt(nonce, cipherBytes, tag, plainBytes);

            using var doc = JsonDocument.Parse(plainBytes);
            var u = doc.RootElement.TryGetProperty("u", out var uProp) ? uProp.GetString() : null;
            var p = doc.RootElement.TryGetProperty("p", out var pProp) ? pProp.GetString() : null;
            return (u, p);
        }
        catch
        {
            return (null, null);
        }
    }
}

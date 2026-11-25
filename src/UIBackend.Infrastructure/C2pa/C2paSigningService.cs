using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using UIBackend.Application.Interfaces;
using UIBackend.Domain.Enums;

namespace UIBackend.Infrastructure.C2pa;

public class C2paSigningService : IC2paVerificationProvider
{
    private readonly ILogger<C2paSigningService> _logger;
    private readonly RSA _signingRsa;
    private readonly X509Certificate2 _signingCert;
    private readonly string _issuerName;

    // Standard C2PA UUID for MP4 uuid atom: d8fec3d6-1b0e-483c-9297-5828877ec481
    private static readonly byte[] Mp4C2paUuid = new byte[]
    {
        0xd8, 0xfe, 0xc3, 0xd6, 0x1b, 0x0e, 0x48, 0x3c,
        0x92, 0x97, 0x58, 0x28, 0x87, 0x7e, 0xc4, 0x81
    };

    public C2paSigningService(IConfiguration configuration, ILogger<C2paSigningService> logger)
    {
        _logger = logger;
        _issuerName = configuration["C2pa:Issuer"] ?? "VisionX Trust Authority";

        var certPath = configuration["C2pa:CertificatePath"] ?? Environment.GetEnvironmentVariable("C2PA_CERT_PATH");
        var keyPath = configuration["C2pa:PrivateKeyPath"] ?? Environment.GetEnvironmentVariable("C2PA_PRIVATE_KEY_PATH");

        if (!string.IsNullOrWhiteSpace(certPath) && File.Exists(certPath) &&
            !string.IsNullOrWhiteSpace(keyPath) && File.Exists(keyPath))
        {
            try
            {
                var certPem = File.ReadAllText(certPath);
                var keyPem = File.ReadAllText(keyPath);
                _signingRsa = RSA.Create();
                _signingRsa.ImportFromPem(keyPem);
                _signingCert = X509Certificate2.CreateFromPem(certPem);
                _logger.LogInformation("Loaded C2PA signing key from files. Thumbprint: {Thumbprint}", _signingCert.Thumbprint);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load C2PA certificate/key from path. Generating secure authority keypair.");
            }
        }

        _signingRsa = RSA.Create(2048);
        var req = new CertificateRequest(
            "CN=VisionX Authenticity Authority, O=VisionX, C=US",
            _signingRsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        req.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(false, false, 0, false));
        req.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, false));

        _signingCert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
        _logger.LogInformation("Initialized VisionX C2PA Signing Authority. Thumbprint: {Thumbprint}", _signingCert.Thumbprint);
    }

    public Task<C2paManifestResult> CreateAndEmbedManifestAsync(
        byte[] mediaBytes,
        string fileName,
        string mimeType,
        string userId,
        SourceType sourceType,
        string? deviceModel = null,
        string? location = null,
        CancellationToken cancellationToken = default)
    {
        using var sha256 = SHA256.Create();
        var rawHashHex = Convert.ToHexStringLower(sha256.ComputeHash(mediaBytes));

        var actionName = sourceType == SourceType.Camera ? "c2pa.created" : "c2pa.opened";
        var digitalSourceType = sourceType == SourceType.Camera
            ? "http://cv.iptc.org/newscodes/digitalsourcetype/digitalCapture"
            : "http://cv.iptc.org/newscodes/digitalsourcetype/unknown";

        var manifestObj = new
        {
            claim_generator = "VisionX Media Provenance Engine v2.0",
            claim_generator_info = new { name = "VisionX", version = "2.0.0" },
            title = fileName,
            format = mimeType,
            instance_id = $"urn:uuid:{Guid.NewGuid()}",
            claim_time = DateTime.UtcNow.ToString("O"),
            issuer = _issuerName,
            assertions = new object[]
            {
                new
                {
                    label = "c2pa.actions",
                    data = new
                    {
                        actions = new[]
                        {
                            new
                            {
                                action = actionName,
                                digitalSourceType = digitalSourceType,
                                softwareAgent = "VisionX Camera Engine 2.0",
                                when = DateTime.UtcNow.ToString("O"),
                                parameters = new
                                {
                                    source = sourceType.ToString(),
                                    device = deviceModel ?? "Unknown Device",
                                    user_hash = Convert.ToHexStringLower(sha256.ComputeHash(Encoding.UTF8.GetBytes(userId)))
                                }
                            }
                        }
                    }
                },
                new
                {
                    label = "c2pa.hash.data",
                    data = new
                    {
                        name = "jumbf manifest binding",
                        pad = 0,
                        hash = rawHashHex,
                        alg = "sha256"
                    }
                }
            }
        };

        var manifestJson = JsonSerializer.Serialize(manifestObj, new JsonSerializerOptions { WriteIndented = false });
        var manifestBytes = Encoding.UTF8.GetBytes(manifestJson);

        var signatureBytes = _signingRsa.SignData(manifestBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var signatureHex = Convert.ToHexStringLower(signatureBytes);

        var embeddedBytes = EmbedManifestInMedia(mediaBytes, mimeType, manifestJson, signatureHex);

        return Task.FromResult(new C2paManifestResult(
            ManifestJson: manifestJson,
            Signature: signatureHex,
            Issuer: _issuerName,
            Algorithm: "RS256",
            EmbeddedMediaBytes: embeddedBytes
        ));
    }

    public async Task<C2paVerificationResult> VerifyMediaAsync(
        byte[] mediaBytes,
        string fileName,
        string mimeType,
        CancellationToken cancellationToken = default)
    {
        var detailed = await InspectDetailedProvenanceAsync(mediaBytes, fileName, mimeType, cancellationToken);

        if (!detailed.ManifestPresent)
        {
            return new C2paVerificationResult(
                Status: C2paStatus.NoCredentials,
                HasManifest: false,
                SignatureValid: false,
                HardBindingValid: false,
                Issuer: null,
                SignerThumbprint: null,
                ManifestJson: null,
                ClaimSummary: null,
                Message: "No C2PA Content Credentials or JUMBF provenance metadata found in media."
            );
        }

        C2paStatus status;
        string message;

        if (!detailed.StructureValid || !detailed.HardBindingValid)
        {
            status = C2paStatus.InvalidCredentials;
            message = "Media payload does not match C2PA cryptographic hard-binding digest or manifest structure is invalid.";
        }
        else if (!detailed.SignatureValid || !detailed.SignerTrusted)
        {
            status = C2paStatus.UntrustedSigner;
            message = "C2PA manifest signature could not be verified under VisionX trust policy.";
        }
        else if (detailed.RecordedActions.Any(a => a.Contains("c2pa.created", StringComparison.OrdinalIgnoreCase)) && !detailed.IsDerivative && !detailed.RecordsAiActions)
        {
            status = C2paStatus.VerifiedOriginal;
            message = "Valid VisionX C2PA Content Credentials and exact hard-binding verified.";
        }
        else
        {
            status = C2paStatus.VerifiedProvenanceTransformed;
            message = "C2PA provenance verified for edited/imported/derivative media.";
        }

        return new C2paVerificationResult(
            Status: status,
            HasManifest: true,
            SignatureValid: detailed.SignatureValid,
            HardBindingValid: detailed.HardBindingValid,
            Issuer: detailed.SignerIdentity,
            SignerThumbprint: detailed.SignerThumbprint,
            ManifestJson: detailed.ManifestJson,
            ClaimSummary: detailed.ClaimSummary,
            Message: message
        );
    }

    public Task<C2paDetailedInspectionResult> InspectDetailedProvenanceAsync(
        byte[] mediaBytes,
        string fileName,
        string mimeType,
        CancellationToken cancellationToken = default)
    {
        const string limitations = "C2PA provenance records the cryptographic history of a file and its declared assertions; it does not by itself prove the depicted real-world scene or rule out analog/pre-capture manipulation.";

        var extracted = ExtractManifestFromMedia(mediaBytes, mimeType);
        if (extracted == null)
        {
            // Also check for external standard JUMBF / OpenAI C2PA markers in container bytes
            var externalInfo = DetectExternalC2paMarkers(mediaBytes);
            if (externalInfo != null)
            {
                return Task.FromResult(new C2paDetailedInspectionResult(
                    ManifestPresent: true,
                    StructureValid: true,
                    SignatureValid: false,
                    SignerTrusted: false,
                    SignerIdentity: externalInfo.Value.Issuer,
                    SignerThumbprint: null,
                    HardBindingValid: false,
                    IsDerivative: true,
                    RecordsAiActions: externalInfo.Value.RecordsAi,
                    RecordedActions: externalInfo.Value.Actions,
                    IngredientRelationships: Array.Empty<string>(),
                    ManifestJson: null,
                    ClaimSummary: externalInfo.Value.Summary,
                    BoundHash: null,
                    Limitations: limitations
                ));
            }

            return Task.FromResult(new C2paDetailedInspectionResult(
                ManifestPresent: false,
                StructureValid: false,
                SignatureValid: false,
                SignerTrusted: false,
                SignerIdentity: null,
                SignerThumbprint: null,
                HardBindingValid: false,
                IsDerivative: false,
                RecordsAiActions: false,
                RecordedActions: Array.Empty<string>(),
                IngredientRelationships: Array.Empty<string>(),
                ManifestJson: null,
                ClaimSummary: null,
                BoundHash: null,
                Limitations: limitations
            ));
        }

        var (manifestJson, signatureHex, cleanMediaBytes) = extracted.Value;

        bool sigValid = false;
        try
        {
            var manifestBytes = Encoding.UTF8.GetBytes(manifestJson);
            var sigBytes = Convert.FromHexString(signatureHex);
            sigValid = _signingRsa.VerifyData(manifestBytes, sigBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to verify signature for media file {FileName}", fileName);
        }

        bool structureValid = false;
        string? boundHash = null;
        string? issuer = null;
        bool isDerivative = false;
        bool recordsAiActions = false;
        var recordedActions = new List<string>();
        var ingredients = new List<string>();

        try
        {
            using var doc = JsonDocument.Parse(manifestJson);
            structureValid = true;

            if (doc.RootElement.TryGetProperty("issuer", out var issElem))
            {
                issuer = issElem.GetString();
            }

            if (doc.RootElement.TryGetProperty("assertions", out var assertions) && assertions.ValueKind == JsonValueKind.Array)
            {
                foreach (var assertion in assertions.EnumerateArray())
                {
                    if (!assertion.TryGetProperty("label", out var labelElem)) continue;
                    var label = labelElem.GetString();

                    if (label == "c2pa.hash.data" && assertion.TryGetProperty("data", out var d) && d.TryGetProperty("hash", out var h))
                    {
                        boundHash = h.GetString();
                    }
                    else if (label == "c2pa.actions" && assertion.TryGetProperty("data", out var ad) && ad.TryGetProperty("actions", out var acts))
                    {
                        foreach (var act in acts.EnumerateArray())
                        {
                            var actionStr = act.TryGetProperty("action", out var aProp) ? aProp.GetString() : null;
                            var dstStr = act.TryGetProperty("digitalSourceType", out var dstProp) ? dstProp.GetString() : null;
                            if (!string.IsNullOrWhiteSpace(actionStr))
                            {
                                recordedActions.Add(string.IsNullOrWhiteSpace(dstStr) ? actionStr : $"{actionStr} ({dstStr})");
                                if (actionStr.Contains("edited", StringComparison.OrdinalIgnoreCase) ||
                                    actionStr.Contains("transformed", StringComparison.OrdinalIgnoreCase) ||
                                    actionStr.Contains("opened", StringComparison.OrdinalIgnoreCase))
                                {
                                    isDerivative = true;
                                }
                            }

                            if (!string.IsNullOrWhiteSpace(dstStr) &&
                                (dstStr.Contains("trainedAlgorithmicMedia", StringComparison.OrdinalIgnoreCase) ||
                                 dstStr.Contains("compositeWithTrainedAlgorithmicMedia", StringComparison.OrdinalIgnoreCase)))
                            {
                                recordsAiActions = true;
                            }
                        }
                    }
                    else if (label == "c2pa.ingredient" && assertion.TryGetProperty("data", out var ingData))
                    {
                        isDerivative = true;
                        var title = ingData.TryGetProperty("title", out var t) ? t.GetString() : "ingredient";
                        ingredients.Add(title ?? "ingredient");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse JSON assertions from C2PA manifest.");
        }

        using var sha256 = SHA256.Create();
        var currentCleanHash = Convert.ToHexStringLower(sha256.ComputeHash(cleanMediaBytes));
        var rawUploadedHash = Convert.ToHexStringLower(sha256.ComputeHash(mediaBytes));

        bool hardBindingValid = !string.IsNullOrEmpty(boundHash) &&
            (string.Equals(boundHash, currentCleanHash, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(boundHash, rawUploadedHash, StringComparison.OrdinalIgnoreCase));

        bool signerTrusted = sigValid && string.Equals(issuer, _issuerName, StringComparison.OrdinalIgnoreCase);

        return Task.FromResult(new C2paDetailedInspectionResult(
            ManifestPresent: true,
            StructureValid: structureValid,
            SignatureValid: sigValid,
            SignerTrusted: signerTrusted,
            SignerIdentity: issuer,
            SignerThumbprint: signerTrusted ? _signingCert.Thumbprint : null,
            HardBindingValid: hardBindingValid,
            IsDerivative: isDerivative,
            RecordsAiActions: recordsAiActions,
            RecordedActions: recordedActions,
            IngredientRelationships: ingredients,
            ManifestJson: manifestJson,
            ClaimSummary: $"Actions: {string.Join(", ", recordedActions)}; BoundHash: {boundHash ?? "None"}",
            BoundHash: boundHash,
            Limitations: limitations
        ));
    }

    private static (string Issuer, bool RecordsAi, List<string> Actions, string Summary)? DetectExternalC2paMarkers(byte[] mediaBytes)
    {
        var scanLen = Math.Min(mediaBytes.Length, 131072);
        var ascii = Encoding.Latin1.GetString(mediaBytes, 0, scanLen);
        bool hasJumb = ascii.Contains("jumb", StringComparison.Ordinal) && ascii.Contains("c2pa", StringComparison.OrdinalIgnoreCase);
        if (!hasJumb) return null;

        bool recordsAi = ascii.Contains("trainedAlgorithmicMedia", StringComparison.OrdinalIgnoreCase) ||
                         ascii.Contains("openai", StringComparison.OrdinalIgnoreCase) ||
                         ascii.Contains("dall-e", StringComparison.OrdinalIgnoreCase);

        string issuer = ascii.Contains("openai", StringComparison.OrdinalIgnoreCase) ? "openai.com (External JUMBF)" : "External JUMBF Signer (Unverified Trust Chain)";
        var actions = new List<string>();
        if (recordsAi) actions.Add("c2pa.created (trainedAlgorithmicMedia)");
        else actions.Add("external.c2pa.manifest");

        return (issuer, recordsAi, actions, "External JUMBF C2PA block detected; cryptographic chain requires external trust validation.");
    }

    private byte[] EmbedManifestInMedia(byte[] mediaBytes, string mimeType, string manifestJson, string signatureHex)
    {
        var manifestPayload = JsonSerializer.Serialize(new
        {
            magic = "VX_C2PA_JUMBF",
            manifest = manifestJson,
            sig = signatureHex
        });
        var payloadBytes = Encoding.UTF8.GetBytes(manifestPayload);

        if (mimeType.Contains("jpeg", StringComparison.OrdinalIgnoreCase) || mimeType.Contains("jpg", StringComparison.OrdinalIgnoreCase))
        {
            if (mediaBytes.Length >= 2 && mediaBytes[0] == 0xFF && mediaBytes[1] == 0xD8)
            {
                using var ms = new MemoryStream();
                ms.WriteByte(0xFF);
                ms.WriteByte(0xD8);

                var marker = new byte[] { 0xFF, 0xEB };
                var id = Encoding.ASCII.GetBytes("JP2C2PA\0");
                var totalSegmentLength = 2 + id.Length + payloadBytes.Length;

                if (totalSegmentLength <= 65535)
                {
                    ms.Write(marker);
                    ms.WriteByte((byte)((totalSegmentLength >> 8) & 0xFF));
                    ms.WriteByte((byte)(totalSegmentLength & 0xFF));
                    ms.Write(id);
                    ms.Write(payloadBytes);

                    ms.Write(mediaBytes.AsSpan(2));
                    return ms.ToArray();
                }
            }
        }
        else if (mimeType.Contains("png", StringComparison.OrdinalIgnoreCase))
        {
            if (mediaBytes.Length >= 8 && mediaBytes[0] == 0x89 && mediaBytes[1] == 0x50)
            {
                using var ms = new MemoryStream();
                var iendIndex = FindBytes(mediaBytes, Encoding.ASCII.GetBytes("IEND"));
                if (iendIndex > 4)
                {
                    var chunkOffset = iendIndex - 4;
                    ms.Write(mediaBytes, 0, chunkOffset);

                    var chunkType = Encoding.ASCII.GetBytes("caCI");
                    var lenBytes = BitConverter.GetBytes((uint)payloadBytes.Length);
                    if (BitConverter.IsLittleEndian) Array.Reverse(lenBytes);

                    ms.Write(lenBytes);
                    ms.Write(chunkType);
                    ms.Write(payloadBytes);

                    var crc = ComputeCrc32(chunkType.Concat(payloadBytes).ToArray());
                    var crcBytes = BitConverter.GetBytes(crc);
                    if (BitConverter.IsLittleEndian) Array.Reverse(crcBytes);
                    ms.Write(crcBytes);

                    ms.Write(mediaBytes, chunkOffset, mediaBytes.Length - chunkOffset);
                    return ms.ToArray();
                }
            }
        }
        else if (mimeType.Contains("mp4", StringComparison.OrdinalIgnoreCase) || mimeType.Contains("video", StringComparison.OrdinalIgnoreCase))
        {
            using var ms = new MemoryStream();
            ms.Write(mediaBytes);

            var boxLength = 4 + 4 + Mp4C2paUuid.Length + payloadBytes.Length;
            var boxLenBytes = BitConverter.GetBytes((uint)boxLength);
            if (BitConverter.IsLittleEndian) Array.Reverse(boxLenBytes);

            ms.Write(boxLenBytes);
            ms.Write(Encoding.ASCII.GetBytes("uuid"));
            ms.Write(Mp4C2paUuid);
            ms.Write(payloadBytes);

            return ms.ToArray();
        }

        using (var ms = new MemoryStream())
        {
            ms.Write(mediaBytes);
            var trailerTag = Encoding.ASCII.GetBytes("---BEGIN_VX_C2PA---");
            var trailerEnd = Encoding.ASCII.GetBytes("---END_VX_C2PA---");
            ms.Write(trailerTag);
            ms.Write(payloadBytes);
            ms.Write(trailerEnd);
            return ms.ToArray();
        }
    }

    private (string ManifestJson, string Signature, byte[] CleanBytes)? ExtractManifestFromMedia(byte[] mediaBytes, string mimeType)
    {
        if (mediaBytes.Length > 4 && mediaBytes[0] == 0xFF && mediaBytes[1] == 0xD8)
        {
            var app11Index = FindBytes(mediaBytes, new byte[] { 0xFF, 0xEB });
            if (app11Index >= 0 && app11Index + 4 < mediaBytes.Length)
            {
                int segLen = (mediaBytes[app11Index + 2] << 8) | mediaBytes[app11Index + 3];
                var id = Encoding.ASCII.GetBytes("JP2C2PA\0");
                if (app11Index + 4 + id.Length <= mediaBytes.Length)
                {
                    var idSlice = mediaBytes.AsSpan(app11Index + 4, id.Length);
                    if (idSlice.SequenceEqual(id))
                    {
                        var dataOffset = app11Index + 4 + id.Length;
                        var dataLen = segLen - 2 - id.Length;
                        if (dataLen > 0 && dataOffset + dataLen <= mediaBytes.Length)
                        {
                            var payloadJson = Encoding.UTF8.GetString(mediaBytes, dataOffset, dataLen);
                            var parsed = ParseManifestPayload(payloadJson);
                            if (parsed != null)
                            {
                                var clean = mediaBytes.Take(app11Index).Concat(mediaBytes.Skip(app11Index + 2 + segLen)).ToArray();
                                return (parsed.Value.ManifestJson, parsed.Value.Signature, clean);
                            }
                        }
                    }
                }
            }
        }

        var caciIndex = FindBytes(mediaBytes, Encoding.ASCII.GetBytes("caCI"));
        if (caciIndex >= 4)
        {
            var lenBytes = mediaBytes.AsSpan(caciIndex - 4, 4).ToArray();
            if (BitConverter.IsLittleEndian) Array.Reverse(lenBytes);
            var len = BitConverter.ToUInt32(lenBytes);
            if (caciIndex + 4 + len <= mediaBytes.Length)
            {
                var payloadJson = Encoding.UTF8.GetString(mediaBytes, caciIndex + 4, (int)len);
                var parsed = ParseManifestPayload(payloadJson);
                if (parsed != null)
                {
                    var totalChunkLen = 4 + 4 + (int)len + 4;
                    var clean = mediaBytes.Take(caciIndex - 4).Concat(mediaBytes.Skip(caciIndex - 4 + totalChunkLen)).ToArray();
                    return (parsed.Value.ManifestJson, parsed.Value.Signature, clean);
                }
            }
        }

        var uuidIndex = FindBytes(mediaBytes, Mp4C2paUuid);
        if (uuidIndex >= 8)
        {
            var boxLenBytes = mediaBytes.AsSpan(uuidIndex - 8, 4).ToArray();
            if (BitConverter.IsLittleEndian) Array.Reverse(boxLenBytes);
            var boxLen = BitConverter.ToUInt32(boxLenBytes);
            var dataOffset = uuidIndex + Mp4C2paUuid.Length;
            var dataLen = (int)boxLen - 8 - Mp4C2paUuid.Length;
            if (dataLen > 0 && dataOffset + dataLen <= mediaBytes.Length)
            {
                var payloadJson = Encoding.UTF8.GetString(mediaBytes, dataOffset, dataLen);
                var parsed = ParseManifestPayload(payloadJson);
                if (parsed != null)
                {
                    var clean = mediaBytes.Take(uuidIndex - 8).ToArray();
                    return (parsed.Value.ManifestJson, parsed.Value.Signature, clean);
                }
            }
        }

        var tag = Encoding.ASCII.GetBytes("---BEGIN_VX_C2PA---");
        var trailerIdx = FindBytes(mediaBytes, tag);
        if (trailerIdx >= 0)
        {
            var endTag = Encoding.ASCII.GetBytes("---END_VX_C2PA---");
            var endIdx = FindBytes(mediaBytes, endTag);
            if (endIdx > trailerIdx)
            {
                var start = trailerIdx + tag.Length;
                var len = endIdx - start;
                var payloadJson = Encoding.UTF8.GetString(mediaBytes, start, len);
                var parsed = ParseManifestPayload(payloadJson);
                if (parsed != null)
                {
                    var clean = mediaBytes.Take(trailerIdx).ToArray();
                    return (parsed.Value.ManifestJson, parsed.Value.Signature, clean);
                }
            }
        }

        return null;
    }

    private static (string ManifestJson, string Signature)? ParseManifestPayload(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.TryGetProperty("manifest", out var m) &&
                doc.RootElement.TryGetProperty("sig", out var s))
            {
                return (m.GetString()!, s.GetString()!);
            }
        }
        catch { /* ignore */ }
        return null;
    }

    private static int FindBytes(byte[] source, byte[] pattern)
    {
        for (int i = 0; i <= source.Length - pattern.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < pattern.Length; j++)
            {
                if (source[i + j] != pattern[j])
                {
                    match = false;
                    break;
                }
            }
            if (match) return i;
        }
        return -1;
    }

    private static uint ComputeCrc32(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        for (int i = 0; i < data.Length; i++)
        {
            byte b = data[i];
            crc ^= b;
            for (int j = 0; j < 8; j++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
            }
        }
        return ~crc;
    }
}

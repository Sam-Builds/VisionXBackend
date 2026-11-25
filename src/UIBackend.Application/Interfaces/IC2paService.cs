using UIBackend.Application.DTOs;
using UIBackend.Domain.Enums;

namespace UIBackend.Application.Interfaces;

public record C2paManifestResult(
    string ManifestJson,
    string Signature,
    string Issuer,
    string Algorithm,
    byte[] EmbeddedMediaBytes
);

public record C2paVerificationResult(
    C2paStatus Status,
    bool HasManifest,
    bool SignatureValid,
    bool HardBindingValid,
    string? Issuer,
    string? SignerThumbprint,
    string? ManifestJson,
    string? ClaimSummary,
    string Message
);

public interface IC2paService
{
    Task<C2paManifestResult> CreateAndEmbedManifestAsync(
        byte[] mediaBytes,
        string fileName,
        string mimeType,
        string userId,
        SourceType sourceType,
        string? deviceModel = null,
        string? location = null,
        CancellationToken cancellationToken = default
    );

    Task<C2paVerificationResult> VerifyMediaAsync(
        byte[] mediaBytes,
        string fileName,
        string mimeType,
        CancellationToken cancellationToken = default
    );
}

using UIBackend.Domain.Enums;

namespace UIBackend.Application.Interfaces;

public record C2paDetailedInspectionResult(
    bool ManifestPresent,
    bool StructureValid,
    bool SignatureValid,
    bool SignerTrusted,
    string? SignerIdentity,
    string? SignerThumbprint,
    bool HardBindingValid,
    bool IsDerivative,
    bool RecordsAiActions,
    IReadOnlyList<string> RecordedActions,
    IReadOnlyList<string> IngredientRelationships,
    string? ManifestJson,
    string? ClaimSummary,
    string? BoundHash,
    string Limitations
);

public interface IC2paVerificationProvider : IC2paService
{
    Task<C2paDetailedInspectionResult> InspectDetailedProvenanceAsync(
        byte[] mediaBytes,
        string fileName,
        string mimeType,
        CancellationToken cancellationToken = default
    );
}

public record AiProviderResult(
    string ProviderName,
    string ProviderVersion,
    EvidenceStatus Status,
    string ResultLabel,
    double? Confidence,
    string? ModelDetected,
    string? EvidenceJson,
    string? ErrorMessage,
    string Limitations,
    DateTime CheckedAtUtc
);

public interface IAiDetectionProvider
{
    string ProviderName { get; }
    Task<AiProviderResult> AnalyzeAsync(
        byte[] mediaBytes,
        string fileName,
        string mimeType,
        CancellationToken cancellationToken = default
    );
}

public record SuspiciousRegion(
    double X,
    double Y,
    double Width,
    double Height,
    double AnomalyScore,
    string Reason
);

public record ForensicAnalysisResult(
    string ProviderName,
    EvidenceStatus Status,
    IReadOnlyList<string> Findings,
    IReadOnlyList<SuspiciousRegion> SuspiciousRegions,
    IReadOnlyList<string> ExifAnomalies,
    string Limitations,
    DateTime CheckedAtUtc
);

public interface IImageForensicsProvider
{
    Task<ForensicAnalysisResult> AnalyzeAsync(
        byte[] mediaBytes,
        string fileName,
        string mimeType,
        CancellationToken cancellationToken = default
    );

    (bool IsPhoneOrigin, string Reason) ValidatePhoneCameraOrigin(
        byte[] mediaBytes,
        string fileName,
        string mimeType
    );
}

public interface IMediaFingerprintProvider
{
    string? ComputePerceptualDHash(byte[] mediaBytes, string mimeType);
    double ComputeSimilarity(string hashA, string hashB);
}

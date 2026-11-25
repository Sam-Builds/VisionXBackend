using UIBackend.Application.Interfaces;
using UIBackend.Domain.Enums;

namespace UIBackend.Application.DTOs;

public record MediaInputSummaryDto(
    string Type,
    string Sha256,
    string? PerceptualHash,
    string Format,
    long FileSizeBytes,
    string FileName
);

public record ProvenanceEvidenceDto(
    bool C2paPresent,
    bool StructureValid,
    bool SignatureValid,
    bool SignerTrusted,
    string? SignerIdentity,
    string? SignerThumbprint,
    bool HardBindingValid,
    bool SoftBindingMatch,
    bool IsDerivative,
    bool RecordsAiActions,
    IReadOnlyList<string> RecordedAiActions,
    IReadOnlyList<string> IngredientRelationships,
    string? ManifestJson,
    string Limitations
);

public record IntegrityEvidenceDto(
    bool ExactHashMatch,
    bool OriginalMediaMatch,
    bool Transformed,
    string RelationshipDescription
);

public record AiDetectionEvidenceDto(
    IReadOnlyList<AiProviderResult> Providers,
    IReadOnlyList<string> PositiveSignals,
    IReadOnlyList<string> NegativeSignals,
    IReadOnlyList<string> InconclusiveSignals
);

public record ForensicsEvidenceDto(
    EvidenceStatus Status,
    IReadOnlyList<string> Findings,
    IReadOnlyList<SuspiciousRegion> SuspiciousRegions,
    IReadOnlyList<string> ExifAnomalies,
    string Limitations
);

public record MatchedMediaEvidenceDto(
    Guid MatchedMediaItemId,
    string OriginalFileName,
    SourceType SourceType,
    DateTime CapturedAtUtc,
    string OwnerUserIdMasked,
    MatchMethod MatchMethod,
    double Similarity,
    double Confidence,
    bool ExactByteMatch,
    bool HasValidEmbeddedCredentials,
    bool DiffersFromOriginal,
    bool IsAmbiguous,
    string? OriginalStreamUrl
);

public record AssessmentSummaryDto(
    OverallAssessment Result,
    string BadgeLabel,
    string BadgeTone,
    string Explanation,
    IReadOnlyList<string> KeyFindings
);

public record EvidenceVerificationResponse(
    string SchemaVersion,
    Guid VerificationId,
    VerificationRunStatus Status,
    DateTime StartedAtUtc,
    DateTime? CompletedAtUtc,
    MediaInputSummaryDto Media,
    ProvenanceEvidenceDto Provenance,
    IntegrityEvidenceDto Integrity,
    AiDetectionEvidenceDto AiDetection,
    ForensicsEvidenceDto Forensics,
    IReadOnlyList<MatchedMediaEvidenceDto> Matches,
    AssessmentSummaryDto Assessment
);

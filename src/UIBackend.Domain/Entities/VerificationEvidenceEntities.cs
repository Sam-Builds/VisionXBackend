using UIBackend.Domain.Enums;

namespace UIBackend.Domain.Entities;

public class VerificationRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string? UserId { get; set; }
    public Guid? MediaItemId { get; set; }
    public string InputFileName { get; set; } = string.Empty;
    public string InputHash { get; set; } = string.Empty;
    public string? InputPerceptualHash { get; set; }
    public string InputFormat { get; set; } = string.Empty;
    public long InputSizeBytes { get; set; }
    public OverallAssessment OverallAssessment { get; set; } = OverallAssessment.Inconclusive;
    public string Explanation { get; set; } = string.Empty;
    public VerificationRunStatus Status { get; set; } = VerificationRunStatus.Pending;
    public DateTime StartedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAtUtc { get; set; }

    // Navigation properties
    public MediaItem? MediaItem { get; set; }
    public ICollection<ProvenanceCheck> ProvenanceChecks { get; set; } = new List<ProvenanceCheck>();
    public ICollection<AiDetectionCheck> AiDetectionChecks { get; set; } = new List<AiDetectionCheck>();
    public ICollection<ForensicCheck> ForensicChecks { get; set; } = new List<ForensicCheck>();
    public ICollection<MediaMatch> MediaMatches { get; set; } = new List<MediaMatch>();
}

public class ProvenanceCheck
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid VerificationRunId { get; set; }
    public string Provider { get; set; } = string.Empty;
    public bool ManifestPresent { get; set; }
    public bool StructureValid { get; set; }
    public bool SignatureValid { get; set; }
    public bool SignerTrusted { get; set; }
    public string? SignerIdentity { get; set; }
    public bool HardBindingValid { get; set; }
    public bool SoftBindingMatch { get; set; }
    public bool IsDerivative { get; set; }
    public bool RecordsAiActions { get; set; }
    public string? RecordedActionsJson { get; set; }
    public string? IngredientRelationshipsJson { get; set; }
    public string? ProvenanceDetailsJson { get; set; }
    public string? Limitations { get; set; }
    public DateTime CheckedAtUtc { get; set; } = DateTime.UtcNow;

    public VerificationRun VerificationRun { get; set; } = null!;
}

public class AiDetectionCheck
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid VerificationRunId { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string ProviderVersion { get; set; } = string.Empty;
    public EvidenceStatus DetectionStatus { get; set; } = EvidenceStatus.NotRun;
    public string ResultLabel { get; set; } = string.Empty;
    public double? Confidence { get; set; }
    public string? ModelDetected { get; set; }
    public string? EvidenceJson { get; set; }
    public string? ErrorMessage { get; set; }
    public string? Limitations { get; set; }
    public DateTime CheckedAtUtc { get; set; } = DateTime.UtcNow;

    public VerificationRun VerificationRun { get; set; } = null!;
}

public class ForensicCheck
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid VerificationRunId { get; set; }
    public string Provider { get; set; } = string.Empty;
    public EvidenceStatus Result { get; set; } = EvidenceStatus.Inconclusive;
    public string? FindingsJson { get; set; }
    public string? RegionDataJson { get; set; }
    public string? ExifAnomaliesJson { get; set; }
    public string? Limitations { get; set; }
    public DateTime CheckedAtUtc { get; set; } = DateTime.UtcNow;

    public VerificationRun VerificationRun { get; set; } = null!;
}

public class MediaMatch
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid VerificationRunId { get; set; }
    public Guid MatchedMediaItemId { get; set; }
    public MatchMethod MatchMethod { get; set; }
    public double Similarity { get; set; }
    public double Confidence { get; set; }
    public bool ExactByteMatch { get; set; }
    public bool HasValidEmbeddedCredentials { get; set; }
    public bool DiffersFromOriginal { get; set; }
    public bool IsAmbiguous { get; set; }

    public VerificationRun VerificationRun { get; set; } = null!;
    public MediaItem MatchedMediaItem { get; set; } = null!;
}

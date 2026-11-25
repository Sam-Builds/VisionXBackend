namespace UIBackend.Domain.Enums;

public enum SourceType
{
    Camera = 0,
    Import = 1
}

public enum MediaType
{
    Image = 0,
    Video = 1
}

public enum C2paStatus
{
    NoCredentials = 0,
    VerifiedOriginal = 1,
    VerifiedProvenanceTransformed = 2,
    UntrustedSigner = 3,
    InvalidCredentials = 4
}

public enum SynthIdStatus
{
    NotDetected = 0,
    Detected = 1,
    UnsupportedFormat = 2,
    ServiceUnavailable = 3
}

public enum AccessEventType
{
    View = 0,
    Play = 1,
    Download = 2,
    Verify = 3
}

public enum UploadStatus
{
    Pending = 0,
    Uploading = 1,
    Completed = 2,
    Failed = 3
}

/// <summary>
/// Multi-dimensional overall assessment categories replacing the simplistic boolean verified/unverified model.
/// </summary>
public enum OverallAssessment
{
    OriginalCaptureVerified = 0,
    ProvenanceVerifiedAiDetected = 1,
    ProvenanceVerifiedModified = 2,
    AiGenerationSuspected = 3,
    ManipulationSuspected = 4,
    ImportedNoProvenance = 5,
    InvalidOrContradictory = 6,
    Inconclusive = 7
}

public enum VerificationRunStatus
{
    Pending = 0,
    Processing = 1,
    Completed = 2,
    Failed = 3
}

public enum EvidenceStatus
{
    NotRun = 0,
    Positive = 1,
    Negative = 2,
    Inconclusive = 3,
    Unsupported = 4,
    Error = 5
}

public enum MatchMethod
{
    ExactSha256 = 0,
    ExactC2paHardBinding = 1,
    C2paSoftBinding = 2,
    PerceptualDHash = 3,
    VisualSimilarity = 4
}

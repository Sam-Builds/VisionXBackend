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

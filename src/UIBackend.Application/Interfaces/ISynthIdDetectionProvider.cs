using UIBackend.Domain.Enums;

namespace UIBackend.Application.Interfaces;

public record SynthIdDetectionResult(
    SynthIdStatus Status,
    double? Confidence,
    string? ModelVersion,
    string? RawResponse,
    string Details
);

public interface ISynthIdDetectionProvider
{
    Task<SynthIdDetectionResult> InspectMediaAsync(
        byte[] mediaBytes,
        string fileName,
        string mimeType,
        CancellationToken cancellationToken = default
    );
}

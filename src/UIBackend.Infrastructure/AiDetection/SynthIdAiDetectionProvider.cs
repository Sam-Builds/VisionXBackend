using UIBackend.Application.Interfaces;
using UIBackend.Domain.Enums;

namespace UIBackend.Infrastructure.AiDetection;

/// <summary>
/// Adapts the dedicated ISynthIdDetectionProvider into the multi-provider IAiDetectionProvider pipeline.
/// Keeps SynthID independent of C2PA and reports limitations transparently.
/// </summary>
public class SynthIdAiDetectionProvider : IAiDetectionProvider
{
    private readonly ISynthIdDetectionProvider _synthIdProvider;

    public string ProviderName => "Google SynthID Watermark Detector";

    public SynthIdAiDetectionProvider(ISynthIdDetectionProvider synthIdProvider)
    {
        _synthIdProvider = synthIdProvider;
    }

    public async Task<AiProviderResult> AnalyzeAsync(
        byte[] mediaBytes,
        string fileName,
        string mimeType,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        const string limitations = "Detects supported SynthID imperceptible watermarks only. A negative result does not prove an image is genuine or non-AI.";

        var res = await _synthIdProvider.InspectMediaAsync(mediaBytes, fileName, mimeType, cancellationToken);

        var status = res.Status switch
        {
            SynthIdStatus.Detected => EvidenceStatus.Positive,
            SynthIdStatus.NotDetected => EvidenceStatus.Negative,
            SynthIdStatus.UnsupportedFormat => EvidenceStatus.Unsupported,
            SynthIdStatus.ServiceUnavailable => EvidenceStatus.Error,
            _ => EvidenceStatus.Inconclusive
        };

        return new AiProviderResult(
            ProviderName: ProviderName,
            ProviderVersion: res.ModelVersion ?? "synthid-v1",
            Status: status,
            ResultLabel: res.Details,
            Confidence: res.Confidence,
            ModelDetected: res.Status == SynthIdStatus.Detected ? "SynthID-Watermarked" : null,
            EvidenceJson: res.RawResponse,
            ErrorMessage: res.Status == SynthIdStatus.ServiceUnavailable ? res.Details : null,
            Limitations: limitations,
            CheckedAtUtc: now
        );
    }
}

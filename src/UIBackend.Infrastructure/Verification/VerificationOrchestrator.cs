using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using UIBackend.Application.DTOs;
using UIBackend.Application.Interfaces;
using UIBackend.Domain.Entities;
using UIBackend.Domain.Enums;

namespace UIBackend.Infrastructure.Verification;

public interface IVerificationOrchestrator
{
    Task<EvidenceVerificationResponse> VerifyMediaAsync(
        byte[] mediaBytes,
        string fileName,
        string mimeType,
        string? userId,
        CancellationToken cancellationToken = default);

    Task<EvidenceVerificationResponse?> GetVerificationRunAsync(
        Guid verificationId,
        CancellationToken cancellationToken = default);
}

public class VerificationOrchestrator : IVerificationOrchestrator
{
    private readonly IUIBackendDbContext _dbContext;
    private readonly IMediaStorageService _storageService;
    private readonly IC2paVerificationProvider _c2paProvider;
    private readonly IEnumerable<IAiDetectionProvider> _aiProviders;
    private readonly IImageForensicsProvider _forensicsProvider;
    private readonly IMediaFingerprintProvider _fingerprintProvider;
    private readonly ILogger<VerificationOrchestrator> _logger;

    public VerificationOrchestrator(
        IUIBackendDbContext dbContext,
        IMediaStorageService storageService,
        IC2paVerificationProvider c2paProvider,
        IEnumerable<IAiDetectionProvider> aiProviders,
        IImageForensicsProvider forensicsProvider,
        IMediaFingerprintProvider fingerprintProvider,
        ILogger<VerificationOrchestrator> logger)
    {
        _dbContext = dbContext;
        _storageService = storageService;
        _c2paProvider = c2paProvider;
        _aiProviders = aiProviders;
        _forensicsProvider = forensicsProvider;
        _fingerprintProvider = fingerprintProvider;
        _logger = logger;
    }

    public async Task<EvidenceVerificationResponse> VerifyMediaAsync(
        byte[] mediaBytes,
        string fileName,
        string mimeType,
        string? userId,
        CancellationToken cancellationToken = default)
    {
        var startedAt = DateTime.UtcNow;
        var sha256 = _storageService.ComputeSha256(new MemoryStream(mediaBytes));
        var perceptualHash = _fingerprintProvider.ComputePerceptualDHash(mediaBytes, mimeType);
        var mediaTypeLabel = mimeType.StartsWith("video", StringComparison.OrdinalIgnoreCase) ? "video" : "image";
        var formatLabel = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(formatLabel)) formatLabel = mimeType.Split('/').LastOrDefault() ?? "unknown";

        // 1. Run independent evidence providers in parallel
        var c2paTask = _c2paProvider.InspectDetailedProvenanceAsync(mediaBytes, fileName, mimeType, cancellationToken);
        var forensicsTask = _forensicsProvider.AnalyzeAsync(mediaBytes, fileName, mimeType, cancellationToken);
        var aiTasks = _aiProviders.Select(p => SafeRunAiProviderAsync(p, mediaBytes, fileName, mimeType, cancellationToken)).ToList();

        await Task.WhenAll(c2paTask, forensicsTask, Task.WhenAll(aiTasks));

        var provenance = await c2paTask;
        var forensics = await forensicsTask;
        var aiResults = aiTasks.Select(t => t.Result).ToList();

        // 2. Multi-level Related Media Matching (Exact SHA-256 -> C2PA Bound Hash -> Perceptual dHash)
        var candidates = await _dbContext.MediaItems
            .Include(m => m.CaptureRecord)
            .Where(m => !m.IsDeleted)
            .OrderByDescending(m => m.CreatedAtUtc)
            .Take(500)
            .ToListAsync(cancellationToken);

        var matchedEvidence = new List<MatchedMediaEvidenceDto>();
        MediaItem? exactMatchItem = null;

        foreach (var item in candidates)
        {
            bool exactSha = string.Equals(item.OriginalSha256, sha256, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(item.SignedSha256, sha256, StringComparison.OrdinalIgnoreCase);

            bool c2paBoundMatch = !string.IsNullOrWhiteSpace(provenance.BoundHash) &&
                                  (string.Equals(item.OriginalSha256, provenance.BoundHash, StringComparison.OrdinalIgnoreCase) ||
                                   string.Equals(item.SignedSha256, provenance.BoundHash, StringComparison.OrdinalIgnoreCase));

            double dHashSim = (!string.IsNullOrWhiteSpace(perceptualHash) && !string.IsNullOrWhiteSpace(item.PerceptualHash))
                ? _fingerprintProvider.ComputeSimilarity(perceptualHash, item.PerceptualHash)
                : 0.0;

            if (exactSha)
            {
                exactMatchItem ??= item;
                matchedEvidence.Add(BuildMatchDto(item, MatchMethod.ExactSha256, 1.0, 1.0, true, provenance.SignatureValid && provenance.HardBindingValid, false, false));
            }
            else if (c2paBoundMatch)
            {
                matchedEvidence.Add(BuildMatchDto(item, MatchMethod.C2paSoftBinding, dHashSim > 0 ? dHashSim : 0.95, 0.9, false, provenance.SignatureValid, true, false));
            }
            else if (dHashSim >= 0.85)
            {
                matchedEvidence.Add(BuildMatchDto(item, MatchMethod.PerceptualDHash, Math.Round(dHashSim, 3), Math.Round(dHashSim, 3), false, false, true, false));
            }
        }

        if (matchedEvidence.Count > 1)
        {
            matchedEvidence = matchedEvidence.Select(m => m with { IsAmbiguous = true }).ToList();
        }

        // 3. Apply Mandatory Decision Rules
        var assessment = EvaluateAssessment(provenance, aiResults, forensics, matchedEvidence, exactMatchItem);

        var completedAt = DateTime.UtcNow;
        var run = new VerificationRun
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            MediaItemId = exactMatchItem?.Id ?? matchedEvidence.FirstOrDefault()?.MatchedMediaItemId,
            InputFileName = fileName,
            InputHash = sha256,
            InputPerceptualHash = perceptualHash,
            InputFormat = formatLabel,
            InputSizeBytes = mediaBytes.Length,
            OverallAssessment = assessment.Result,
            Explanation = assessment.Explanation,
            Status = VerificationRunStatus.Completed,
            StartedAtUtc = startedAt,
            CompletedAtUtc = completedAt
        };

        run.ProvenanceChecks.Add(new ProvenanceCheck
        {
            VerificationRunId = run.Id,
            Provider = "VisionX C2PA Engine",
            ManifestPresent = provenance.ManifestPresent,
            StructureValid = provenance.StructureValid,
            SignatureValid = provenance.SignatureValid,
            SignerTrusted = provenance.SignerTrusted,
            SignerIdentity = provenance.SignerIdentity,
            HardBindingValid = provenance.HardBindingValid,
            SoftBindingMatch = matchedEvidence.Any(m => m.MatchMethod == MatchMethod.C2paSoftBinding),
            IsDerivative = provenance.IsDerivative,
            RecordsAiActions = provenance.RecordsAiActions,
            RecordedActionsJson = JsonSerializer.Serialize(provenance.RecordedActions),
            IngredientRelationshipsJson = JsonSerializer.Serialize(provenance.IngredientRelationships),
            ProvenanceDetailsJson = provenance.ManifestJson,
            Limitations = provenance.Limitations,
            CheckedAtUtc = completedAt
        });

        foreach (var ai in aiResults)
        {
            run.AiDetectionChecks.Add(new AiDetectionCheck
            {
                VerificationRunId = run.Id,
                Provider = ai.ProviderName,
                ProviderVersion = ai.ProviderVersion,
                DetectionStatus = ai.Status,
                ResultLabel = ai.ResultLabel,
                Confidence = ai.Confidence,
                ModelDetected = ai.ModelDetected,
                EvidenceJson = ai.EvidenceJson,
                ErrorMessage = ai.ErrorMessage,
                Limitations = ai.Limitations,
                CheckedAtUtc = ai.CheckedAtUtc
            });
        }

        run.ForensicChecks.Add(new ForensicCheck
        {
            VerificationRunId = run.Id,
            Provider = forensics.ProviderName,
            Result = forensics.Status,
            FindingsJson = JsonSerializer.Serialize(forensics.Findings),
            RegionDataJson = JsonSerializer.Serialize(forensics.SuspiciousRegions),
            ExifAnomaliesJson = JsonSerializer.Serialize(forensics.ExifAnomalies),
            Limitations = forensics.Limitations,
            CheckedAtUtc = forensics.CheckedAtUtc
        });

        foreach (var match in matchedEvidence)
        {
            run.MediaMatches.Add(new MediaMatch
            {
                VerificationRunId = run.Id,
                MatchedMediaItemId = match.MatchedMediaItemId,
                MatchMethod = match.MatchMethod,
                Similarity = match.Similarity,
                Confidence = match.Confidence,
                ExactByteMatch = match.ExactByteMatch,
                HasValidEmbeddedCredentials = match.HasValidEmbeddedCredentials,
                DiffersFromOriginal = match.DiffersFromOriginal,
                IsAmbiguous = match.IsAmbiguous
            });
        }

        _dbContext.VerificationRuns.Add(run);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return MapRunToResponse(run, mediaTypeLabel, provenance, aiResults, forensics, matchedEvidence, assessment, exactMatchItem);
    }

    public async Task<EvidenceVerificationResponse?> GetVerificationRunAsync(
        Guid verificationId,
        CancellationToken cancellationToken = default)
    {
        var run = await _dbContext.VerificationRuns
            .Include(r => r.ProvenanceChecks)
            .Include(r => r.AiDetectionChecks)
            .Include(r => r.ForensicChecks)
            .Include(r => r.MediaMatches)
            .ThenInclude(m => m.MatchedMediaItem)
            .ThenInclude(mi => mi.CaptureRecord)
            .FirstOrDefaultAsync(r => r.Id == verificationId, cancellationToken);

        if (run == null) return null;

        var prov = run.ProvenanceChecks.FirstOrDefault();
        var provenanceDto = new ProvenanceEvidenceDto(
            C2paPresent: prov?.ManifestPresent ?? false,
            StructureValid: prov?.StructureValid ?? false,
            SignatureValid: prov?.SignatureValid ?? false,
            SignerTrusted: prov?.SignerTrusted ?? false,
            SignerIdentity: prov?.SignerIdentity,
            SignerThumbprint: null,
            HardBindingValid: prov?.HardBindingValid ?? false,
            SoftBindingMatch: prov?.SoftBindingMatch ?? false,
            IsDerivative: prov?.IsDerivative ?? false,
            RecordsAiActions: prov?.RecordsAiActions ?? false,
            RecordedAiActions: DeserializeList(prov?.RecordedActionsJson),
            IngredientRelationships: DeserializeList(prov?.IngredientRelationshipsJson),
            ManifestJson: prov?.ProvenanceDetailsJson,
            Limitations: prov?.Limitations ?? ""
        );

        var aiResults = run.AiDetectionChecks.Select(a => new AiProviderResult(
            a.Provider, a.ProviderVersion, a.DetectionStatus, a.ResultLabel, a.Confidence, a.ModelDetected, a.EvidenceJson, a.ErrorMessage, a.Limitations ?? "", a.CheckedAtUtc
        )).ToList();

        var f = run.ForensicChecks.FirstOrDefault();
        var forensicsDto = new ForensicsEvidenceDto(
            Status: f?.Result ?? EvidenceStatus.NotRun,
            Findings: DeserializeList(f?.FindingsJson),
            SuspiciousRegions: DeserializeRegions(f?.RegionDataJson),
            ExifAnomalies: DeserializeList(f?.ExifAnomaliesJson),
            Limitations: f?.Limitations ?? ""
        );

        var matches = run.MediaMatches.Select(m => BuildMatchDto(
            m.MatchedMediaItem, m.MatchMethod, m.Similarity, m.Confidence, m.ExactByteMatch, m.HasValidEmbeddedCredentials, m.DiffersFromOriginal, m.IsAmbiguous
        )).ToList();

        var exactMatch = matches.Any(m => m.ExactByteMatch);
        var relatedMatch = matches.Count > 0;
        var mediaType = run.InputFormat is "mp4" or "mov" ? "video" : "image";

        var (badgeLabel, badgeTone) = GetBadgeInfo(run.OverallAssessment);
        return new EvidenceVerificationResponse(
            SchemaVersion: "2.0",
            VerificationId: run.Id,
            Status: run.Status,
            StartedAtUtc: run.StartedAtUtc,
            CompletedAtUtc: run.CompletedAtUtc,
            Media: new MediaInputSummaryDto(mediaType, run.InputHash, run.InputPerceptualHash, run.InputFormat, run.InputSizeBytes, run.InputFileName),
            Provenance: provenanceDto,
            Integrity: new IntegrityEvidenceDto(
                ExactHashMatch: exactMatch,
                OriginalMediaMatch: matches.Any(m => m.SourceType == SourceType.Camera),
                Transformed: relatedMatch && !exactMatch,
                RelationshipDescription: DescribeRelationship(matches, null)
            ),
            AiDetection: BuildAiEvidenceDto(aiResults),
            Forensics: forensicsDto,
            Matches: matches,
            Assessment: new AssessmentSummaryDto(run.OverallAssessment, badgeLabel, badgeTone, run.Explanation, new[] { run.Explanation })
        );
    }

    private static AssessmentSummaryDto EvaluateAssessment(
        C2paDetailedInspectionResult provenance,
        List<AiProviderResult> aiResults,
        ForensicAnalysisResult forensics,
        List<MatchedMediaEvidenceDto> matches,
        MediaItem? exactMatchItem)
    {
        var keyFindings = new List<string>();
        bool aiPositive = aiResults.Any(p => p.Status == EvidenceStatus.Positive) || provenance.RecordsAiActions;
        bool invalidCreds = provenance.ManifestPresent &&
            (!provenance.StructureValid || (provenance.SignerTrusted && !provenance.HardBindingValid));

        bool isExactVisionXCamera = exactMatchItem != null &&
            exactMatchItem.SourceType == SourceType.Camera &&
            provenance.ManifestPresent &&
            provenance.SignatureValid &&
            provenance.SignerTrusted &&
            provenance.HardBindingValid &&
            !provenance.IsDerivative &&
            !aiPositive &&
            forensics.Status != EvidenceStatus.Positive;

        if (aiPositive)
        {
            keyFindings.Add("AI-generation or AI-watermark signals were detected.");
            if (provenance.ManifestPresent && provenance.SignatureValid)
            {
                keyFindings.Add("Provenance metadata is present alongside AI signals.");
                return new AssessmentSummaryDto(
                    OverallAssessment.ProvenanceVerifiedAiDetected,
                    "Not Verified (AI)",
                    "danger",
                    "This image is NOT verified as a real photograph. AI-generated content signals were detected — do not take this image as authentic.",
                    keyFindings
                );
            }

            return new AssessmentSummaryDto(
                OverallAssessment.AiGenerationSuspected,
                "Not Verified (AI)",
                "danger",
                "This image is NOT verified. AI-generation signals were detected — do not treat this image as a genuine photograph.",
                keyFindings
            );
        }

        if (invalidCreds)
        {
            keyFindings.Add("Embedded C2PA credentials are invalid or the cryptographic hard-binding does not match the file bytes.");
            return new AssessmentSummaryDto(
                OverallAssessment.InvalidOrContradictory,
                "Not Verified",
                "danger",
                "This image is NOT verified. Its cryptographic credentials are invalid or broken, meaning the file was altered after signing.",
                keyFindings
            );
        }

        if (isExactVisionXCamera)
        {
            keyFindings.Add("Registered as captured through VisionX with valid RS256 signature and intact SHA-256 hard binding.");
            keyFindings.Add("No contradictory AI or localized forensic manipulation signals detected.");
            return new AssessmentSummaryDto(
                OverallAssessment.OriginalCaptureVerified,
                "Verified",
                "success",
                "Verified original VisionX capture with intact cryptographic file integrity.",
                keyFindings
            );
        }

        if (matches.Count > 0)
        {
            if (exactMatchItem != null && exactMatchItem.SourceType == SourceType.Import)
            {
                keyFindings.Add("This file was imported without valid camera capture proof.");
                if (forensics.Status == EvidenceStatus.Positive)
                {
                    keyFindings.AddRange(forensics.Findings);
                    return new AssessmentSummaryDto(
                        OverallAssessment.ManipulationSuspected,
                        "Not Verified",
                        "danger",
                        "This image is NOT verified. Forensic checks found suspicious anomalies and no trusted camera origin exists.",
                        keyFindings
                    );
                }

                return new AssessmentSummaryDto(
                    OverallAssessment.ImportedNoProvenance,
                    "Not Verified",
                    "danger",
                    "This image is NOT verified. No trusted origin or camera capture proof was found — do not treat it as an authentic photograph.",
                    keyFindings
                );
            }

            keyFindings.Add("This file resembles a previously registered image, but was modified or compressed (e.g. via WhatsApp).");
            if (matches.Any(m => m.IsAmbiguous))
            {
                keyFindings.Add("Multiple potential visual matches found; manual side-by-side comparison is recommended.");
            }
            return new AssessmentSummaryDto(
                OverallAssessment.ProvenanceVerifiedModified,
                "Not Verified (Modified)",
                "warning",
                "This exact file is NOT verified as original. It matches a registered image, but has been modified or recompressed.",
                keyFindings
            );
        }

        if (forensics.Status == EvidenceStatus.Positive)
        {
            keyFindings.AddRange(forensics.Findings);
            return new AssessmentSummaryDto(
                OverallAssessment.ManipulationSuspected,
                "Not Verified (Edited)",
                "danger",
                "This image is NOT verified. Forensic analysis detected suspicious regions or metadata anomalies — do not rely on it as unaltered.",
                keyFindings
            );
        }

        if (provenance.ManifestPresent && !provenance.SignerTrusted)
        {
            keyFindings.Add($"External C2PA manifest found (Signer: {provenance.SignerIdentity ?? "Unknown"}), outside VisionX trust list.");
            return new AssessmentSummaryDto(
                OverallAssessment.Inconclusive,
                "Not Verified",
                "warning",
                "This image is NOT verified. It comes from an untrusted or external signer — do not assume it is authentic.",
                keyFindings
            );
        }

        keyFindings.Add("No C2PA credentials, registered VisionX match, or trusted camera origin found.");
        return new AssessmentSummaryDto(
            OverallAssessment.ImportedNoProvenance,
            "Not Verified",
            "danger",
            "This image is NOT verified. No trusted origin or camera capture proof was found — do not take this image seriously as an authentic photograph.",
            keyFindings
        );
    }

    private async Task<AiProviderResult> SafeRunAiProviderAsync(
        IAiDetectionProvider provider,
        byte[] mediaBytes,
        string fileName,
        string mimeType,
        CancellationToken ct)
    {
        try
        {
            return await provider.AnalyzeAsync(mediaBytes, fileName, mimeType, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AI provider {Provider} threw unhandled exception.", provider.ProviderName);
            return new AiProviderResult(
                provider.ProviderName, "unknown", EvidenceStatus.Error, "Provider Error", null, null, null, ex.Message,
                "Provider encountered an unexpected error.", DateTime.UtcNow
            );
        }
    }

    private static MatchedMediaEvidenceDto BuildMatchDto(
        MediaItem item,
        MatchMethod method,
        double similarity,
        double confidence,
        bool exactByteMatch,
        bool hasValidCreds,
        bool differs,
        bool isAmbiguous)
    {
        var maskedUser = string.IsNullOrWhiteSpace(item.UserId) || item.UserId.Length <= 4
            ? "****"
            : $"{item.UserId[..2]}****{item.UserId[^2..]}";

        return new MatchedMediaEvidenceDto(
            MatchedMediaItemId: item.Id,
            OriginalFileName: item.OriginalFileName,
            SourceType: item.SourceType,
            CapturedAtUtc: item.CaptureRecord?.CapturedAtUtc ?? item.CreatedAtUtc,
            OwnerUserIdMasked: maskedUser,
            MatchMethod: method,
            Similarity: similarity,
            Confidence: confidence,
            ExactByteMatch: exactByteMatch,
            HasValidEmbeddedCredentials: hasValidCreds,
            DiffersFromOriginal: differs,
            IsAmbiguous: isAmbiguous,
            OriginalStreamUrl: $"/a/media/{item.Id}"
        );
    }

    private static EvidenceVerificationResponse MapRunToResponse(
        VerificationRun run,
        string mediaTypeLabel,
        C2paDetailedInspectionResult provenance,
        List<AiProviderResult> aiResults,
        ForensicAnalysisResult forensics,
        List<MatchedMediaEvidenceDto> matches,
        AssessmentSummaryDto assessment,
        MediaItem? exactMatchItem)
    {
        bool exactMatch = exactMatchItem != null;
        bool relatedMatch = matches.Count > 0;

        return new EvidenceVerificationResponse(
            SchemaVersion: "2.0",
            VerificationId: run.Id,
            Status: run.Status,
            StartedAtUtc: run.StartedAtUtc,
            CompletedAtUtc: run.CompletedAtUtc,
            Media: new MediaInputSummaryDto(mediaTypeLabel, run.InputHash, run.InputPerceptualHash, run.InputFormat, run.InputSizeBytes, run.InputFileName),
            Provenance: new ProvenanceEvidenceDto(
                C2paPresent: provenance.ManifestPresent,
                StructureValid: provenance.StructureValid,
                SignatureValid: provenance.SignatureValid,
                SignerTrusted: provenance.SignerTrusted,
                SignerIdentity: provenance.SignerIdentity,
                SignerThumbprint: provenance.SignerThumbprint,
                HardBindingValid: provenance.HardBindingValid,
                SoftBindingMatch: matches.Any(m => m.MatchMethod == MatchMethod.C2paSoftBinding),
                IsDerivative: provenance.IsDerivative,
                RecordsAiActions: provenance.RecordsAiActions,
                RecordedAiActions: provenance.RecordedActions,
                IngredientRelationships: provenance.IngredientRelationships,
                ManifestJson: provenance.ManifestJson,
                Limitations: provenance.Limitations
            ),
            Integrity: new IntegrityEvidenceDto(
                ExactHashMatch: exactMatch,
                OriginalMediaMatch: matches.Any(m => m.SourceType == SourceType.Camera),
                Transformed: relatedMatch && !exactMatch,
                RelationshipDescription: DescribeRelationship(matches, exactMatchItem)
            ),
            AiDetection: BuildAiEvidenceDto(aiResults),
            Forensics: new ForensicsEvidenceDto(
                Status: forensics.Status,
                Findings: forensics.Findings,
                SuspiciousRegions: forensics.SuspiciousRegions,
                ExifAnomalies: forensics.ExifAnomalies,
                Limitations: forensics.Limitations
            ),
            Matches: matches,
            Assessment: assessment
        );
    }

    private static AiDetectionEvidenceDto BuildAiEvidenceDto(List<AiProviderResult> aiResults)
    {
        return new AiDetectionEvidenceDto(
            Providers: aiResults,
            PositiveSignals: aiResults.Where(p => p.Status == EvidenceStatus.Positive).Select(p => $"{p.ProviderName}: {p.ResultLabel}").ToList(),
            NegativeSignals: aiResults.Where(p => p.Status == EvidenceStatus.Negative).Select(p => $"{p.ProviderName}: {p.ResultLabel}").ToList(),
            InconclusiveSignals: aiResults.Where(p => p.Status is EvidenceStatus.Inconclusive or EvidenceStatus.Unsupported or EvidenceStatus.Error).Select(p => $"{p.ProviderName}: {p.ResultLabel}").ToList()
        );
    }

    private static string DescribeRelationship(List<MatchedMediaEvidenceDto> matches, MediaItem? exactMatchItem)
    {
        if (exactMatchItem != null)
        {
            return exactMatchItem.SourceType == SourceType.Camera
                ? "Exact byte-for-byte SHA-256 match with a registered VisionX camera capture."
                : "Exact SHA-256 match with an imported and registered VisionX record.";
        }
        if (matches.Count > 0)
        {
            return "Related to a previously registered VisionX image via soft-binding or perceptual fingerprint; exact byte integrity differs.";
        }
        return "No matching registered VisionX asset found.";
    }

    private static (string Label, string Tone) GetBadgeInfo(OverallAssessment assessment) => assessment switch
    {
        OverallAssessment.OriginalCaptureVerified => ("High assurance", "success"),
        OverallAssessment.ProvenanceVerifiedAiDetected => ("AI signal detected", "danger"),
        OverallAssessment.ProvenanceVerifiedModified => ("Modified", "warning"),
        OverallAssessment.AiGenerationSuspected => ("Inconclusive evidence", "warning"),
        OverallAssessment.ManipulationSuspected => ("Forensic anomalies", "warning"),
        OverallAssessment.ImportedNoProvenance => ("Unverified origin", "neutral"),
        OverallAssessment.InvalidOrContradictory => ("Verification failed", "danger"),
        _ => ("Inconclusive", "warning")
    };

    private static IReadOnlyList<string> DeserializeList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<string>();
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>(); }
        catch { return Array.Empty<string>(); }
    }

    private static IReadOnlyList<SuspiciousRegion> DeserializeRegions(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<SuspiciousRegion>();
        try { return JsonSerializer.Deserialize<List<SuspiciousRegion>>(json) ?? new List<SuspiciousRegion>(); }
        catch { return Array.Empty<SuspiciousRegion>(); }
    }
}

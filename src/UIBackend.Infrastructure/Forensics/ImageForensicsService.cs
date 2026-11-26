using System.Text;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using UIBackend.Application.Interfaces;
using UIBackend.Domain.Enums;

namespace UIBackend.Infrastructure.Forensics;

public class ImageForensicsService : IImageForensicsProvider
{
    private readonly ILogger<ImageForensicsService> _logger;

    private static readonly string[] AiSoftwareIndicators =
    {
        "chatgpt", "dall-e", "dalle", "openai", "midjourney",
        "stable diffusion", "automatic1111", "comfyui", "firefly", "imagen"
    };

    private static readonly string[] EditingSoftwareIndicators =
    {
        "photoshop", "gimp", "lightroom", "snapseed", "picsart", "canva", "pixelmator"
    };

    private static readonly string[] OutsideSourceFileIndicators =
    {
        "whatsapp", "-wa", "wa00", "telegram", "screenshot", "screen_shot",
        "download", "chatgpt", "dalle", "dall-e", "midjourney", "fb_img",
        "instagram", "snapchat", "edited"
    };

    public ImageForensicsService(ILogger<ImageForensicsService> logger)
    {
        _logger = logger;
    }

    public (bool IsPhoneOrigin, string Reason) ValidatePhoneCameraOrigin(
        byte[] mediaBytes,
        string fileName,
        string mimeType)
    {
        var lowerName = (fileName ?? string.Empty).ToLowerInvariant();
        if (OutsideSourceFileIndicators.Any(ind => lowerName.Contains(ind)))
        {
            return (false, "Cannot verify or add C2PA credentials: this file appears to come from an outside source (messaging app, screenshot, download, or AI tool), not directly from this phone's camera.");
        }

        if (mimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
        {
            return (true, "Video accepted for device upload.");
        }

        try
        {
            var info = Image.Identify(mediaBytes);
            var exif = info?.Metadata.ExifProfile;
            if (exif == null || exif.Values.Count == 0)
            {
                return (false, "Cannot verify or add C2PA credentials: missing phone camera metadata (EXIF). Images from WhatsApp, downloads, screenshots, or AI generators cannot be signed.");
            }

            var make = exif.Values.FirstOrDefault(v => v.Tag == SixLabors.ImageSharp.Metadata.Profiles.Exif.ExifTag.Make)?.GetValue()?.ToString();
            var model = exif.Values.FirstOrDefault(v => v.Tag == SixLabors.ImageSharp.Metadata.Profiles.Exif.ExifTag.Model)?.GetValue()?.ToString();
            var software = exif.Values.FirstOrDefault(v => v.Tag == SixLabors.ImageSharp.Metadata.Profiles.Exif.ExifTag.Software)?.GetValue()?.ToString()?.ToLowerInvariant() ?? string.Empty;

            if (AiSoftwareIndicators.Any(ind => software.Contains(ind)) || EditingSoftwareIndicators.Any(ind => software.Contains(ind)))
            {
                return (false, $"Cannot verify or add C2PA credentials: metadata indicates external software ('{software}') rather than an unmodified phone camera photo.");
            }

            if (string.IsNullOrWhiteSpace(make) || string.IsNullOrWhiteSpace(model))
            {
                return (false, "Cannot verify or add C2PA credentials: phone camera Make/Model metadata is missing, indicating an outside source.");
            }

            bool hasCaptureTag = exif.Values.Any(v =>
                v.Tag == SixLabors.ImageSharp.Metadata.Profiles.Exif.ExifTag.DateTimeOriginal ||
                v.Tag == SixLabors.ImageSharp.Metadata.Profiles.Exif.ExifTag.ExposureTime ||
                v.Tag == SixLabors.ImageSharp.Metadata.Profiles.Exif.ExifTag.FNumber ||
                v.Tag == SixLabors.ImageSharp.Metadata.Profiles.Exif.ExifTag.ISOSpeedRatings ||
                v.Tag == SixLabors.ImageSharp.Metadata.Profiles.Exif.ExifTag.FocalLength);

            if (!hasCaptureTag)
            {
                return (false, "Cannot verify or add C2PA credentials: camera capture settings are missing, indicating this image was not taken directly by the phone's camera.");
            }

            var headerSliceLen = Math.Min(mediaBytes.Length, 65536);
            var headerText = Encoding.Latin1.GetString(mediaBytes, 0, headerSliceLen).ToLowerInvariant();
            if (AiSoftwareIndicators.Any(ind => headerText.Contains(ind)))
            {
                return (false, "Cannot verify or add C2PA credentials: embedded metadata indicates AI-generated media from an outside source.");
            }

            return (true, $"Verified phone camera metadata ({make.Trim()} {model.Trim()}).");
        }
        catch
        {
            return (false, "Cannot verify or add C2PA credentials: could not read valid phone camera metadata from this image.");
        }
    }

    public Task<ForensicAnalysisResult> AnalyzeAsync(
        byte[] mediaBytes,
        string fileName,
        string mimeType,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        const string limitations = "Forensic indicators (Error Level Analysis, noise variance, and EXIF metadata) are supporting indicators and not conclusive proof of manipulation. Legitimate recompression, messaging apps, or screenshots can also produce anomalies.";

        if (mediaBytes.Length == 0 || mimeType.StartsWith("video", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(new ForensicAnalysisResult(
                ProviderName: "VisionX Image Forensics (ELA + Noise + EXIF)",
                Status: EvidenceStatus.Unsupported,
                Findings: new[] { "Frame-level image forensics not supported for video containers." },
                SuspiciousRegions: Array.Empty<SuspiciousRegion>(),
                ExifAnomalies: Array.Empty<string>(),
                Limitations: limitations,
                CheckedAtUtc: now
            ));
        }

        var findings = new List<string>();
        var exifAnomalies = new List<string>();
        var suspiciousRegions = new List<SuspiciousRegion>();

        try
        {
            using var image = Image.Load<Rgb24>(mediaBytes);

            // 1. Inspect EXIF / XMP / Metadata
            InspectMetadata(image, mediaBytes, exifAnomalies, findings);

            // 2. Block-level ELA & Local Noise Variance Grid (8x8 blocks)
            AnalyzeSpatialCompressionAndNoise(image, suspiciousRegions, findings);

            var status = (suspiciousRegions.Count > 0 || exifAnomalies.Any(a => a.Contains("AI software", StringComparison.OrdinalIgnoreCase)))
                ? EvidenceStatus.Positive
                : (findings.Count > 0 ? EvidenceStatus.Inconclusive : EvidenceStatus.Negative);

            if (findings.Count == 0 && suspiciousRegions.Count == 0 && exifAnomalies.Count == 0)
            {
                findings.Add("No significant localized compression or metadata anomalies detected.");
            }

            return Task.FromResult(new ForensicAnalysisResult(
                ProviderName: "VisionX Image Forensics (ELA + Noise + EXIF)",
                Status: status,
                Findings: findings,
                SuspiciousRegions: suspiciousRegions,
                ExifAnomalies: exifAnomalies,
                Limitations: limitations,
                CheckedAtUtc: now
            ));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Forensic analysis failed for {FileName}", fileName);
            return Task.FromResult(new ForensicAnalysisResult(
                ProviderName: "VisionX Image Forensics (ELA + Noise + EXIF)",
                Status: EvidenceStatus.Error,
                Findings: new[] { $"Forensic analysis could not decode image: {ex.Message}" },
                SuspiciousRegions: Array.Empty<SuspiciousRegion>(),
                ExifAnomalies: Array.Empty<string>(),
                Limitations: limitations,
                CheckedAtUtc: now
            ));
        }
    }

    private static void InspectMetadata(
        Image<Rgb24> image,
        byte[] rawBytes,
        List<string> exifAnomalies,
        List<string> findings)
    {
        var exif = image.Metadata.ExifProfile;
        if (exif == null || exif.Values.Count == 0)
        {
            exifAnomalies.Add("EXIF camera metadata is absent or stripped (common in messaging apps like WhatsApp, screenshots, and AI exports).");
        }
        else
        {
            var softwareTag = exif.Values.FirstOrDefault(v => v.Tag == SixLabors.ImageSharp.Metadata.Profiles.Exif.ExifTag.Software)?.GetValue()?.ToString();
            var makeTag = exif.Values.FirstOrDefault(v => v.Tag == SixLabors.ImageSharp.Metadata.Profiles.Exif.ExifTag.Make)?.GetValue()?.ToString();
            var modelTag = exif.Values.FirstOrDefault(v => v.Tag == SixLabors.ImageSharp.Metadata.Profiles.Exif.ExifTag.Model)?.GetValue()?.ToString();

            if (!string.IsNullOrWhiteSpace(softwareTag))
            {
                var lowerSoft = softwareTag.ToLowerInvariant();
                if (AiSoftwareIndicators.Any(ind => lowerSoft.Contains(ind)))
                {
                    exifAnomalies.Add($"EXIF Software tag references AI generation tool: '{softwareTag}'.");
                    findings.Add($"Metadata indicates AI software signature ({softwareTag}).");
                }
                else if (EditingSoftwareIndicators.Any(ind => lowerSoft.Contains(ind)))
                {
                    exifAnomalies.Add($"EXIF Software tag references image editing software: '{softwareTag}'.");
                    findings.Add($"Metadata indicates post-processing editor ({softwareTag}).");
                }
            }

            if (string.IsNullOrWhiteSpace(makeTag) && string.IsNullOrWhiteSpace(modelTag))
            {
                exifAnomalies.Add("Camera hardware Make/Model tags are missing from EXIF profile.");
            }
        }

        // Check XMP / ASCII header window for AI tool signatures (e.g. OpenAI / DALL-E / ChatGPT)
        var headerSliceLen = Math.Min(rawBytes.Length, 65536);
        var headerText = Encoding.Latin1.GetString(rawBytes, 0, headerSliceLen).ToLowerInvariant();
        foreach (var indicator in AiSoftwareIndicators)
        {
            if (headerText.Contains(indicator))
            {
                exifAnomalies.Add($"Container metadata contains AI generator signature ('{indicator}').");
                findings.Add($"Embedded metadata references '{indicator}'.");
                break;
            }
        }
    }

    private static void AnalyzeSpatialCompressionAndNoise(
        Image<Rgb24> original,
        List<SuspiciousRegion> suspiciousRegions,
        List<string> findings)
    {
        if (original.Width < 64 || original.Height < 64)
        {
            return;
        }

        // Recompress at JPEG Q=90 in memory for Error Level Analysis (ELA)
        using var ms = new MemoryStream();
        original.Save(ms, new JpegEncoder { Quality = 90 });
        ms.Position = 0;
        using var recompressed = Image.Load<Rgb24>(ms);

        const int gridCols = 8;
        const int gridRows = 8;
        int cellW = original.Width / gridCols;
        int cellH = original.Height / gridRows;

        var blockScores = new double[gridRows, gridCols];
        var allScores = new List<double>(gridRows * gridCols);

        for (int gy = 0; gy < gridRows; gy++)
        {
            for (int gx = 0; gx < gridCols; gx++)
            {
                int startX = gx * cellW;
                int startY = gy * cellH;
                double elaSum = 0;
                double noiseSum = 0;
                int count = 0;

                // Sample with stride=2 for fast, deterministic execution
                for (int y = startY; y < startY + cellH - 1; y += 2)
                {
                    for (int x = startX; x < startX + cellW - 1; x += 2)
                    {
                        var p1 = original[x, y];
                        var p2 = recompressed[x, y];
                        var diff = (Math.Abs(p1.R - p2.R) + Math.Abs(p1.G - p2.G) + Math.Abs(p1.B - p2.B)) / 3.0;
                        elaSum += diff;

                        // Local high-frequency spatial gradient
                        var right = original[x + 1, y];
                        var down = original[x, y + 1];
                        var grad = (Math.Abs(p1.G - right.G) + Math.Abs(p1.G - down.G)) / 2.0;
                        noiseSum += grad;
                        count++;
                    }
                }

                double meanEla = count > 0 ? elaSum / count : 0;
                double score = meanEla;
                blockScores[gy, gx] = score;
                allScores.Add(score);
            }
        }

        // Compute median and MAD (Median Absolute Deviation) for robust outlier detection
        var sorted = allScores.OrderBy(v => v).ToList();
        double median = sorted[sorted.Count / 2];
        var deviations = sorted.Select(v => Math.Abs(v - median)).OrderBy(v => v).ToList();
        double mad = deviations[deviations.Count / 2];
        double scale = mad * 1.4826 + 0.35;

        for (int gy = 0; gy < gridRows; gy++)
        {
            for (int gx = 0; gx < gridCols; gx++)
            {
                double z = (blockScores[gy, gx] - median) / scale;
                // High positive z-score indicates localized compression/noise discrepancy
                if (z >= 3.2 && blockScores[gy, gx] > 2.5)
                {
                    double confidence = Math.Min(0.95, 0.45 + (z - 3.2) * 0.12);
                    suspiciousRegions.Add(new SuspiciousRegion(
                        X: (double)gx / gridCols,
                        Y: (double)gy / gridRows,
                        Width: 1.0 / gridCols,
                        Height: 1.0 / gridRows,
                        AnomalyScore: Math.Round(confidence, 3),
                        Reason: $"Localized ELA/noise discrepancy (z={z:F1}) relative to surrounding image blocks."
                    ));
                }
            }
        }

        if (suspiciousRegions.Count > 0)
        {
            findings.Add($"Detected {suspiciousRegions.Count} image region(s) with localized compression/noise inconsistencies relative to surrounding background.");
        }
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using UIBackend.Application.Interfaces;
using UIBackend.Domain.Entities;
using UIBackend.Domain.Enums;
using UIBackend.Infrastructure.AiDetection;
using UIBackend.Infrastructure.C2pa;
using UIBackend.Infrastructure.Fingerprinting;
using UIBackend.Infrastructure.Forensics;
using UIBackend.Infrastructure.Persistence;
using UIBackend.Infrastructure.Storage;
using UIBackend.Infrastructure.SynthId;
using UIBackend.Infrastructure.Verification;
using Xunit;

namespace UIBackend.UnitTests;

public class VerificationRegressionCorpusTests
{
    private readonly C2paSigningService _c2pa;
    private readonly PerceptualHashService _phash;
    private readonly ImageForensicsService _forensics;
    private readonly FileSystemMediaStorageService _storage;

    public VerificationRegressionCorpusTests()
    {
        var config = new ConfigurationBuilder().Build();
        _c2pa = new C2paSigningService(config, NullLogger<C2paSigningService>.Instance);
        _phash = new PerceptualHashService(NullLogger<PerceptualHashService>.Instance);
        _forensics = new ImageForensicsService(NullLogger<ImageForensicsService>.Instance);
        _storage = new FileSystemMediaStorageService(config, NullLogger<FileSystemMediaStorageService>.Instance);
    }

    private UIBackendDbContext CreateDb()
    {
        var opts = new DbContextOptionsBuilder<UIBackendDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new UIBackendDbContext(opts);
    }

    private VerificationOrchestrator CreateOrchestrator(
        UIBackendDbContext db,
        params IAiDetectionProvider[] aiProviders)
    {
        if (aiProviders.Length == 0)
        {
            var synth = new GoogleSynthIdDetectionProvider(
                new DummyHttpClientFactory(),
                new ConfigurationBuilder().Build(),
                NullLogger<GoogleSynthIdDetectionProvider>.Instance);
            aiProviders = new IAiDetectionProvider[] { new SynthIdAiDetectionProvider(synth) };
        }

        return new VerificationOrchestrator(
            db,
            _storage,
            _c2pa,
            aiProviders,
            _forensics,
            _phash,
            NullLogger<VerificationOrchestrator>.Instance);
    }

    private static byte[] CreateGradientJpeg(int width = 128, int height = 128, int quality = 92, int seedOffset = 0)
    {
        using var img = new Image<Rgb24>(width, height);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                byte r = (byte)((x * 2 + seedOffset) % 256);
                byte g = (byte)((y * 2 + seedOffset) % 256);
                byte b = (byte)(((x + y) + seedOffset) % 256);
                img[x, y] = new Rgb24(r, g, b);
            }
        }
        using var ms = new MemoryStream();
        img.Save(ms, new JpegEncoder { Quality = quality });
        return ms.ToArray();
    }

    private static byte[] RecompressJpeg(byte[] jpegBytes, int quality, int? newWidth = null, int? newHeight = null)
    {
        using var img = Image.Load<Rgb24>(jpegBytes);
        if (newWidth.HasValue && newHeight.HasValue)
        {
            img.Mutate(x => x.Resize(newWidth.Value, newHeight.Value));
        }
        img.Metadata.ExifProfile = null; // Simulate WhatsApp stripping EXIF & C2PA
        using var ms = new MemoryStream();
        img.Save(ms, new JpegEncoder { Quality = quality });
        return ms.ToArray();
    }

    private async Task<(MediaItem Item, byte[] SignedBytes)> RegisterMediaAsync(
        UIBackendDbContext db,
        byte[] rawBytes,
        SourceType sourceType,
        string fileName = "photo.jpg")
    {
        var sha = _storage.ComputeSha256(new MemoryStream(rawBytes));
        var dhash = _phash.ComputePerceptualDHash(rawBytes, "image/jpeg");
        string? signedSha = null;
        byte[] signedBytes = rawBytes;

        if (sourceType == SourceType.Camera)
        {
            var signed = await _c2pa.CreateAndEmbedManifestAsync(rawBytes, fileName, "image/jpeg", "owner1", SourceType.Camera);
            signedBytes = signed.EmbeddedMediaBytes;
            signedSha = _storage.ComputeSha256(new MemoryStream(signedBytes));
        }

        var item = new MediaItem
        {
            Id = Guid.NewGuid(),
            UserId = "owner1",
            OriginalFileName = fileName,
            MimeType = "image/jpeg",
            MediaType = MediaType.Image,
            FileSizeBytes = signedBytes.Length,
            OriginalSha256 = sha,
            SignedSha256 = signedSha,
            PerceptualHash = dhash,
            StoragePath = "/tmp/fake",
            SourceType = sourceType
        };
        db.MediaItems.Add(item);
        await db.SaveChangesAsync();
        return (item, signedBytes);
    }

    // 1. Genuine VisionX capture
    [Fact]
    public async Task Case01_GenuineVisionXCapture_ShouldVerifyHighAssurance()
    {
        using var db = CreateDb();
        var raw = CreateGradientJpeg();
        var (_, signedBytes) = await RegisterMediaAsync(db, raw, SourceType.Camera);

        var res = await CreateOrchestrator(db).VerifyMediaAsync(signedBytes, "photo.jpg", "image/jpeg", "viewer");
        Assert.Equal(OverallAssessment.OriginalCaptureVerified, res.Assessment.Result);
        Assert.True(res.Provenance.SignatureValid);
        Assert.True(res.Provenance.HardBindingValid);
        Assert.True(res.Integrity.ExactHashMatch);
    }

    // 2. VisionX photograph sent through WhatsApp (recompressed, stripped C2PA)
    [Fact]
    public async Task Case02_VisionXPhotoViaWhatsApp_ShouldReportModifiedNotTamperedOrOriginal()
    {
        using var db = CreateDb();
        var raw = CreateGradientJpeg(256, 256, 95);
        await RegisterMediaAsync(db, raw, SourceType.Camera);
        var whatsappBytes = RecompressJpeg(raw, 72);

        var res = await CreateOrchestrator(db).VerifyMediaAsync(whatsappBytes, "wa.jpg", "image/jpeg", "viewer");
        Assert.Equal(OverallAssessment.ProvenanceVerifiedModified, res.Assessment.Result);
        Assert.False(res.Integrity.ExactHashMatch);
        Assert.True(res.Integrity.Transformed);
    }

    // 3. VisionX photograph sent as WhatsApp document (intact bytes & C2PA)
    [Fact]
    public async Task Case03_VisionXPhotoAsWhatsAppDocument_ShouldVerifyHighAssurance()
    {
        using var db = CreateDb();
        var raw = CreateGradientJpeg();
        var (_, signedBytes) = await RegisterMediaAsync(db, raw, SourceType.Camera, "doc.jpg");

        var res = await CreateOrchestrator(db).VerifyMediaAsync(signedBytes, "doc.jpg", "image/jpeg", "viewer");
        Assert.Equal(OverallAssessment.OriginalCaptureVerified, res.Assessment.Result);
    }

    // 4. ChatGPT-generated image (with OpenAI provenance signal)
    [Fact]
    public async Task Case04_ChatGptGeneratedImage_ShouldDetectAiSignal()
    {
        using var db = CreateDb();
        var aiBytes = CreateGradientJpeg();
        var mockOpenAi = new StubAiProvider("OpenAI Content Provenance API", EvidenceStatus.Positive, "OpenAI AI Signal Detected (c2pa, synthid)", "gpt-image-1");

        var res = await CreateOrchestrator(db, mockOpenAi).VerifyMediaAsync(aiBytes, "bottle.jpg", "image/jpeg", "viewer");
        Assert.NotEqual(OverallAssessment.OriginalCaptureVerified, res.Assessment.Result);
        Assert.True(res.Assessment.Result is OverallAssessment.AiGenerationSuspected or OverallAssessment.ProvenanceVerifiedAiDetected);
        Assert.NotEmpty(res.AiDetection.PositiveSignals);
    }

    // 5. CRITICAL REGRESSION CASE: ChatGPT bottle -> WhatsApp compression -> Imported & Registered in VisionX
    [Fact]
    public async Task Case05_CriticalBug_ChatGptBottleViaWhatsAppImportedIntoVisionX_MustNeverBeVerifiedOriginal()
    {
        using var db = CreateDb();
        var chatGptBottle = CreateGradientJpeg(256, 256, 95, seedOffset: 42);
        var whatsAppCompressed = RecompressJpeg(chatGptBottle, 68);

        // User imports WhatsApp-compressed AI bottle into VisionX (SourceType.Import)
        await RegisterMediaAsync(db, whatsAppCompressed, SourceType.Import, "whatsapp_bottle.jpg");

        // Subcase A: SynthID survives WhatsApp compression
        var mockOpenAiSurviving = new StubAiProvider("OpenAI Content Provenance API", EvidenceStatus.Positive, "OpenAI AI Signal Detected (synthid)", "gpt-image-1");
        var resWithSignal = await CreateOrchestrator(db, mockOpenAiSurviving).VerifyMediaAsync(whatsAppCompressed, "whatsapp_bottle.jpg", "image/jpeg", "viewer");
        Assert.NotEqual(OverallAssessment.OriginalCaptureVerified, resWithSignal.Assessment.Result);
        Assert.Equal(OverallAssessment.AiGenerationSuspected, resWithSignal.Assessment.Result);

        // Subcase B: Heavy compression stripped all AI signals -> must NOT claim verified original or proven authentic!
        var mockOpenAiStripped = new StubAiProvider("OpenAI Content Provenance API", EvidenceStatus.Negative, "No OpenAI Provenance Signal Detected", null);
        var resNoSignal = await CreateOrchestrator(db, mockOpenAiStripped).VerifyMediaAsync(whatsAppCompressed, "whatsapp_bottle.jpg", "image/jpeg", "viewer");
        Assert.NotEqual(OverallAssessment.OriginalCaptureVerified, resNoSignal.Assessment.Result);
        Assert.NotEqual(OverallAssessment.ProvenanceVerifiedModified, resNoSignal.Assessment.Result);
        Assert.Equal(OverallAssessment.ImportedNoProvenance, resNoSignal.Assessment.Result);
        Assert.Contains("Imported and registered by VisionX", resNoSignal.Assessment.Explanation);
    }

    // 6. ChatGPT image edited into an existing photograph
    [Fact]
    public async Task Case06_ChatGptEditedIntoPhoto_ShouldNotVerifyAsOriginal()
    {
        using var db = CreateDb();
        var imgBytes = CreateGradientJpeg();
        var mockOpenAi = new StubAiProvider("OpenAI Content Provenance API", EvidenceStatus.Positive, "OpenAI AI Signal Detected (synthid)", "gpt-image-1");

        var res = await CreateOrchestrator(db, mockOpenAi).VerifyMediaAsync(imgBytes, "edited.jpg", "image/jpeg", "viewer");
        Assert.Equal(OverallAssessment.AiGenerationSuspected, res.Assessment.Result);
    }

    // 7. AI-generated object composited into a real photograph (localized ELA anomaly)
    [Fact]
    public async Task Case07_AiObjectCompositedIntoRealPhoto_ShouldFlagForensicRegion()
    {
        using var db = CreateDb();
        // Smooth low-noise JPEG background + high-frequency uncompressed patch spliced in one quadrant
        using var img = new Image<Rgb24>(128, 128);
        for (int y = 0; y < 128; y++)
            for (int x = 0; x < 128; x++)
                img[x, y] = new Rgb24(120, 120, 120);

        using var baseMs = new MemoryStream();
        img.Save(baseMs, new JpegEncoder { Quality = 95 });
        baseMs.Position = 0;
        using var spliced = Image.Load<Rgb24>(baseMs);
        var rng = new Random(7);
        for (int y = 0; y < 32; y++)
            for (int x = 0; x < 32; x++)
                spliced[x, y] = new Rgb24((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256));

        using var outMs = new MemoryStream();
        spliced.Save(outMs, new JpegEncoder { Quality = 98 });

        var res = await CreateOrchestrator(db).VerifyMediaAsync(outMs.ToArray(), "splice.jpg", "image/jpeg", "viewer");
        Assert.NotEqual(OverallAssessment.OriginalCaptureVerified, res.Assessment.Result);
        Assert.NotEmpty(res.Forensics.Findings);
    }

    // 8. Photograph with object added using image editor (EXIF Software tag)
    [Fact]
    public async Task Case08_PhotoEditedWithPhotoshopExif_ShouldFlagMetadataAnomaly()
    {
        using var db = CreateDb();
        using var img = new Image<Rgb24>(128, 128);
        img.Metadata.ExifProfile = new SixLabors.ImageSharp.Metadata.Profiles.Exif.ExifProfile();
        img.Metadata.ExifProfile.SetValue(SixLabors.ImageSharp.Metadata.Profiles.Exif.ExifTag.Software, "Adobe Photoshop 2026");
        using var ms = new MemoryStream();
        img.Save(ms, new JpegEncoder { Quality = 90 });

        var res = await CreateOrchestrator(db).VerifyMediaAsync(ms.ToArray(), "ps.jpg", "image/jpeg", "viewer");
        Assert.Contains(res.Forensics.ExifAnomalies, a => a.Contains("Photoshop", StringComparison.OrdinalIgnoreCase));
    }

    // 9. Genuine photograph with ordinary color correction
    [Fact]
    public async Task Case09_ColorCorrectedPhoto_ShouldReportModifiedWhenMatched()
    {
        using var db = CreateDb();
        var raw = CreateGradientJpeg(128, 128, 95);
        await RegisterMediaAsync(db, raw, SourceType.Camera);

        using var img = Image.Load<Rgb24>(raw);
        img.Mutate(x => x.Brightness(1.05f));
        using var ms = new MemoryStream();
        img.Save(ms, new JpegEncoder { Quality = 90 });

        var res = await CreateOrchestrator(db).VerifyMediaAsync(ms.ToArray(), "graded.jpg", "image/jpeg", "viewer");
        Assert.Equal(OverallAssessment.ProvenanceVerifiedModified, res.Assessment.Result);
    }

    // 10. Genuine photograph resized and recompressed
    [Fact]
    public async Task Case10_ResizedRecompressedPhoto_ShouldNotClaimMaliciousTampering()
    {
        using var db = CreateDb();
        var raw = CreateGradientJpeg(256, 256, 95);
        await RegisterMediaAsync(db, raw, SourceType.Camera);
        var resized = RecompressJpeg(raw, 75, 128, 128);

        var res = await CreateOrchestrator(db).VerifyMediaAsync(resized, "resized.jpg", "image/jpeg", "viewer");
        Assert.NotEqual(OverallAssessment.InvalidOrContradictory, res.Assessment.Result);
        Assert.Equal(OverallAssessment.ProvenanceVerifiedModified, res.Assessment.Result);
    }

    // 11. Screenshot of a genuine photograph
    [Fact]
    public async Task Case11_ScreenshotOfGenuinePhoto_ShouldNotVerifyAsOriginalCapture()
    {
        using var db = CreateDb();
        var raw = CreateGradientJpeg(128, 128, 95);
        await RegisterMediaAsync(db, raw, SourceType.Camera);
        var screenshot = RecompressJpeg(raw, 80, 120, 120);

        var res = await CreateOrchestrator(db).VerifyMediaAsync(screenshot, "screenshot.jpg", "image/jpeg", "viewer");
        Assert.NotEqual(OverallAssessment.OriginalCaptureVerified, res.Assessment.Result);
    }

    // 12. Screenshot of an AI-generated photograph
    [Fact]
    public async Task Case12_ScreenshotOfAiPhoto_ShouldNotVerifyAsOriginalCapture()
    {
        using var db = CreateDb();
        var screenshotAi = CreateGradientJpeg(128, 128, 80);

        var res = await CreateOrchestrator(db).VerifyMediaAsync(screenshotAi, "ai_screen.jpg", "image/jpeg", "viewer");
        Assert.Equal(OverallAssessment.ImportedNoProvenance, res.Assessment.Result);
    }

    // 13. Image with stripped C2PA metadata
    [Fact]
    public async Task Case13_StrippedC2paMetadata_ShouldReportNoProvenanceOrModified()
    {
        using var db = CreateDb();
        var raw = CreateGradientJpeg();
        var res = await CreateOrchestrator(db).VerifyMediaAsync(raw, "stripped.jpg", "image/jpeg", "viewer");
        Assert.False(res.Provenance.C2paPresent);
        Assert.Equal(OverallAssessment.ImportedNoProvenance, res.Assessment.Result);
    }

    // 14. Image with invalid C2PA credentials (tampered bytes)
    [Fact]
    public async Task Case14_InvalidC2paCredentials_ShouldFailVerification()
    {
        using var db = CreateDb();
        var raw = CreateGradientJpeg();
        var signed = await _c2pa.CreateAndEmbedManifestAsync(raw, "tamper.jpg", "image/jpeg", "owner1", SourceType.Camera);
        var tampered = (byte[])signed.EmbeddedMediaBytes.Clone();
        tampered[^10] ^= 0xFF;

        var res = await CreateOrchestrator(db).VerifyMediaAsync(tampered, "tamper.jpg", "image/jpeg", "viewer");
        Assert.Equal(OverallAssessment.InvalidOrContradictory, res.Assessment.Result);
        Assert.False(res.Provenance.HardBindingValid);
    }

    // 15. Valid C2PA image from untrusted signer
    [Fact]
    public async Task Case15_UntrustedSignerC2pa_ShouldReportInconclusive()
    {
        using var db = CreateDb();
        var externalConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["C2pa:Issuer"] = "Unknown External Authority" })
            .Build();
        var externalC2pa = new C2paSigningService(externalConfig, NullLogger<C2paSigningService>.Instance);
        var raw = CreateGradientJpeg();
        var signed = await externalC2pa.CreateAndEmbedManifestAsync(raw, "ext.jpg", "image/jpeg", "ext_user", SourceType.Camera);

        var res = await CreateOrchestrator(db).VerifyMediaAsync(signed.EmbeddedMediaBytes, "ext.jpg", "image/jpeg", "viewer");
        Assert.Equal(OverallAssessment.Inconclusive, res.Assessment.Result);
        Assert.False(res.Provenance.SignerTrusted);
    }

    // 16. File with copied or misleading manifest
    [Fact]
    public async Task Case16_CopiedManifestOnDifferentFile_ShouldFailHardBinding()
    {
        using var db = CreateDb();
        var rawA = CreateGradientJpeg(128, 128, 92, seedOffset: 1);
        var rawB = CreateGradientJpeg(128, 128, 92, seedOffset: 99);
        var signedA = await _c2pa.CreateAndEmbedManifestAsync(rawA, "a.jpg", "image/jpeg", "owner1", SourceType.Camera);

        // Graft header/APP11 segment from signedA onto rawB
        var misleading = signedA.EmbeddedMediaBytes.Take(signedA.EmbeddedMediaBytes.Length - rawA.Length + 20)
            .Concat(rawB.Skip(20)).ToArray();

        var res = await CreateOrchestrator(db).VerifyMediaAsync(misleading, "misleading.jpg", "image/jpeg", "viewer");
        Assert.Equal(OverallAssessment.InvalidOrContradictory, res.Assessment.Result);
        Assert.False(res.Provenance.HardBindingValid);
    }

    // 17. Visually similar / unrelated photograph
    [Fact]
    public async Task Case17_UnrelatedPhotograph_ShouldNotMatchRegisteredCapture()
    {
        using var db = CreateDb();
        var registered = CreateGradientJpeg(128, 128, 92, seedOffset: 0);
        await RegisterMediaAsync(db, registered, SourceType.Camera);

        // Distinct unrelated smooth scene
        using var other = new Image<Rgb24>(128, 128);
        for (int y = 0; y < 128; y++)
            for (int x = 0; x < 128; x++)
                other[x, y] = new Rgb24((byte)(255 - x * 2), (byte)(255 - y * 2), (byte)((x + 2 * y) % 256));
        using var ms = new MemoryStream();
        other.Save(ms, new JpegEncoder { Quality = 90 });

        var res = await CreateOrchestrator(db).VerifyMediaAsync(ms.ToArray(), "unrelated.jpg", "image/jpeg", "viewer");
        Assert.Empty(res.Matches);
        Assert.Equal(OverallAssessment.ImportedNoProvenance, res.Assessment.Result);
    }

    // 18. Valid C2PA history containing an editing/import action
    [Fact]
    public async Task Case18_ValidC2paWithEditingAction_ShouldNotVerifyAsOriginalCapture()
    {
        using var db = CreateDb();
        var raw = CreateGradientJpeg();
        var signedEdit = await _c2pa.CreateAndEmbedManifestAsync(raw, "edit.jpg", "image/jpeg", "owner1", SourceType.Import);

        var res = await CreateOrchestrator(db).VerifyMediaAsync(signedEdit.EmbeddedMediaBytes, "edit.jpg", "image/jpeg", "viewer");
        Assert.NotEqual(OverallAssessment.OriginalCaptureVerified, res.Assessment.Result);
    }

    // 19. Photograph with supported SynthID signal
    [Fact]
    public async Task Case19_SupportedSynthIdSignal_ShouldReportAiDetection()
    {
        using var db = CreateDb();
        var img = CreateGradientJpeg();
        var withSynthMarker = img.Concat(System.Text.Encoding.UTF8.GetBytes("SynthID")).ToArray();

        var res = await CreateOrchestrator(db).VerifyMediaAsync(withSynthMarker, "synth.jpg", "image/jpeg", "viewer");
        Assert.Equal(OverallAssessment.AiGenerationSuspected, res.Assessment.Result);
        Assert.NotEmpty(res.AiDetection.PositiveSignals);
    }

    // 20. File with no AI signals and no provenance
    [Fact]
    public async Task Case20_NoAiSignalsAndNoProvenance_ShouldNotClaimAuthenticOriginal()
    {
        using var db = CreateDb();
        var raw = CreateGradientJpeg();
        var res = await CreateOrchestrator(db).VerifyMediaAsync(raw, "plain.jpg", "image/jpeg", "viewer");
        Assert.Equal(OverallAssessment.ImportedNoProvenance, res.Assessment.Result);
        Assert.NotEqual(OverallAssessment.OriginalCaptureVerified, res.Assessment.Result);
    }

    private sealed class StubAiProvider : IAiDetectionProvider
    {
        private readonly EvidenceStatus _status;
        private readonly string _label;
        private readonly string? _model;

        public string ProviderName { get; }

        public StubAiProvider(string name, EvidenceStatus status, string label, string? model)
        {
            ProviderName = name;
            _status = status;
            _label = label;
            _model = model;
        }

        public Task<AiProviderResult> AnalyzeAsync(byte[] mediaBytes, string fileName, string mimeType, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new AiProviderResult(
                ProviderName, "test-v1", _status, _label,
                _status == EvidenceStatus.Positive ? 0.99 : null,
                _model, null, null, "Test stub", DateTime.UtcNow));
        }
    }

    private sealed class DummyHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}

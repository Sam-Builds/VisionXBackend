using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using UIBackend.Domain.Enums;
using UIBackend.Infrastructure.C2pa;
using Xunit;

namespace UIBackend.UnitTests;

public class C2paSigningServiceTests
{
    private readonly C2paSigningService _service;

    public C2paSigningServiceTests()
    {
        var config = new ConfigurationBuilder().Build();
        _service = new C2paSigningService(config, NullLogger<C2paSigningService>.Instance);
    }

    [Fact]
    public async Task CreateAndEmbedManifest_ShouldEmbedValidCredentials()
    {
        // Arrange
        var fakeJpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0xFF, 0xD9 };
        var fileName = "test_photo.jpg";
        var mimeType = "image/jpeg";
        var userId = "user_12345";

        // Act
        var manifestResult = await _service.CreateAndEmbedManifestAsync(
            fakeJpeg,
            fileName,
            mimeType,
            userId,
            SourceType.Camera,
            deviceModel: "Pixel 9 Pro"
        );

        // Assert
        Assert.NotNull(manifestResult);
        Assert.NotEmpty(manifestResult.ManifestJson);
        Assert.NotEmpty(manifestResult.Signature);
        Assert.True(manifestResult.EmbeddedMediaBytes.Length > fakeJpeg.Length);

        // Verify the newly created media
        var verification = await _service.VerifyMediaAsync(manifestResult.EmbeddedMediaBytes, fileName, mimeType);
        Assert.True(verification.HasManifest);
        Assert.True(verification.SignatureValid);
        Assert.True(verification.HardBindingValid);
        Assert.Equal(C2paStatus.VerifiedOriginal, verification.Status);
    }

    [Fact]
    public async Task VerifyMedia_WhenTamperedBytes_ShouldFailHardBinding()
    {
        // Arrange
        var fakeJpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0xFF, 0xD9 };
        var fileName = "tampered.jpg";
        var mimeType = "image/jpeg";

        var manifestResult = await _service.CreateAndEmbedManifestAsync(
            fakeJpeg,
            fileName,
            mimeType,
            "user_tamper",
            SourceType.Camera
        );

        var tamperedBytes = (byte[])manifestResult.EmbeddedMediaBytes.Clone();
        // Alter payload byte at the end
        tamperedBytes[^1] = (byte)(tamperedBytes[^1] ^ 0xFF);

        // Act
        var verification = await _service.VerifyMediaAsync(tamperedBytes, fileName, mimeType);

        // Assert
        Assert.Equal(C2paStatus.InvalidCredentials, verification.Status);
        Assert.False(verification.HardBindingValid);
    }

    [Fact]
    public async Task VerifyMedia_WhenImportedSource_ShouldReportVerifiedProvenanceTransformed()
    {
        // Arrange
        var fakePng = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82 };
        var fileName = "imported.png";
        var mimeType = "image/png";

        var manifestResult = await _service.CreateAndEmbedManifestAsync(
            fakePng,
            fileName,
            mimeType,
            "user_import",
            SourceType.Import
        );

        // Act
        var verification = await _service.VerifyMediaAsync(manifestResult.EmbeddedMediaBytes, fileName, mimeType);

        // Assert
        Assert.True(verification.HasManifest);
        Assert.True(verification.SignatureValid);
        Assert.True(verification.HardBindingValid);
        Assert.Equal(C2paStatus.VerifiedProvenanceTransformed, verification.Status);
    }
}

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using UIBackend.Infrastructure.Storage;
using Xunit;

namespace UIBackend.UnitTests;

public class FileSystemMediaStorageServiceTests
{
    [Fact]
    public async Task StoreAndRetrieve_ShouldStoreAndComputeSha256Correctly()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "vx_test_storage_" + Guid.NewGuid().ToString("N"));
        var inMemorySettings = new Dictionary<string, string?>
        {
            {"Storage:BasePath", tempDir}
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(inMemorySettings).Build();
        var storage = new FileSystemMediaStorageService(configuration, NullLogger<FileSystemMediaStorageService>.Instance);

        var data = "VisionX Authenticity Test Payload"u8.ToArray();
        var mediaId = Guid.NewGuid();

        try
        {
            using var ms = new MemoryStream(data);
            var expectedHash = storage.ComputeSha256(ms);

            var path = await storage.StoreMediaAsync("user1", mediaId, "test.txt", new MemoryStream(data));
            Assert.True(File.Exists(path));

            using var retrieved = await storage.GetMediaStreamAsync(path);
            Assert.NotNull(retrieved);

            var retrievedHash = storage.ComputeSha256(retrieved!);
            Assert.Equal(expectedHash, retrievedHash);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }
}

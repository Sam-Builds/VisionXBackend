using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using UIBackend.Application.Interfaces;

namespace UIBackend.Infrastructure.Storage;

public class FileSystemMediaStorageService : IMediaStorageService
{
    private readonly string _storageBasePath;
    private readonly ILogger<FileSystemMediaStorageService> _logger;

    public FileSystemMediaStorageService(IConfiguration configuration, ILogger<FileSystemMediaStorageService> logger)
    {
        _logger = logger;
        var envPath = Environment.GetEnvironmentVariable("MEDIA_STORAGE_PATH");
        var configPath = configuration["Storage:BasePath"];
        
        _storageBasePath = !string.IsNullOrWhiteSpace(envPath) 
            ? envPath 
            : !string.IsNullOrWhiteSpace(configPath) 
                ? configPath 
                : Path.Combine(Directory.GetCurrentDirectory(), "media_storage");

        try
        {
            if (!Directory.Exists(_storageBasePath))
            {
                Directory.CreateDirectory(_storageBasePath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not create configured base storage path {Path}. Falling back to local temp folder.", _storageBasePath);
            _storageBasePath = Path.Combine(Path.GetTempPath(), "visionx_media_storage");
            Directory.CreateDirectory(_storageBasePath);
        }

        _logger.LogInformation("Media Storage initialized at: {StorageBasePath}", _storageBasePath);
    }

    public async Task<string> StoreMediaAsync(string userId, Guid mediaItemId, string fileName, Stream contentStream, CancellationToken cancellationToken = default)
    {
        var sanitizedUserId = SanitizePath(userId);
        var subDir = Path.Combine(_storageBasePath, sanitizedUserId, DateTime.UtcNow.ToString("yyyy-MM"), mediaItemId.ToString());
        Directory.CreateDirectory(subDir);

        var safeFileName = Path.GetFileName(fileName);
        var targetFilePath = Path.Combine(subDir, safeFileName);
        var tempFilePath = Path.Combine(subDir, $"{safeFileName}.{Guid.NewGuid():N}.tmp");

        try
        {
            if (contentStream.CanSeek)
            {
                contentStream.Position = 0;
            }

            await using (var fileStream = new FileStream(tempFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await contentStream.CopyToAsync(fileStream, cancellationToken);
            }

            // Atomic move/replace
            if (File.Exists(targetFilePath))
            {
                File.Delete(targetFilePath);
            }
            File.Move(tempFilePath, targetFilePath);

            _logger.LogInformation("Stored media file {TargetFilePath} successfully.", targetFilePath);
            return targetFilePath;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to store media file {TargetFilePath}", targetFilePath);
            if (File.Exists(tempFilePath))
            {
                try { File.Delete(tempFilePath); } catch { /* ignore */ }
            }
            throw;
        }
    }

    public Task<Stream?> GetMediaStreamAsync(string storagePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(storagePath))
        {
            return Task.FromResult<Stream?>(null);
        }

        var stream = new FileStream(storagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        return Task.FromResult<Stream?>(stream);
    }

    public async Task<(Stream Stream, long TotalLength, long Start, long End)?> GetMediaRangeStreamAsync(string storagePath, string? rangeHeader, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(storagePath))
        {
            return null;
        }

        var fileInfo = new FileInfo(storagePath);
        var totalLength = fileInfo.Length;

        if (string.IsNullOrWhiteSpace(rangeHeader) || !rangeHeader.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            var fullStream = new FileStream(storagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            return (fullStream, totalLength, 0, Math.Max(0, totalLength - 1));
        }

        // Parse bytes=start-end
        var rangeSpec = rangeHeader["bytes=".Length..].Trim();
        var parts = rangeSpec.Split('-', StringSplitOptions.RemoveEmptyEntries);

        long start = 0;
        long end = totalLength - 1;

        if (parts.Length == 1)
        {
            if (rangeHeader.EndsWith("-"))
            {
                // bytes=start-
                if (long.TryParse(parts[0], out var s))
                    start = Math.Clamp(s, 0, totalLength - 1);
            }
            else
            {
                // bytes=-suffix
                if (long.TryParse(parts[0], out var suffix))
                {
                    start = Math.Max(0, totalLength - suffix);
                }
            }
        }
        else if (parts.Length >= 2)
        {
            if (long.TryParse(parts[0], out var s))
                start = Math.Clamp(s, 0, totalLength - 1);
            if (long.TryParse(parts[1], out var e))
                end = Math.Clamp(e, start, totalLength - 1);
        }

        var fs = new FileStream(storagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        fs.Seek(start, SeekOrigin.Begin);

        return (fs, totalLength, start, end);
    }

    public Task DeleteMediaAsync(string storagePath, CancellationToken cancellationToken = default)
    {
        try
        {
            if (File.Exists(storagePath))
            {
                File.Delete(storagePath);
                var dir = Path.GetDirectoryName(storagePath);
                if (dir != null && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                {
                    Directory.Delete(dir, recursive: false);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete file {StoragePath}", storagePath);
        }

        return Task.CompletedTask;
    }

    public bool Exists(string storagePath)
    {
        return File.Exists(storagePath);
    }

    public string ComputeSha256(Stream stream)
    {
        if (stream.CanSeek)
        {
            stream.Position = 0;
        }

        using var sha256 = SHA256.Create();
        var hashBytes = sha256.ComputeHash(stream);

        if (stream.CanSeek)
        {
            stream.Position = 0;
        }

        return Convert.ToHexStringLower(hashBytes);
    }

    public async Task<string> ComputeSha256Async(Stream stream, CancellationToken cancellationToken = default)
    {
        if (stream.CanSeek)
        {
            stream.Position = 0;
        }

        using var sha256 = SHA256.Create();
        var hashBytes = await sha256.ComputeHashAsync(stream, cancellationToken);

        if (stream.CanSeek)
        {
            stream.Position = 0;
        }

        return Convert.ToHexStringLower(hashBytes);
    }

    private static string SanitizePath(string input)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(Path.GetInvalidPathChars()).ToHashSet();
        return new string(input.Where(c => !invalid.Contains(c) && c != '/' && c != '\\').ToArray());
    }
}

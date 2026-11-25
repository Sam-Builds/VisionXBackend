namespace UIBackend.Application.Interfaces;

public interface IMediaStorageService
{
    Task<string> StoreMediaAsync(string userId, Guid mediaItemId, string fileName, Stream contentStream, CancellationToken cancellationToken = default);
    Task<Stream?> GetMediaStreamAsync(string storagePath, CancellationToken cancellationToken = default);
    Task<(Stream Stream, long TotalLength, long Start, long End)?> GetMediaRangeStreamAsync(string storagePath, string? rangeHeader, CancellationToken cancellationToken = default);
    Task DeleteMediaAsync(string storagePath, CancellationToken cancellationToken = default);
    bool Exists(string storagePath);
    string ComputeSha256(Stream stream);
    Task<string> ComputeSha256Async(Stream stream, CancellationToken cancellationToken = default);
}

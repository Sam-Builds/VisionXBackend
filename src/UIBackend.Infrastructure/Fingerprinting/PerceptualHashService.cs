using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using UIBackend.Application.Interfaces;

namespace UIBackend.Infrastructure.Fingerprinting;

public class PerceptualHashService : IMediaFingerprintProvider
{
    private readonly ILogger<PerceptualHashService> _logger;

    public PerceptualHashService(ILogger<PerceptualHashService> logger)
    {
        _logger = logger;
    }

    public string? ComputePerceptualDHash(byte[] mediaBytes, string mimeType)
    {
        if (mediaBytes.Length == 0 || mimeType.StartsWith("video", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            using var image = Image.Load<L8>(mediaBytes);
            image.Mutate(x => x.Resize(9, 8));

            ulong hash = 0;
            int bit = 0;
            for (int y = 0; y < 8; y++)
            {
                for (int x = 0; x < 8; x++)
                {
                    byte left = image[x, y].PackedValue;
                    byte right = image[x + 1, y].PackedValue;
                    if (left > right)
                    {
                        hash |= (1UL << bit);
                    }
                    bit++;
                }
            }

            return hash.ToString("x16");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not compute perceptual dHash for media ({MimeType}).", mimeType);
            return null;
        }
    }

    public double ComputeSimilarity(string hashA, string hashB)
    {
        if (string.IsNullOrWhiteSpace(hashA) || string.IsNullOrWhiteSpace(hashB) ||
            hashA.Length != 16 || hashB.Length != 16)
        {
            return 0.0;
        }

        try
        {
            ulong a = Convert.ToUInt64(hashA, 16);
            ulong b = Convert.ToUInt64(hashB, 16);
            ulong diff = a ^ b;
            int hammingDistance = System.Numerics.BitOperations.PopCount(diff);
            return 1.0 - (hammingDistance / 64.0);
        }
        catch
        {
            return 0.0;
        }
    }
}

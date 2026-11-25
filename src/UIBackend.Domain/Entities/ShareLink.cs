namespace UIBackend.Domain.Entities;

public class ShareLink
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid MediaItemId { get; set; }
    public string CreatedByUserId { get; set; } = string.Empty;
    public string Token { get; set; } = Guid.NewGuid().ToString("N");
    public bool IsPublic { get; set; } = true;
    public bool AllowDownload { get; set; } = true;
    public DateTime? ExpiresAtUtc { get; set; }
    public int AccessCount { get; set; } = 0;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public bool IsRevoked { get; set; } = false;

    // Navigation property
    public MediaItem MediaItem { get; set; } = null!;
}

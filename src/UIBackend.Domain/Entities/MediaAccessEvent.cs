using UIBackend.Domain.Enums;

namespace UIBackend.Domain.Entities;

public class MediaAccessEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid MediaItemId { get; set; }
    public Guid? ShareLinkId { get; set; }
    public string? AccessingUserId { get; set; }
    public AccessEventType EventType { get; set; } = AccessEventType.View;
    public string IpAddress { get; set; } = string.Empty;
    public string UserAgent { get; set; } = string.Empty;
    public string? Referer { get; set; }
    public bool IsCrawlerOrBot { get; set; } = false;
    public bool OwnerNotified { get; set; } = false;
    public DateTime AccessedAtUtc { get; set; } = DateTime.UtcNow;

    // Navigation property
    public MediaItem MediaItem { get; set; } = null!;
    public ShareLink? ShareLink { get; set; }
}

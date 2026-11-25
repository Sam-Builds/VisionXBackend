using UIBackend.Domain.Enums;

namespace UIBackend.Application.DTOs;

public record AccessHistoryItemResponse(
    Guid Id,
    Guid MediaItemId,
    AccessEventType EventType,
    string IpAddress,
    string UserAgent,
    string? Referer,
    bool IsCrawlerOrBot,
    string? AccessingUserId,
    DateTime AccessedAtUtc
);

public record MediaAccessSummaryResponse(
    Guid MediaItemId,
    int TotalViews,
    int TotalPlays,
    int TotalDownloads,
    int TotalVerifications,
    IReadOnlyList<AccessHistoryItemResponse> RecentEvents
);

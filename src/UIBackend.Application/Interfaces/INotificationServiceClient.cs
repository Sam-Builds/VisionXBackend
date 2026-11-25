using UIBackend.Domain.Enums;

namespace UIBackend.Application.Interfaces;

public interface INotificationServiceClient
{
    Task NotifyOwnerMediaAccessedAsync(
        string ownerUserId,
        Guid mediaItemId,
        string mediaName,
        AccessEventType eventType,
        string accessingIp,
        string? accessingUserId = null,
        CancellationToken cancellationToken = default
    );
}

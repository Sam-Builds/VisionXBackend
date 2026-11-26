using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using UIBackend.Application.Interfaces;
using UIBackend.Domain.Enums;
using UIBackend.Infrastructure.Monitoring;

namespace UIBackend.Infrastructure.Notifications;

public class VisionXNotificationClient : INotificationServiceClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<VisionXNotificationClient> _logger;

    public VisionXNotificationClient(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<VisionXNotificationClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task NotifyOwnerMediaAccessedAsync(
        string ownerUserId,
        Guid mediaItemId,
        string mediaName,
        AccessEventType eventType,
        string accessingIp,
        string? accessingUserId = null,
        CancellationToken cancellationToken = default)
    {
        var notifBaseUrl = (_configuration["Notifications:BaseUrl"]
            ?? Environment.GetEnvironmentVariable("NOTIFICATIONS_BASE_URL")
            ?? "https://visionx.samstack.site/notifs").TrimEnd('/');
        var webhookSecret = _configuration["Notifications:WebhookSecret"]
            ?? Environment.GetEnvironmentVariable("SESSION_WEBHOOK_SECRET")
            ?? "VisionX_SessionEvent_SharedSecret_2026";

        var eventVerb = eventType switch
        {
            AccessEventType.View => "viewed",
            AccessEventType.Play => "played",
            AccessEventType.Download => "downloaded",
            AccessEventType.Verify => "verified",
            _ => "accessed"
        };

        var title = $"Media {eventType}: {mediaName}";
        var body = string.IsNullOrWhiteSpace(accessingUserId)
            ? $"Your registered media '{mediaName}' was {eventVerb} from IP {accessingIp}."
            : $"User '{accessingUserId}' {eventVerb} your registered media '{mediaName}' (IP: {accessingIp}).";

        var eventId = $"media-{mediaItemId:N}-{Guid.NewGuid():N}";
        var payload = new
        {
            eventId,
            eventType = "media.accessed",
            source = "VisionXBackend",
            title,
            body,
            categoryCode = "alerts",
            routeKey = "alerts.inbox",
            routeParameters = new Dictionary<string, string>
            {
                { "mediaItemId", mediaItemId.ToString() },
                { "eventType", eventType.ToString() }
            },
            directRecipientUserIds = new[] { ownerUserId },
            occurredAt = DateTime.UtcNow
        };

        var rawBody = JsonSerializer.Serialize(payload);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var signature = NotificationOutboxService.ComputeHmacSha256(timestamp, rawBody, webhookSecret);

        try
        {
            var client = _httpClientFactory.CreateClient("NotificationsClient");
            var url = $"{notifBaseUrl}/api/internal/events";

            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Add("X-Timestamp", timestamp);
            request.Headers.Add("X-Signature", signature);
            request.Content = new StringContent(rawBody, Encoding.UTF8, "application/json");

            var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Notification service returned status {StatusCode} when notifying owner {OwnerId}", response.StatusCode, ownerUserId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not send media access notification to notifications service.");
        }
    }
}

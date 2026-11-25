using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using UIBackend.Application.Interfaces;
using UIBackend.Domain.Enums;

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
        var notifBaseUrl = _configuration["Notifications:BaseUrl"] ?? "http://visionx-notifications-api:8080";
        var webhookSecret = _configuration["Notifications:WebhookSecret"] ?? "VisionX_SessionEvent_SharedSecret_2026";

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

        var payload = new
        {
            userId = ownerUserId,
            title = title,
            body = body,
            categoryId = "cat_alerts",
            channel = "InAppAndPush",
            metadata = new Dictionary<string, string>
            {
                { "type", "media_access_event" },
                { "mediaItemId", mediaItemId.ToString() },
                { "eventType", eventType.ToString() },
                { "accessingIp", accessingIp }
            }
        };

        try
        {
            var client = _httpClientFactory.CreateClient("NotificationsClient");
            var url = $"{notifBaseUrl.TrimEnd('/')}/api/v1/notifications/internal/dispatch";

            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Add("X-Webhook-Secret", webhookSecret);
            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            var response = await client.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Successfully dispatched access notification for owner {OwnerId} of media {MediaId}", ownerUserId, mediaItemId);
            }
            else
            {
                _logger.LogWarning("Notification service returned status {StatusCode} when notifying owner {OwnerId}", response.StatusCode, ownerUserId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not send media access notification to notifications service. Falling back gracefully.");
        }
    }
}

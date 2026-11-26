using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using UIBackend.Application.Interfaces;
using UIBackend.Domain.Entities;
using UIBackend.Domain.Enums;

namespace UIBackend.Infrastructure.Monitoring;

public class NotificationOutboxService : INotificationOutboxService
{
    private readonly IUIBackendDbContext _dbContext;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<NotificationOutboxService> _logger;

    private static readonly int[] RetryBackoffSeconds = { 15, 30, 60, 120, 300 };

    public NotificationOutboxService(
        IUIBackendDbContext dbContext,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<NotificationOutboxService> logger)
    {
        _dbContext = dbContext;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<NotificationOutboxItem?> DispatchItemAsync(
        Guid outboxId,
        CancellationToken cancellationToken = default)
    {
        var item = await _dbContext.NotificationOutboxItems
            .Include(o => o.Alert)
            .FirstOrDefaultAsync(o => o.Id == outboxId, cancellationToken);

        if (item == null || item.Status == NotificationDispatchStatus.Sent)
        {
            return item;
        }

        item.AttemptCount++;

        // Support controlled dev retry simulation
        if (item.SimulateTransientFailureOnce && item.AttemptCount == 1)
        {
            item.SimulateTransientFailureOnce = false;
            item.Status = NotificationDispatchStatus.Retrying;
            item.LastResponseCode = 503;
            item.LastError = "Simulated transient notification service unavailability (503). Scheduled for automatic retry.";
            item.NextRetryAtUtc = DateTime.UtcNow.AddSeconds(5);
            item.Alert.NotificationStatus = NotificationDispatchStatus.Retrying;
            await _dbContext.SaveChangesAsync(cancellationToken);
            return item;
        }

        var notifBaseUrl = (_configuration["Notifications:BaseUrl"]
            ?? Environment.GetEnvironmentVariable("NOTIFICATIONS_BASE_URL")
            ?? "https://visionx.samstack.site/notifs").TrimEnd('/');

        var webhookSecret = _configuration["Notifications:WebhookSecret"]
            ?? Environment.GetEnvironmentVariable("SESSION_WEBHOOK_SECRET")
            ?? "VisionX_SessionEvent_SharedSecret_2026";

        var url = $"{notifBaseUrl}/api/internal/events";
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var signature = ComputeHmacSha256(timestamp, item.PayloadJson, webhookSecret);

        try
        {
            var client = _httpClientFactory.CreateClient("NotificationsClient");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Add("X-Timestamp", timestamp);
            request.Headers.Add("X-Signature", signature);
            request.Content = new StringContent(item.PayloadJson, Encoding.UTF8, "application/json");

            var response = await client.SendAsync(request, cts.Token);
            var responseBody = await response.Content.ReadAsStringAsync(cts.Token);

            item.LastResponseCode = (int)response.StatusCode;
            item.LastResponseBody = responseBody.Length > 1000 ? responseBody[..1000] : responseBody;

            if (response.IsSuccessStatusCode)
            {
                item.Status = NotificationDispatchStatus.Sent;
                item.SentAtUtc = DateTime.UtcNow;
                item.LastError = null;
                item.NextRetryAtUtc = null;
                item.Alert.NotificationStatus = NotificationDispatchStatus.Sent;
                item.Alert.NotificationId = item.EventId;
                _logger.LogInformation("Dispatched alert {AlertId} to Notifications service ({EventId}).", item.AlertId, item.EventId);
            }
            else
            {
                ScheduleRetryOrFail(item, $"Notifications service returned HTTP {(int)response.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed dispatching outbox item {OutboxId} for alert {AlertId}", item.Id, item.AlertId);
            ScheduleRetryOrFail(item, ex.Message);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return item;
    }

    public async Task<int> ProcessPendingBatchAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var dueIds = await _dbContext.NotificationOutboxItems
            .Where(o => (o.Status == NotificationDispatchStatus.Pending || o.Status == NotificationDispatchStatus.Retrying) &&
                        (o.NextRetryAtUtc == null || o.NextRetryAtUtc <= now))
            .OrderBy(o => o.CreatedAtUtc)
            .Take(20)
            .Select(o => o.Id)
            .ToListAsync(cancellationToken);

        int processed = 0;
        foreach (var id in dueIds)
        {
            await DispatchItemAsync(id, cancellationToken);
            processed++;
        }
        return processed;
    }

    private static void ScheduleRetryOrFail(NotificationOutboxItem item, string error)
    {
        item.LastError = error;
        if (item.AttemptCount >= item.MaxAttempts)
        {
            item.Status = NotificationDispatchStatus.Failed;
            item.NextRetryAtUtc = null;
            item.Alert.NotificationStatus = NotificationDispatchStatus.Failed;
        }
        else
        {
            var backoffIdx = Math.Clamp(item.AttemptCount - 1, 0, RetryBackoffSeconds.Length - 1);
            item.Status = NotificationDispatchStatus.Retrying;
            item.NextRetryAtUtc = DateTime.UtcNow.AddSeconds(RetryBackoffSeconds[backoffIdx]);
            item.Alert.NotificationStatus = NotificationDispatchStatus.Retrying;
        }
    }

    public static string ComputeHmacSha256(string timestamp, string rawBody, string secret)
    {
        var signedData = $"{timestamp}.{rawBody}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(signedData));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

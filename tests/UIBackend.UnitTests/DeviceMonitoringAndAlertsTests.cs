using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using UIBackend.Application.DTOs;
using UIBackend.Domain.Entities;
using UIBackend.Domain.Enums;
using UIBackend.Infrastructure.Monitoring;
using UIBackend.Infrastructure.Persistence;
using Xunit;

namespace UIBackend.UnitTests;

public class DeviceMonitoringAndAlertsTests
{
    private static UIBackendDbContext CreateDb()
    {
        var opts = new DbContextOptionsBuilder<UIBackendDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new UIBackendDbContext(opts);
    }

    private static IConfiguration CreateConfig(Dictionary<string, string?>? extra = null)
    {
        var dict = new Dictionary<string, string?>
        {
            ["AlertPolicy:InfoMaxAnomalyScore"] = "0.40",
            ["AlertPolicy:WarningMaxAnomalyScore"] = "0.75",
            ["AlertPolicy:NotificationCooldownSeconds"] = "120",
            ["AlertPolicy:NotifyOnInfo"] = "false",
            ["AlertPolicy:NotifyOnWarning"] = "true",
            ["AlertPolicy:NotifyOnCritical"] = "true",
            ["Notifications:BaseUrl"] = "https://visionx.samstack.site/notifs",
            ["Notifications:WebhookSecret"] = "VisionX_SessionEvent_SharedSecret_2026"
        };
        if (extra != null)
        {
            foreach (var kv in extra) dict[kv.Key] = kv.Value;
        }
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    [Theory]
    [InlineData("https://visionx.samstack.site/webinput/stream/cam1", true)]
    [InlineData("rtsp://cam.example.com:554/stream1", true)]
    [InlineData("http://169.254.169.254/latest/meta-data/", false)]
    [InlineData("http://127.0.0.1:8080/internal", false)]
    [InlineData("http://192.168.1.50/stream", false)]
    [InlineData("file:///etc/passwd", false)]
    public async Task FeedUrlValidator_ShouldEnforceSsrfAndSchemePolicy(string url, bool expectedValid)
    {
        var validator = new FeedUrlValidator(CreateConfig());
        var (isValid, _) = await validator.ValidateAsync(url);
        Assert.Equal(expectedValid, isValid);
    }

    [Fact]
    public void StreamCredentialProtector_ShouldEncryptAndDecryptSafely()
    {
        var protector = new StreamCredentialProtector(CreateConfig());
        var encrypted = protector.EncryptCredentials("cam_admin", "SuperSecret123!");
        Assert.NotNull(encrypted);
        Assert.DoesNotContain("SuperSecret123!", encrypted);

        var (u, p) = protector.DecryptCredentials(encrypted);
        Assert.Equal("cam_admin", u);
        Assert.Equal("SuperSecret123!", p);
    }

    [Fact]
    public async Task AlertClassification_InfoWarningCriticalAndDeduplication_ShouldWorkEndToEnd()
    {
        using var db = CreateDb();
        var config = CreateConfig();
        var httpFactory = new MockHttpClientFactory(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"success\":true}")
        });
        var outbox = new NotificationOutboxService(db, httpFactory, config, NullLogger<NotificationOutboxService>.Instance);
        var classifier = new AlertClassificationService(db, outbox, config, NullLogger<AlertClassificationService>.Instance);

        var device = new MonitoredDevice
        {
            Id = Guid.NewGuid(),
            OwnerUserId = "user_a",
            Name = "Main Entrance",
            FeedUrl = "https://visionx.samstack.site/webinput/stream/main",
            Status = DeviceStatus.Starting,
            MonitoringEnabled = true,
            ActiveJobId = "job-1"
        };
        db.MonitoredDevices.Add(device);
        await db.SaveChangesAsync();

        // 1. INFO event -> no push notification
        var infoRes = await classifier.ProcessDetectionEventAsync(new MlDetectionEventRequest(
            "evt-info-1", device.Id, "job-1", DateTime.UtcNow, "motion_low", 0.6, 0.20, "YOLOv8", "1.0", "Low motion", null, null));
        Assert.True(infoRes.Accepted);
        Assert.Equal(AlertSeverity.Info, infoRes.AssignedSeverity);
        Assert.Equal(NotificationDispatchStatus.NotRequired, infoRes.NotificationStatus);

        // Device becomes Online after ML inference event arrives
        var reloadedDevice = await db.MonitoredDevices.FindAsync(device.Id);
        Assert.Equal(DeviceStatus.Online, reloadedDevice!.Status);

        // 2. Duplicate EventId -> idempotent, no duplicate alert
        var dupRes = await classifier.ProcessDetectionEventAsync(new MlDetectionEventRequest(
            "evt-info-1", device.Id, "job-1", DateTime.UtcNow, "motion_low", 0.6, 0.20, "YOLOv8", "1.0", "Low motion", null, null));
        Assert.True(dupRes.Duplicate);
        Assert.Equal(1, await db.DetectionRecords.CountAsync());

        // 3. WARNING event -> triggers push notification
        var warnRes = await classifier.ProcessDetectionEventAsync(new MlDetectionEventRequest(
            "evt-warn-1", device.Id, "job-1", DateTime.UtcNow, "loitering", 0.85, 0.55, "YOLOv8", "1.0", "Person loitering", null, null));
        Assert.Equal(AlertSeverity.Warning, warnRes.AssignedSeverity);
        Assert.Equal(NotificationDispatchStatus.Sent, warnRes.NotificationStatus);

        // 4. Repeated WARNING within cooldown -> aggregates count, no duplicate notification
        var repeatRes = await classifier.ProcessDetectionEventAsync(new MlDetectionEventRequest(
            "evt-warn-2", device.Id, "job-1", DateTime.UtcNow, "loitering", 0.86, 0.58, "YOLOv8", "1.0", "Still loitering", null, null));
        Assert.Equal(warnRes.AlertId, repeatRes.AlertId);
        var warnAlert = await db.AlertRecords.FindAsync(warnRes.AlertId);
        Assert.Equal(2, warnAlert!.OccurrenceCount);
        Assert.Equal(1, await db.NotificationOutboxItems.CountAsync(o => o.AlertId == warnAlert.Id));

        // 5. Escalation WARNING -> CRITICAL -> upgrades severity and sends critical notification
        var escRes = await classifier.ProcessDetectionEventAsync(new MlDetectionEventRequest(
            "evt-crit-1", device.Id, "job-1", DateTime.UtcNow, "loitering", 0.95, 0.90, "YOLOv8", "1.0", "Escalated threat", null, null));
        Assert.Equal(AlertSeverity.Critical, escRes.AssignedSeverity);
        Assert.Equal(2, await db.NotificationOutboxItems.CountAsync(o => o.AlertId == warnAlert.Id));
    }

    [Fact]
    public async Task NotificationOutbox_WhenServiceUnavailable_ShouldPreserveAlertAndRetrySafely()
    {
        using var db = CreateDb();
        var config = CreateConfig();
        bool serviceUp = false;
        var httpFactory = new MockHttpClientFactory(_ =>
        {
            if (!serviceUp)
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("Unavailable") };
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"success\":true}") };
        });

        var outbox = new NotificationOutboxService(db, httpFactory, config, NullLogger<NotificationOutboxService>.Instance);
        var classifier = new AlertClassificationService(db, outbox, config, NullLogger<AlertClassificationService>.Instance);

        var device = new MonitoredDevice
        {
            Id = Guid.NewGuid(),
            OwnerUserId = "user_b",
            Name = "Back Gate",
            FeedUrl = "https://visionx.samstack.site/webinput/stream/back",
            ActiveJobId = "job-back"
        };
        db.MonitoredDevices.Add(device);
        await db.SaveChangesAsync();

        // Notifications service is down -> alert MUST remain saved, outbox marked Retrying
        var res = await classifier.ProcessDetectionEventAsync(new MlDetectionEventRequest(
            "evt-fail-1", device.Id, "job-back", DateTime.UtcNow, "intrusion", 0.95, 0.92, "YOLOv8", "1.0", "Intrusion", null, null));

        Assert.NotNull(res.AlertId);
        Assert.Equal(NotificationDispatchStatus.Retrying, res.NotificationStatus);
        Assert.Equal(1, await db.AlertRecords.CountAsync());

        // Bring service back online and force retry window due
        serviceUp = true;
        var outboxRow = await db.NotificationOutboxItems.FirstAsync();
        outboxRow.NextRetryAtUtc = DateTime.UtcNow.AddSeconds(-1);
        await db.SaveChangesAsync();

        var processed = await outbox.ProcessPendingBatchAsync();
        Assert.Equal(1, processed);

        var updatedAlert = await db.AlertRecords.FindAsync(res.AlertId);
        Assert.Equal(NotificationDispatchStatus.Sent, updatedAlert!.NotificationStatus);
        Assert.Equal(1, await db.AlertRecords.CountAsync());
    }

    private sealed class MockHttpClientFactory : IHttpClientFactory
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;
        public MockHttpClientFactory(Func<HttpRequestMessage, HttpResponseMessage> handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(new DelegateHandler(_handler));
    }

    private sealed class DelegateHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;
        public DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) => _handler = handler;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_handler(request));
    }
}

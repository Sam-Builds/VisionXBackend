using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using UIBackend.Application.Interfaces;
using UIBackend.Domain.Entities;
using UIBackend.Domain.Enums;

namespace UIBackend.Infrastructure.Monitoring;

public class MlServiceClient : IMlServiceClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<MlServiceClient> _logger;

    public MlServiceClient(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<MlServiceClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    private string BaseUrl => (_configuration["MlService:BaseUrl"]
        ?? Environment.GetEnvironmentVariable("ML_SERVICE_BASE_URL")
        ?? "https://visionx.samstack.site/ML").TrimEnd('/');

    private string ServiceApiKey => _configuration["MlService:ApiKey"]
        ?? Environment.GetEnvironmentVariable("ML_SERVICE_API_KEY")
        ?? "VisionX_ML_Service_Key_2026";

    public async Task<MlStartResult> StartMonitoringAsync(
        MonitoredDevice device,
        string? username,
        string? password,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        var payload = new
        {
            deviceId = device.Id,
            jobId = device.ActiveJobId ?? $"job-{device.Id:N}",
            idempotencyKey,
            feedUrl = device.FeedUrl,
            streamUsername = username,
            streamPassword = password,
            configurationVersion = device.ConfigurationVersion,
            callbackUrl = _configuration["MlService:CallbackUrl"] ?? "https://visionx.samstack.site/a/api/ml/events"
        };

        try
        {
            var client = CreateClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            var response = await client.PostAsJsonAsync($"{BaseUrl}/api/jobs/start", payload, cts.Token);
            var body = await response.Content.ReadAsStringAsync(cts.Token);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("ML service returned {Status} on start for device {DeviceId}", response.StatusCode, device.Id);
                return new MlStartResult(
                    Success: false,
                    Reachable: true,
                    JobId: null,
                    WorkerId: null,
                    ResultingStatus: DeviceStatus.Error,
                    ErrorMessage: $"ML service rejected job start (HTTP {(int)response.StatusCode})."
                );
            }

            string? jobId = payload.jobId;
            string? workerId = null;
            DeviceStatus status = DeviceStatus.Starting;

            if (!string.IsNullOrWhiteSpace(body))
            {
                try
                {
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("jobId", out var j) && !string.IsNullOrWhiteSpace(j.GetString()))
                        jobId = j.GetString();
                    if (doc.RootElement.TryGetProperty("workerId", out var w))
                        workerId = w.GetString();
                    if (doc.RootElement.TryGetProperty("status", out var s))
                    {
                        var str = s.GetString()?.ToLowerInvariant();
                        if (str == "online" || str == "active" || str == "running") status = DeviceStatus.Online;
                        else if (str == "starting") status = DeviceStatus.Starting;
                    }
                }
                catch { /* use defaults */ }
            }

            return new MlStartResult(true, true, jobId, workerId, status, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ML service unreachable when starting device {DeviceId}", device.Id);
            return new MlStartResult(
                Success: false,
                Reachable: false,
                JobId: null,
                WorkerId: null,
                ResultingStatus: DeviceStatus.Offline,
                ErrorMessage: "ML service is currently unavailable. Job will remain pending."
            );
        }
    }

    public async Task<MlStopResult> StopMonitoringAsync(
        Guid deviceId,
        string jobId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var client = CreateClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(8));

            var response = await client.PostAsJsonAsync(
                $"{BaseUrl}/api/jobs/stop",
                new { deviceId, jobId },
                cts.Token);

            if (!response.IsSuccessStatusCode)
            {
                return new MlStopResult(false, true, $"ML service returned HTTP {(int)response.StatusCode}.");
            }
            return new MlStopResult(true, true, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ML service unreachable when stopping device {DeviceId}", deviceId);
            return new MlStopResult(false, false, "ML service unreachable while stopping job.");
        }
    }

    public async Task<MlStatusResult> GetJobStatusAsync(
        Guid deviceId,
        string jobId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var client = CreateClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(6));

            var response = await client.GetAsync($"{BaseUrl}/api/jobs/{Uri.EscapeDataString(jobId)}/status", cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                return new MlStatusResult(true, DeviceStatus.Error, null, null, $"ML status returned HTTP {(int)response.StatusCode}.");
            }

            var body = await response.Content.ReadAsStringAsync(cts.Token);
            using var doc = JsonDocument.Parse(body);
            var rawStatus = doc.RootElement.TryGetProperty("status", out var s) ? s.GetString()?.ToLowerInvariant() : null;
            var workerId = doc.RootElement.TryGetProperty("workerId", out var w) ? w.GetString() : null;
            DateTime? lastInf = null;
            if (doc.RootElement.TryGetProperty("lastSuccessfulInferenceAtUtc", out var inf) && inf.ValueKind == JsonValueKind.String)
            {
                if (DateTime.TryParse(inf.GetString(), out var parsed)) lastInf = parsed.ToUniversalTime();
            }

            var mapped = rawStatus switch
            {
                "online" or "active" or "running" => DeviceStatus.Online,
                "starting" => DeviceStatus.Starting,
                "stopped" => DeviceStatus.Stopped,
                "error" => DeviceStatus.Error,
                _ => DeviceStatus.Offline
            };

            return new MlStatusResult(true, mapped, workerId, lastInf, null);
        }
        catch
        {
            return new MlStatusResult(false, DeviceStatus.Offline, null, null, "ML service unreachable.");
        }
    }

    private HttpClient CreateClient()
    {
        var client = _httpClientFactory.CreateClient("MlServiceClient");
        if (!string.IsNullOrWhiteSpace(ServiceApiKey))
        {
            client.DefaultRequestHeaders.Remove("X-ML-Service-Key");
            client.DefaultRequestHeaders.Add("X-ML-Service-Key", ServiceApiKey);
        }
        return client;
    }
}

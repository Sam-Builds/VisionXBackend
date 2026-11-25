using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using UIBackend.Application.Interfaces;
using UIBackend.Domain.Enums;

namespace UIBackend.Infrastructure.AiDetection;

/// <summary>
/// Integrates OpenAI's official Content Provenance API (POST /v1/content_provenance_checks)
/// to inspect media for supported OpenAI C2PA and SynthID signals.
/// </summary>
public class OpenAiContentProvenanceProvider : IAiDetectionProvider
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<OpenAiContentProvenanceProvider> _logger;

    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".webp", ".gif", ".pdf",
        ".mp3", ".wav", ".aac", ".flac", ".ogg", ".opus", ".pcm"
    };

    public string ProviderName => "OpenAI Content Provenance API";

    public OpenAiContentProvenanceProvider(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<OpenAiContentProvenanceProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<AiProviderResult> AnalyzeAsync(
        byte[] mediaBytes,
        string fileName,
        string mimeType,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var ext = Path.GetExtension(fileName);
        const string limitations = "Checks only supported OpenAI provenance signals (C2PA & SynthID). A negative result does not prove human origin or rule out AI models from other providers or stripped/degraded watermarks.";

        if (!SupportedExtensions.Contains(ext) &&
            !mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            return new AiProviderResult(
                ProviderName: ProviderName,
                ProviderVersion: "v1/content_provenance_checks",
                Status: EvidenceStatus.Unsupported,
                ResultLabel: "Unsupported Format",
                Confidence: null,
                ModelDetected: null,
                EvidenceJson: null,
                ErrorMessage: null,
                Limitations: limitations,
                CheckedAtUtc: now
            );
        }

        var apiKey = _configuration["OpenAI:ApiKey"] ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return new AiProviderResult(
                ProviderName: ProviderName,
                ProviderVersion: "v1/content_provenance_checks",
                Status: EvidenceStatus.Unsupported,
                ResultLabel: "Provider Not Configured",
                Confidence: null,
                ModelDetected: null,
                EvidenceJson: null,
                ErrorMessage: "OPENAI_API_KEY is not configured on the server.",
                Limitations: limitations,
                CheckedAtUtc: now
            );
        }

        try
        {
            var endpoint = _configuration["OpenAI:ContentProvenanceEndpoint"]
                ?? "https://api.openai.com/v1/content_provenance_checks";

            var client = _httpClientFactory.CreateClient("OpenAiClient");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(15));

            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            using var content = new MultipartFormDataContent();
            var fileContent = new ByteArrayContent(mediaBytes);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue(string.IsNullOrWhiteSpace(mimeType) ? "application/octet-stream" : mimeType);
            content.Add(fileContent, "file", Path.GetFileName(fileName));
            request.Content = content;

            var response = await client.SendAsync(request, cts.Token);
            var body = await response.Content.ReadAsStringAsync(cts.Token);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("OpenAI Content Provenance API returned {Status}: {Body}", response.StatusCode, body);
                return new AiProviderResult(
                    ProviderName: ProviderName,
                    ProviderVersion: "v1/content_provenance_checks",
                    Status: EvidenceStatus.Error,
                    ResultLabel: $"API Error ({(int)response.StatusCode})",
                    Confidence: null,
                    ModelDetected: null,
                    EvidenceJson: null,
                    ErrorMessage: $"HTTP {(int)response.StatusCode}",
                    Limitations: limitations,
                    CheckedAtUtc: now
                );
            }

            using var doc = JsonDocument.Parse(body);
            bool anyDetected = false;
            string? detectedModel = null;
            var detectedTypes = new List<string>();

            if (doc.RootElement.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in results.EnumerateArray())
                {
                    var outcome = item.TryGetProperty("outcome", out var o) ? o.GetString() : null;
                    var type = item.TryGetProperty("type", out var t) ? t.GetString() : null;
                    if (string.Equals(outcome, "detected", StringComparison.OrdinalIgnoreCase))
                    {
                        anyDetected = true;
                        if (!string.IsNullOrWhiteSpace(type)) detectedTypes.Add(type);
                        if (detectedModel == null && item.TryGetProperty("model", out var m))
                        {
                            detectedModel = m.GetString();
                        }
                    }
                }
            }

            return new AiProviderResult(
                ProviderName: ProviderName,
                ProviderVersion: "v1/content_provenance_checks",
                Status: anyDetected ? EvidenceStatus.Positive : EvidenceStatus.Negative,
                ResultLabel: anyDetected
                    ? $"OpenAI AI Signal Detected ({string.Join(", ", detectedTypes)})"
                    : "No OpenAI Provenance Signal Detected",
                Confidence: anyDetected ? 0.99 : null,
                ModelDetected: detectedModel,
                EvidenceJson: body,
                ErrorMessage: null,
                Limitations: limitations,
                CheckedAtUtc: now
            );
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OpenAI Content Provenance check failed.");
            return new AiProviderResult(
                ProviderName: ProviderName,
                ProviderVersion: "v1/content_provenance_checks",
                Status: EvidenceStatus.Error,
                ResultLabel: "Check Failed",
                Confidence: null,
                ModelDetected: null,
                EvidenceJson: null,
                ErrorMessage: ex.Message,
                Limitations: limitations,
                CheckedAtUtc: now
            );
        }
    }
}

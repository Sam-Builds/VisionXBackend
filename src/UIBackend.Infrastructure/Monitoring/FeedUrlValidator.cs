using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;
using UIBackend.Application.Interfaces;

namespace UIBackend.Infrastructure.Monitoring;

public class FeedUrlValidator : IFeedUrlValidator
{
    private readonly IConfiguration _configuration;
    private static readonly HashSet<string> AllowedSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "http", "https", "rtsp", "rtsps"
    };

    public FeedUrlValidator(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public async Task<(bool IsValid, string? ErrorMessage)> ValidateAsync(
        string feedUrl,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(feedUrl))
        {
            return (false, "Feed URL is required.");
        }

        if (!Uri.TryCreate(feedUrl.Trim(), UriKind.Absolute, out var uri))
        {
            return (false, "Feed URL must be a valid absolute URL.");
        }

        if (!AllowedSchemes.Contains(uri.Scheme))
        {
            return (false, $"URL scheme '{uri.Scheme}' is not permitted. Allowed schemes: http, https, rtsp, rtsps.");
        }

        if (string.IsNullOrWhiteSpace(uri.Host))
        {
            return (false, "Feed URL must include a valid host.");
        }

        var host = uri.Host.Trim().ToLowerInvariant();
        var allowPrivateNetworks = _configuration.GetValue<bool>("Monitoring:AllowPrivateNetworks");
        var allowedHostsRaw = _configuration["Monitoring:AllowedPrivateHosts"] ?? string.Empty;
        var allowedHosts = allowedHostsRaw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (allowedHosts.Contains(host))
        {
            return (true, null);
        }

        if (host is "localhost" or "metadata.google.internal" or "169.254.169.254")
        {
            if (!allowPrivateNetworks || host == "169.254.169.254" || host == "metadata.google.internal")
            {
                return (false, $"Host '{uri.Host}' is blocked by SSRF security policy.");
            }
        }

        if (IPAddress.TryParse(host, out var directIp))
        {
            if (IsRestrictedIp(directIp, allowPrivateNetworks))
            {
                return (false, $"IP address '{directIp}' is in a restricted/private network range.");
            }
            return (true, null);
        }

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
            if (addresses.Length > 0 && addresses.All(ip => IsRestrictedIp(ip, allowPrivateNetworks)))
            {
                return (false, $"Host '{uri.Host}' resolves to a restricted/private network address.");
            }
        }
        catch
        {
            // Host may be an external hostname resolved later by ML laptop; allow valid syntax if not blocked
        }

        return (true, null);
    }

    private static bool IsRestrictedIp(IPAddress ip, bool allowPrivateNetworks)
    {
        if (IPAddress.IsLoopback(ip))
        {
            return !allowPrivateNetworks;
        }

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            // 169.254.0.0/16 (Link-local / AWS/GCP/Azure metadata) - ALWAYS blocked
            if (b[0] == 169 && b[1] == 254) return true;
            // 0.0.0.0/8
            if (b[0] == 0) return true;

            if (!allowPrivateNetworks)
            {
                // 10.0.0.0/8
                if (b[0] == 10) return true;
                // 172.16.0.0/12
                if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
                // 192.168.0.0/16
                if (b[0] == 192 && b[1] == 168) return true;
            }
        }
        else if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal) return true;
            if (!allowPrivateNetworks && ip.IsIPv6SiteLocal) return true;
        }

        return false;
    }
}

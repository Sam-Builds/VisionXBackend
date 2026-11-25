namespace UIBackend.Infrastructure.Security;

public static class BotDetector
{
    private static readonly string[] CrawlerSignatures = new[]
    {
        "whatsapp",
        "telegrambot",
        "twitterbot",
        "facebookexternalhit",
        "facebot",
        "googlebot",
        "bingbot",
        "slackbot",
        "linkedinbot",
        "discordbot",
        "applebot",
        "yandexbot",
        "baiduspider",
        "duckduckbot",
        "curl/",
        "wget/",
        "postmanruntime",
        "bytespider",
        "semrushbot",
        "ahrefsbot",
        "mj12bot"
    };

    public static bool IsCrawlerOrBot(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
        {
            return false;
        }

        var lower = userAgent.ToLowerInvariant();
        return CrawlerSignatures.Any(sig => lower.Contains(sig));
    }
}

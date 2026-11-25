using UIBackend.Infrastructure.Security;
using Xunit;

namespace UIBackend.UnitTests;

public class BotDetectorTests
{
    [Theory]
    [InlineData("WhatsApp/2.21.12.21 A", true)]
    [InlineData("TelegramBot (like TwitterBot)", true)]
    [InlineData("facebookexternalhit/1.1 (+http://www.facebook.com/externalhit_uatext.php)", true)]
    [InlineData("Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)", true)]
    [InlineData("Twitterbot/1.0", true)]
    [InlineData("curl/7.81.0", true)]
    [InlineData("Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Mobile Safari/537.36", false)]
    [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) AppleWebKit/605.1.15 Mobile/15E148 Safari/604.1", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void IsCrawlerOrBot_ShouldCorrectlyIdentifyBots(string? userAgent, bool expected)
    {
        var result = BotDetector.IsCrawlerOrBot(userAgent);
        Assert.Equal(expected, result);
    }
}

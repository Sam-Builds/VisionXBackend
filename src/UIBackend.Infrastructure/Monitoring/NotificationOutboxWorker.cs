using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using UIBackend.Application.Interfaces;

namespace UIBackend.Infrastructure.Monitoring;

public class NotificationOutboxWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<NotificationOutboxWorker> _logger;

    public NotificationOutboxWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<NotificationOutboxWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var outboxService = scope.ServiceProvider.GetRequiredService<INotificationOutboxService>();
                await outboxService.ProcessPendingBatchAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Notification outbox background tick failed.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}

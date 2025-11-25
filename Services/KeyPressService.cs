using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

public class KeyPressService : BackgroundService
{
    private readonly ILogger<KeyPressService> _logger;
    private readonly IStatisticsService _stats;

    public KeyPressService(ILogger<KeyPressService> logger, IStatisticsService stats)
    {
        _logger = logger;
        _stats = stats;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("KeyPressService started. Press SPACE to show statistics.");

        while (!stoppingToken.IsCancellationRequested)
        {
            if (Console.KeyAvailable)
            {
                var key = Console.ReadKey(true).Key;
                if (key == ConsoleKey.Spacebar)
                {
                    var count = _stats.GetProcessedCount();
                    //Console.WriteLine($"📊 Total trades processed: {count}");
                    _logger.LogInformation("📊 Total trades processed: {Count}", count);
                }
            }

            await Task.Delay(100, stoppingToken); // Проверяем каждые 100мс
        }
    }
}
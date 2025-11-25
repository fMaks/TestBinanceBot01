// Services/BinanceConnectionManager.cs
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Threading;

public class BinanceConnectionManager : IBinanceConnectionManager, IHostedService
{
    private readonly IOptionsMonitor<AppOptions> _options;
    private readonly ILogger<BinanceConnectionManager> _logger;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    private Task? _monitorTask;
    private CancellationTokenSource? _monitorCts;

    public BinanceConnectionManager(
        IOptionsMonitor<AppOptions> options,
        ILogger<BinanceConnectionManager> logger)
    {
        _options = options;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _monitorCts = new CancellationTokenSource();
        _monitorTask = MonitorChangesAsync(_monitorCts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _monitorCts?.Cancel();
        if (_monitorTask is not null)
        {
            await _monitorTask.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        }
        _semaphore.Dispose();
    }

    private async Task MonitorChangesAsync(CancellationToken ct)
    {
        var currentSymbols = new HashSet<string>(_options.CurrentValue.Symbols, StringComparer.OrdinalIgnoreCase);

        _options.OnChange(options =>
        {
            var newSymbols = new HashSet<string>(options.Symbols, StringComparer.OrdinalIgnoreCase);

            if (!currentSymbols.SetEquals(newSymbols))
            {
                _logger.LogInformation("🔄 Symbols changed: {Old} -> {New}. Requesting reconnect...",
                    string.Join(",", currentSymbols),
                    string.Join(",", newSymbols));

                currentSymbols = newSymbols;
                RequestReconnect();
            }
        });

        // Также проверяем раз в 5 секунд (на всякий случай)
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public void RequestReconnect()
    {
        // Помечаем, что нужно переподключиться
        // Это будет обработано в BinanceWsClient
        _logger.LogInformation("Reconnect requested.");
        // Здесь можно использовать событие, канал, или флаг
    }
}
// TradeBatchWriter.cs
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Threading.Channels;

public interface ITradeBatchWriter
{
    ValueTask AddAsync(Trade trade, CancellationToken ct = default);
    long GetProcessedCount();
}

public sealed class TradeBatchWriter : ITradeBatchWriter, IHostedService
{
    private readonly Channel<Trade> _channel;
    private readonly TradeRepository _repo;
    private readonly ILogger<TradeBatchWriter> _logger;
    private long _processedCount;
    private Task? _processingTask;

    public TradeBatchWriter(
        TradeRepository repo,
        ILogger<TradeBatchWriter> logger)
    {
        _repo = repo;
        _logger = logger;
        _channel = Channel.CreateBounded<Trade>(new BoundedChannelOptions(50_000)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
    }

    public ValueTask AddAsync(Trade trade, CancellationToken ct = default) =>
        _channel.Writer.TryWrite(trade)
            ? default
            : _channel.Writer.WriteAsync(trade, ct); // fallback для Wait

    public long GetProcessedCount() => Interlocked.Read(ref _processedCount);

    public Task StartAsync(CancellationToken ct)
    {
        _processingTask = Task.Run(() => ProcessBatchesAsync(ct), ct);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        _logger.LogInformation("Stopping batch writer...");
        _channel.Writer.Complete();

        if (_processingTask is not null)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10)); // даем 10 сек на финальную выгрузку

            try
            {
                await _processingTask.WaitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Batch processor did not finish in time. Data may be lost.");
            }
        }

        _logger.LogInformation("Batch writer stopped. Total trades: {Count}", _processedCount);
    }

    private async Task ProcessBatchesAsync(CancellationToken ct)
    {
        var batch = new List<Trade>(200);
        var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250)); // 4 батча/сек

        _logger.LogInformation("🚀 Batch processor started.");

        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                // Сборка батча
                while (batch.Count < 200 && _channel.Reader.TryRead(out var trade))
                {
                    batch.Add(trade);
                }

                if (batch.Count > 0)
                {
                    var sw = Stopwatch.StartNew();
                    await _repo.SaveBatchAsync(batch, ct);
                    sw.Stop();

                    Interlocked.Add(ref _processedCount, batch.Count);
                    _logger.LogDebug(
                        "Saved {Count} trades in {Ms} ms (avg {AvgMs} ms/trade)",
                        batch.Count, sw.ElapsedMilliseconds,
                        Math.Round(sw.ElapsedMilliseconds / (double)batch.Count, 2));

                    batch.Clear();
                }
            }

            // Финальная выгрузка
            while (_channel.Reader.TryRead(out var trade))
            {
                batch.Add(trade);
                if (batch.Count >= 200)
                {
                    await _repo.SaveBatchAsync(batch, ct);
                    Interlocked.Add(ref _processedCount, batch.Count);
                    batch.Clear();
                }
            }
            if (batch.Count > 0)
            {
                await _repo.SaveBatchAsync(batch, ct);
                Interlocked.Add(ref _processedCount, batch.Count);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogInformation("Batch processor cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Batch processor crashed");
        }
    }
}
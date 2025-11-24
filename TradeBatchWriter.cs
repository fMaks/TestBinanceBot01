using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.Threading.Channels;

using TestBinanceBot01.Services;

public interface ITradeBatchWriter
{
    ValueTask AddAsync(Trade trade, CancellationToken ct = default);
    long GetProcessedCount();
}

public sealed class TradeBatchWriter : ITradeBatchWriter, IHostedService
{
    private readonly Channel<Trade> _channel;
    // ChannelWriter<Trade> channelWriter;
    //ChannelReader<Trade> chReader;
    private readonly TradeRepository _repo;
    private readonly ILogger<TradeBatchWriter> _logger;
    private readonly IStatisticsService _stats;
    private readonly int _batchSize;
    private long _processedCount = 0;


    // ✅ Добавим поле для задачи
    private Task? _processingTask;

    public long GetProcessedCount() => Interlocked.Read(ref _processedCount);

    public TradeBatchWriter(TradeRepository repo, ILogger<TradeBatchWriter> logger, IOptions<AppOptions> options, IStatisticsService stats)
    {
        _repo = repo ?? throw new ArgumentNullException(nameof(repo));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _stats = stats ?? throw new ArgumentNullException(nameof(stats));
        _batchSize = options.Value.BatchSize;
        _channel = Channel.CreateBounded<Trade>(new BoundedChannelOptions(50_000)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
        _logger.LogInformation("📦 TradeBatchWriter created.");
    }

    public ValueTask AddAsync(Trade trade, CancellationToken ct = default)
    {
        //_logger.LogDebug("📥 [BatchWriter] AddAsync called for trade {TradeId}", trade.TradeId);

        var result = _channel.Writer.WriteAsync(trade, ct); // ✅ Используем _channel.Writer

        //_logger.LogDebug("📊 Channel stats after WriteAsync: Count = {_channel.Reader.Count}", _channel.Reader.Count);

        return result;
    }

    public Task StartAsync(CancellationToken ct)
    {
        _logger.LogInformation("🚀 Batch processor service starting...");
        _logger.LogDebug("🔧 Starting ProcessBatchesAsync directly...");

        // Запускаем ProcessBatchesAsync напрямую (а не через Task.Run)
        _processingTask = ProcessBatchesAsync(ct);

        _logger.LogDebug("✅ ProcessBatchesAsync task created.");
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        _logger.LogInformation("🛑 Stopping batch writer...");
        _channel.Writer.Complete(); // ✅ Используем _channel.Writer

        if (_processingTask is not null)
        {
            // ✅ Ждём завершения обработки с таймаутом
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10)); // 10 сек на завершение

            try
            {
                await _processingTask.WaitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Batch processor did not finish in time.");
            }
        }

        _logger.LogInformation("Batch writer stopped.");
    }

    private async Task ProcessBatchesAsync(CancellationToken ct)
    {
        _logger.LogInformation("✅ Batch processor loop started.");
        _logger.LogDebug("🔄 ProcessBatchesAsync entered.");
        _logger.LogDebug("🔄 CancellationToken.IsCancellationRequested = {IsCancellationRequested}", ct.IsCancellationRequested);

        var batch = new List<Trade>(_batchSize);
        _logger.LogDebug("🔄 Batch list created.");

        try
        {
            _logger.LogDebug("🔄 Starting 'await foreach' read loop with ReadAllAsync...");

            // ✅ Используем ReadAllAsync — он должен читать, даже если данные были до старта
            await foreach (var trade in _channel.Reader.ReadAllAsync(ct))
            {
                _logger.LogDebug("📥 [READALL] Received trade {TradeId} from channel.", trade.TradeId);
                batch.Add(trade);
                _logger.LogDebug("📥 Added trade {TradeId} to batch. Current batch size: {BatchSize}", trade.TradeId, batch.Count);

                if (batch.Count >= _batchSize)
                {
                    await FlushBatchAsync(batch, ct);
                    batch.Clear();
                }
            }
            // Сброс оставшихся данных
            if (batch.Count > 0)
            {
                _logger.LogInformation("💾 Flushing remaining {Count} trades before exit.", batch.Count);
                await FlushBatchAsync(batch, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogInformation("Batch processor cancelled.");
            if (batch.Count > 0)
            {
                _logger.LogInformation("💾 Flushing remaining {Count} trades before exit.", batch.Count);
                await FlushBatchAsync(batch, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Batch processor crashed");
        }
    }

    private async Task FlushBatchAsync(List<Trade> batch, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        await _repo.SaveBatchAsync(batch, ct);
        sw.Stop();
        _stats.IncrementProcessedCount(batch.Count);
        _logger.LogDebug("✅ Saved {Count} trades in {Ms} ms", batch.Count, sw.ElapsedMilliseconds);
    }

    long ITradeBatchWriter.GetProcessedCount() => _stats.GetProcessedCount();
}
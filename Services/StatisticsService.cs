// Services/StatisticsService.cs
namespace TestBinanceBot01.Services;

public class StatisticsService : IStatisticsService
{
    private long _processedCount;

    public long GetProcessedCount() => Interlocked.Read(ref _processedCount);

    public void IncrementProcessedCount(int count = 1)
    {
        Interlocked.Add(ref _processedCount, count);
    }
}
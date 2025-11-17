public class DirectWriter : ITradeBatchWriter
{
    private readonly TradeRepository _repo;
    public DirectWriter(TradeRepository repo) => _repo = repo;

    public async ValueTask AddAsync(Trade trade, CancellationToken ct = default)
    {
        await _repo.SaveAsync(trade, ct); // ← по одной записи
    }
    public long GetProcessedCount() => 0;
}

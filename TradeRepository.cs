using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using System.Diagnostics;
using System.Xml.Linq;

public sealed class TradeRepository
{
    private readonly string _conn;
    private readonly ILogger<TradeRepository> _logger;

    public TradeRepository(IConfiguration cfg, ILogger<TradeRepository> logger)
    {
        _conn = cfg.GetConnectionString("Postgres")
                ?? throw new ArgumentNullException("Postgres");
        _logger = logger;
    }

    public async Task SaveBatchAsync(List<Trade> trades, CancellationToken ct = default)
    {
        using var connection = new NpgsqlConnection(_conn);
        await connection.OpenAsync(ct);

        using var batch = new NpgsqlBatch(connection);

        foreach (var trade in trades)
        {
            //var command = new NpgsqlBatchCommand(
            //"INSERT INTO trades (symbol, utime, trade_id, price, quantity) VALUES (@symbol, @utime, @trade_id, @price, @quantity)");
            var command = new NpgsqlBatchCommand(
                "INSERT INTO trades (symbol, utime, trade_id, price, quantity) VALUES ($1, $2, $3, $4, $5)");

            command.Parameters.Add(new NpgsqlParameter { Value = trade.Symbol });
            command.Parameters.Add(new NpgsqlParameter { Value = trade.TradeTime });
            command.Parameters.Add(new NpgsqlParameter { Value = trade.TradeId });
            command.Parameters.Add(new NpgsqlParameter { Value = trade.Price });
            command.Parameters.Add(new NpgsqlParameter { Value = trade.Quantity });

            batch.BatchCommands.Add(command);
        }

        await batch.ExecuteNonQueryAsync(ct);
    }
    /*
    public async Task SaveBatchAsync(List<Trade> trades, CancellationToken ct = default)
    {
        _logger.LogInformation("Saving batch of {Count} trades", trades.Count);
        if (trades.Count == 0) return;

        // Валидация
        foreach (var t in trades)
        {
            if (!IsValidSymbol(t.Symbol))
                throw new ArgumentException($"Invalid symbol: {t.Symbol}");
        }

        await using var conn = new NpgsqlConnection(_conn);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        try
        {
            // Единая таблица: trades(symbol, utime, trade_id, price, quantity)
            const string sql = """
            INSERT INTO trades (symbol, utime, trade_id, price, quantity)
            SELECT * FROM UNNEST($1::text[], $2::timestamptz[], $3::bigint[], $4::numeric[], $5::numeric[])
            """;

            var symbols = trades.Select(t => t.Symbol).ToArray();
            var utimes = trades.Select(t => t.TradeTime).ToArray();
            var tradeIds = trades.Select(t => t.TradeId).ToArray();
            var prices = trades.Select(t => t.Price).ToArray();
            var quantities = trades.Select(t => t.Quantity).ToArray();

            await using var cmd = new NpgsqlCommand(sql, conn, tx);

            cmd.Parameters.Add(new NpgsqlParameter<string[]>("symbols", symbols));
            var utimesParam = new NpgsqlParameter("utimes", NpgsqlDbType.TimestampTz) { Value = utimes };
            cmd.Parameters.Add(utimesParam);
            cmd.Parameters.Add(new NpgsqlParameter<long[]>("tradeIds", tradeIds));
            cmd.Parameters.Add(new NpgsqlParameter<decimal[]>("prices", prices));
            cmd.Parameters.Add(new NpgsqlParameter<decimal[]>("quantities", quantities));

            await cmd.ExecuteNonQueryAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "💥 Failed to save batch of {Count} trades", trades.Count);
            await tx.RollbackAsync(ct);
            throw;
        }
    }
    */

    private static bool IsValidSymbol(string symbol) =>
        !string.IsNullOrWhiteSpace(symbol) && symbol.All(c => char.IsLetterOrDigit(c));

    public async Task SaveAsync(Trade trade, CancellationToken ct = default)
    {
        try
        {
            string sql = $"""INSERT INTO trades(utime, trade_id, price, quantity ) VALUES ($1, $2, $3, $4);""";
            await using var conn = new NpgsqlConnection(_conn);
            await conn.OpenAsync(ct);
            //long tUnixTime = ((DateTimeOffset)trade.TradeTime).ToUnixTimeSeconds();
            await using (var cmdc = new NpgsqlCommand($"INSERT INTO trades VALUES('{trade.Symbol}', '{trade.TradeTime}', {trade.TradeId}, {trade.Price}, {trade.Quantity});", conn))
            {
                await cmdc.ExecuteNonQueryAsync();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to save trade for symbol {trade.Symbol}: {trade.TradeId}");
            Console.WriteLine (ex.Message);
            throw; // чтобы вызвался reconnect
        }
    }
}

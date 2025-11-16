using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using System.Diagnostics;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;

public sealed class BinanceWsClient : BackgroundService
{
    //private readonly TradeRepository _repo;
    private readonly AppOptions _opt;
    private readonly ILogger<BinanceWsClient> _log;
    private readonly ITradeBatchWriter _batchWriter;
    ulong CountTick = 0;
    int CountReconnect = 0;

    private ClientWebSocket? _ws;
    private readonly string _connectionString;

    public BinanceWsClient(
        ITradeBatchWriter batchWriter,
        IOptions<AppOptions> opt,
        IConfiguration configuration,
        ILogger<BinanceWsClient> log)
    {
        _batchWriter = batchWriter;
        _opt = opt.Value;
        _connectionString = configuration.GetConnectionString("Postgres")
                            ?? throw new InvalidOperationException("Missing 'Postgres' connection string");
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var url = $"wss://stream.binance.com:9443/ws/{string.Join('/', _opt.Symbols.Select(s => $"{s.ToLower()}@trade"))}";

        // 🔹 Тест подключения к БД
        using (var conn = new NpgsqlConnection(_connectionString))
        {
            try
            {
                await conn.OpenAsync(ct);
                var version = conn.PostgreSqlVersion;
                _log.LogInformation("✅ Connected to PostgreSQL {Version} as user '{User}' on database '{Db}'",
                    version, conn.UserName, conn.Database);
            }
            catch (Exception ex)
            {
                _log.LogCritical(ex, "❌ Failed to connect to PostgreSQL");
                throw;
            }
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ConnectAndReceiveAsync(url, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log.LogError(ex.Message, $"{DateTime.Now} WebSocket error, reconnect in 5 s");
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
    }

    private async Task ConnectAndReceiveAsync(string url, CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(5);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_ws is not null)
                {
                    await CloseWebSocketAsync(ct);
                }

                _ws = new ClientWebSocket();
                Console.WriteLine($"{DateTime.Now} Connecting to {url}");
                await _ws.ConnectAsync(new Uri(url), ct);
                await ReceiveLoopAsync(ct);
                // Если вышли из ReceiveLoop — соединение закрыто штатно
                break;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                Console.WriteLine ($"{DateTime.Now} Binance connection canceled.");
                break;
            }
            catch (Exception ex)
            {
                //Console.WriteLine($"{DateTime.Now} Binance WebSocket error. {ex.Message}");
                Console.WriteLine ($"{DateTime.Now} Reconnecting in {delay}s...");
                await Task.Delay(delay, ct);
                Console.WriteLine($"Reconnect : {++CountReconnect}");
            }
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new ArraySegment<byte>(new byte[8192]); // Binance сообщения обычно <4KB
        using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var lastMessageTime = DateTime.UtcNow;
        //var pingTask = PingLoopAsync(pingCts.Token);
        
        try
        {
            while (_ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                // Таймаут неактивности
                if ((DateTime.UtcNow - lastMessageTime).TotalSeconds > 60)
                {
                    _log.LogWarning("No messages from Binance for 60 seconds. Reconnecting...");
                    throw new TimeoutException("Binance heartbeat timeout");
                }
                
                var sb = new StringBuilder();
                WebSocketReceiveResult result;

                do
                {
                    result = await _ws.ReceiveAsync(buffer, ct);
                    lastMessageTime = DateTime.UtcNow; // обновляем при любом сообщении

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        _log.LogInformation("Binance closed WebSocket: {Status} - {Desc}",
                            result.CloseStatus, result.CloseStatusDescription);
                        return;
                    }

                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        sb.Append(Encoding.UTF8.GetString(buffer.Array!, buffer.Offset, result.Count));
                    }

                } while (!result.EndOfMessage && !ct.IsCancellationRequested);

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    /*
                     * → Binance никогда не отправляет текстовое "ping" в стриме @trade
                    if (sb.ToString().Contains("ping") )
                    {
                        Console.WriteLine("PING");
                    }
                    */
                    await HandleMessageAsync(sb.ToString(), ct);
                }
            }
        }
        catch (WebSocketException wsex) when (wsex.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
        {
            //Console.WriteLine("Binance closed connection prematurely (normal). Reconnecting...");
            _log.LogInformation("Binance closed connection prematurely (normal). Reconnecting...");
            return; // выйти из ReceiveLoop, чтобы запустить reconnect
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Error receiving message from Binance");
            throw; // чтобы вызвался reconnect
        }
        /*
        finally
        {
            if (!ct.IsCancellationRequested)
                await pingTask; // дождаться завершения ping-задачи
        }
        */
    }

    private async Task HandleMessageAsync(string json, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Более надёжная проверка
            if (!root.TryGetProperty("e", out var e) || e.GetString() != "trade")
                return;

            if (!root.TryGetProperty("s", out var s) || s.ValueKind != JsonValueKind.String)
                return;

            var symbol = s.GetString();
            if (string.IsNullOrWhiteSpace(symbol)) return;

            // ✅ Защита от SQL-инъекции (если таблицы динамические)
            if (!IsValidSymbol(symbol))
            {
                _log.LogWarning("Invalid symbol received: {Symbol}", symbol);
                return;
            }

            var trade = new Trade(
                Id: 0,
                Symbol: symbol,
                Price: decimal.TryParse(
                    root.GetProperty("p").GetString(),
                    NumberStyles.Number,
                    CultureInfo.InvariantCulture,
                    out var p) ? p : 0m,
                Quantity: decimal.TryParse(
                    root.GetProperty("q").GetString(),
                    NumberStyles.Number,
                    CultureInfo.InvariantCulture,
                    out var q) ? q : 0m,
                TradeId: root.TryGetProperty("t", out var t) ? t.GetInt64() : 0,
                TradeTime: root.TryGetProperty("T", out var T)
                    ? DateTimeOffset.FromUnixTimeMilliseconds(T.GetInt64()).UtcDateTime
                    : DateTime.UtcNow
            );

            // 🔹 Счётчик
            var count = Interlocked.Increment(ref CountTick);
            if (count % 100 == 0)
            {
                LogProgress(count); // вынесено в отдельный метод
            }

            // ✅ Батчинг
            try
            {
                await _batchWriter.AddAsync(trade, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Штатное завершение — не логируем как ошибку
                return;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to add trade to batch");
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to process Binance message: {JsonPreview}",
                json.Length > 120 ? json[..120] : json);
            // Не бросаем — не рвём WebSocket из-за одного плохого сообщения
        }
    }

    private static bool IsValidSymbol(string symbol) =>
        !string.IsNullOrWhiteSpace(symbol) &&
        symbol.Length is >= 4 and <= 20 &&
        symbol.All(c => char.IsLetterOrDigit(c));

    private void LogProgress(ulong count)
    {
        if (!Environment.UserInteractive) return;

        try
        {
            var pos = Console.GetCursorPosition();
            Console.SetCursorPosition(0, 0);
            Console.Write($"Trades: {count,12}  "); // 2 пробела — затираем старое
            if (pos.Left < Console.BufferWidth && pos.Top < Console.BufferHeight)
                Console.SetCursorPosition(pos.Left, pos.Top);
        }
        catch { /* игнорируем ошибки консоли */ }
    }

    private async Task CloseWebSocketAsync(CancellationToken ct)
    {
        if (_ws is null) return;

        try
        {
            if (_ws.State == WebSocketState.Open)
            {
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Shutdown", ct);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Error closing WebSocket");
        }
        finally
        {
            _ws.Dispose();
            _ws = null;
        }
    }

    public override void Dispose()
    {
        _ws?.Dispose();
        base.Dispose();
    }

}

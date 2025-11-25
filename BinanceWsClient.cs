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
    private readonly IOptionsMonitor<AppOptions> _opt;
    private readonly ILogger<BinanceWsClient> _log;
    private readonly ITradeBatchWriter _batchWriter;
    private readonly string _connectionString;
    private CancellationTokenSource? _receiveCts;
    private readonly ISymbolValidator _symbolValidator;
    private readonly IAppConfigManager _configManager;

    int CountReconnect = 0;

    private ClientWebSocket? _ws;
    private string _wsUrl;
    private volatile bool _reconnectRequested = false;
    private volatile HashSet<string> _currentSymbols = new(StringComparer.OrdinalIgnoreCase);

    public BinanceWsClient(
        ITradeBatchWriter batchWriter,
        IOptionsMonitor<AppOptions> opt,
        IConfiguration configuration,
        ILogger<BinanceWsClient> log,
        ISymbolValidator symbolValidator,
        IAppConfigManager configManager)
    {
        _batchWriter = batchWriter;
        _configManager = configManager;
        _opt = opt;
        _symbolValidator = symbolValidator;
        _connectionString = configuration.GetConnectionString("Postgres")
                            ?? throw new InvalidOperationException("Missing 'Postgres' connection string");
        _log = log;

        _opt.OnChange(async options =>
        {
            // ✅ Валидируем и получаем **только валидные** символы
            var validSymbols = new HashSet<string>(
                await _configManager.GetValidatedSymbolsAsync(_symbolValidator, CancellationToken.None),
                StringComparer.OrdinalIgnoreCase);

            _log.LogDebug("Validated symbols from config: {Symbols}", string.Join(", ", validSymbols));

            // ✅ Сравниваем с текущими валидными символами
            if (!validSymbols.SetEquals(_currentSymbols)) // ❗ _currentSymbols — поле класса
            {
                _log.LogInformation("🔄 Symbols changed: {Old} -> {New}. Requesting reconnect...",
                    string.Join(",", _currentSymbols),
                    string.Join(",", validSymbols));

                _currentSymbols = validSymbols; // ✅ Обновляем поле
                _reconnectRequested = true;

                // ✅ Прерываем текущий ReceiveLoop
                _receiveCts?.Cancel();
            }
        });
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
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

        // ✅ Валидируем и получаем символы ОДИН раз при запуске
        var currentSymbols = new HashSet<string>(
            await _configManager.GetValidatedSymbolsAsync(_symbolValidator, ct),
            StringComparer.OrdinalIgnoreCase);

        _log.LogInformation("Initial validated symbols: {Symbols}", string.Join(", ", currentSymbols));

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // ✅ НЕ вызываем GetValidatedSymbolsAsync второй раз!
                // Вместо этого — используем IOptionsMonitor для отслеживания изменений
                // и валидируем в OnChange

                var url = $"wss://stream.binance.com:9443/ws/{string.Join('/', currentSymbols.Select(s => $"{s.ToLower()}@trade"))}";

                await ConnectAndReceiveAsync(url, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log.LogError(ex, "WebSocket error, reconnecting in 5 seconds...");
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
                _log.LogInformation($"{DateTime.Now} Connecting to {url}");
                await _ws.ConnectAsync(new Uri(url), ct);
                // ✅ СБРАСЫВАЕМ флаг ПОСЛЕ успешного подключения
                _reconnectRequested = false;
                // ✅ Создаём новый CancellationTokenSource для ReceiveLoop
                _receiveCts?.Cancel(); // Прерываем предыдущий цикл
                _receiveCts = new CancellationTokenSource();
                await ReceiveLoopAsync(_receiveCts.Token);
                // Если вышли из ReceiveLoop — соединение закрыто штатно
                break;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                _log.LogInformation($"{DateTime.Now} Binance connection canceled.");
                break;
            }
            catch (Exception ex)
            {
                //Console.WriteLine($"{DateTime.Now} Binance WebSocket error. {ex.Message}");
                _log.LogInformation($"{DateTime.Now} Reconnecting in {delay}s...");
                await Task.Delay(delay, ct);
                _log.LogInformation($"Reconnect : {++CountReconnect}");
            }
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        _log.LogInformation("ReceiveLoopAsync starting");
        var buffer = new ArraySegment<byte>(new byte[8192]); // Binance сообщения обычно <4KB
        using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var lastMessageTime = DateTime.UtcNow;
        //var pingTask = PingLoopAsync(pingCts.Token);
        
        try
        {
            while (_ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                // ✅ Проверяем, нужно ли переподключиться
                if (_reconnectRequested)
                {
                    _log.LogInformation("Reconnect requested during ReceiveLoop. Exiting...");
                    return; // Выходим из цикла
                }
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

                    //_log.LogDebug("📥 Received fragment: Type={Type}, Count={Count}, EndOfMessage={EndOfMessage}",
                    //result.MessageType, result.Count, result.EndOfMessage);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        _log.LogInformation("Binance closed WebSocket: {Status} - {Desc}",
                            result.CloseStatus, result.CloseStatusDescription);
                        return;
                    }

                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        var text = Encoding.UTF8.GetString(buffer.Array!, buffer.Offset, result.Count);
                        //_log.LogDebug("📄 Text fragment: {Text}", text.Length > 100 ? text[..100] : text);
                        sb.Append(text);
                    }
                    else if (result.MessageType == WebSocketMessageType.Binary)
                    {
                        _log.LogDebug("📄 Received binary message of {Count} bytes", result.Count);
                    }

                } while (!result.EndOfMessage && !ct.IsCancellationRequested);

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var fullJson = sb.ToString();
                    //_log.LogDebug("📄 Full JSON received: {Json}", fullJson.Length > 200 ? fullJson[..200] : fullJson);

                    // ✅ Вызов HandleMessageAsync
                    await HandleMessageAsync(fullJson, ct);
                    //_log.LogDebug("📤 HandleMessageAsync called successfully.");
                }
                else
                {
                    _log.LogDebug("result.MessageType != WebSocketMessageType.Text");
                }
            }
        }
        catch (WebSocketException wsex) when (wsex.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
        {
            _log.LogInformation("Binance closed connection prematurely (normal). Reconnecting...");
            return; // выйти из ReceiveLoop, чтобы запустить reconnect
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // ✅ Не логируем как ошибку — это нормальное поведение
            _log.LogDebug("ReceiveLoop canceled (reconnect requested or shutdown).");
            return;
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
        //_log.LogDebug("HandleMessageAsync called with JSON: {JsonLength} chars", json.Length);

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Более надёжная проверка
            if (!root.TryGetProperty("e", out var e) || e.GetString() != "trade")
            {
                _log.LogWarning("❌ No 'e' property in JSON: {JsonPreview}", json.Length > 100 ? json[..100] : json);
                return;
            }
            var eventType = e.GetString();
            //_log.LogDebug("Event type: '{EventType}'", eventType);
            
            if (eventType != "trade")
            {
                _log.LogDebug("Skipping non-trade event: '{EventType}'", eventType);
                return;
            }

            if (!root.TryGetProperty("s", out var s) || s.ValueKind != JsonValueKind.String)
            {
                _log.LogWarning("❌ No valid 's' (symbol) property in JSON: {JsonPreview}", json.Length > 100 ? json[..100] : json);
                return;
            }
            var symbol = s.GetString();
            //_log.LogDebug("Symbol: {Symbol}", symbol);

            if (string.IsNullOrWhiteSpace(symbol))
            {
                _log.LogWarning("❌ Empty symbol in JSON: {JsonPreview}", json.Length > 100 ? json[..100] : json);
                return;
            }

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

            // ✅ Батчинг
            try
            {
                await _batchWriter.AddAsync(trade, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Штатное завершение — не логируем как ошибку
                _log.LogInformation("Batch writer cancelled during AddAsync");
                return;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to add trade to batch");
                throw;
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

    private async Task<List<string>> GetValidatedSymbolsAsync(List<string> symbols, CancellationToken ct)
    {
        var localSymbols = symbols.Select(s => s.ToUpperInvariant()).ToList();
        var validatedSymbols = await _symbolValidator.ValidateSymbolsAsync(localSymbols, ct);
        return validatedSymbols;
    }

    public override void Dispose()
    {
        _ws?.Dispose();
        base.Dispose();
    }

}

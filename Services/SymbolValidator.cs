using Microsoft.Extensions.Logging;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

public class SymbolValidator : ISymbolValidator
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<SymbolValidator> _logger;

    // ✅ Кеш для валидации
    private static readonly MemoryCache _cache = new(new MemoryCacheOptions
    {
    });

    private static readonly TimeSpan _cacheExpiry = TimeSpan.FromMinutes(10);

    public SymbolValidator(HttpClient httpClient, ILogger<SymbolValidator> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<List<string>> ValidateSymbolsAsync(List<string> symbols, CancellationToken ct = default)
    {
        if (symbols == null || symbols.Count == 0) return new List<string>();

        var cacheKey = string.Join(",", symbols.OrderBy(s => s));
        if (_cache.TryGetValue(cacheKey, out List<string> cachedResult))
        {
            return cachedResult;
        }

        var result = await ValidateSymbolsInternalAsync(symbols, ct);

        var cacheOptions = new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = _cacheExpiry,
            Size = 1 // ✅ Размер
        };
        // ✅ Кешируем результат
        _cache.Set(cacheKey, result, _cacheExpiry);

        return result;
    }

    private async Task<List<string>> ValidateSymbolsInternalAsync(List<string> symbols, CancellationToken ct)
    {
        var validSymbols = new List<string>();
        var invalidSymbols = new List<string>();

        // ✅ Сначала проверяем формат
        var formattedSymbols = new List<string>();
        foreach (var symbol in symbols)
        {
            if (IsValidSymbolFormat(symbol))
            {
                formattedSymbols.Add(symbol.ToUpperInvariant());
            }
            else
            {
                invalidSymbols.Add(symbol);
            }
        }

        if (invalidSymbols.Count > 0)
        {
            _logger.LogWarning("Invalid symbol format: {InvalidSymbols}", string.Join(", ", invalidSymbols));
        }

        // ✅ Потом проверяем через API
        try
        {
            var response = await _httpClient.GetStringAsync("https://api.binance.com/api/v3/exchangeInfo", ct);
            using var doc = JsonDocument.Parse(response);
            var symbolsArray = doc.RootElement.GetProperty("symbols");

            var binanceSymbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var s in symbolsArray.EnumerateArray())
            {
                var symbol = s.GetProperty("symbol").GetString();
                if (!string.IsNullOrEmpty(symbol))
                {
                    binanceSymbols.Add(symbol);
                }
            }

            foreach (var symbol in formattedSymbols)
            {
                if (binanceSymbols.Contains(symbol))
                {
                    validSymbols.Add(symbol);
                }
                else
                {
                    invalidSymbols.Add(symbol);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to validate symbols against Binance API");
            // Если API недоступен — возвращаем только отформатированные символы
            return formattedSymbols;
        }

        if (invalidSymbols.Count > 0)
        {
            _logger.LogWarning("Invalid symbols not found on Binance: {InvalidSymbols}", string.Join(", ", invalidSymbols));
        }

        return validSymbols;
    }

    private static bool IsValidSymbolFormat(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return false;
        if (symbol.Length < 4 || symbol.Length > 12) return false;
        return symbol.All(c => char.IsLetterOrDigit(c));
    }
}
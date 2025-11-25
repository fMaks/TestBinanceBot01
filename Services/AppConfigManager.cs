// Services/AppConfigManager.cs
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public class AppConfigManager : IAppConfigManager
{
    private readonly string _configPath;
    private readonly ILogger<AppConfigManager> _logger;
    private volatile bool _configCleaned = false;
    private DateTime _lastReadTime = DateTime.MinValue;
    private List<string> _cachedValidSymbols = new();
    private readonly SemaphoreSlim _semaphore = new(1, 1); // ✅ Добавляем

    public AppConfigManager(IConfiguration configuration, ILogger<AppConfigManager> logger)
    {
        _logger = logger;
        _configPath = Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json");
        if (!File.Exists(_configPath))
        {
            _configPath = "appsettings.json";
        }
    }

    public async Task<List<string>> GetValidatedSymbolsAsync(ISymbolValidator symbolValidator, CancellationToken ct = default)
    {
        // ✅ Блокируем доступ к методу
        await _semaphore.WaitAsync(ct);

        try
        {
            var fileInfo = new FileInfo(_configPath);
            if (fileInfo.Exists && fileInfo.LastWriteTimeUtc > _lastReadTime)
            {
                _logger.LogDebug("Config file changed. Revalidating symbols...");
                var validSymbols = await ValidateAndCleanConfigAsync(symbolValidator, ct);
                _cachedValidSymbols = validSymbols;
                _lastReadTime = fileInfo.LastWriteTimeUtc;
            }

            return _cachedValidSymbols;
        }
        finally
        {
            _semaphore.Release(); // ✅ Всегда освобождаем
        }
    }

    private async Task<List<string>> ValidateAndCleanConfigAsync(ISymbolValidator symbolValidator, CancellationToken ct)
    {
        var json = await File.ReadAllTextAsync(_configPath);
        var jObject = JsonConvert.DeserializeObject<JObject>(json, new JsonSerializerSettings
        {
            DateParseHandling = DateParseHandling.None
        });

        var symbols = jObject["AppOptions"]?["Symbols"]?.ToObject<List<string>>() ?? new List<string>();

        var validSymbols = await symbolValidator.ValidateSymbolsAsync(symbols, ct);

        var invalidSymbols = symbols.Except(validSymbols, StringComparer.OrdinalIgnoreCase).ToList();

        if (invalidSymbols.Count > 0)
        {
            _logger.LogWarning("Invalid symbols found: {InvalidSymbols}", string.Join(", ", invalidSymbols));

            // ✅ Удаляем только при первом запуске
            if (!_configCleaned)
            {
                await RemoveInvalidSymbolsAsync(invalidSymbols);
                _configCleaned = true;
            }
        }

        return validSymbols;
    }

    private async Task RemoveInvalidSymbolsAsync(List<string> invalidSymbols)
    {
        var json = await File.ReadAllTextAsync(_configPath);
        var jObject = JsonConvert.DeserializeObject<JObject>(json);

        var symbolsArray = jObject["AppOptions"]?["Symbols"]?.ToObject<List<string>>() ?? new List<string>();
        var newSymbols = symbolsArray.Where(s => !invalidSymbols.Contains(s, StringComparer.OrdinalIgnoreCase)).ToList();

        jObject["AppOptions"]["Symbols"] = JArray.FromObject(newSymbols);

        var output = jObject.ToString(Formatting.Indented);
        await File.WriteAllTextAsync(_configPath, output);

        _logger.LogInformation("Invalid symbols removed from appsettings.json: {InvalidSymbols}", string.Join(", ", invalidSymbols));
    }
}
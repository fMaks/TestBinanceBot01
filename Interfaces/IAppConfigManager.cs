// Interfaces/IAppConfigManager.cs
public interface IAppConfigManager
{
    Task<List<string>> GetValidatedSymbolsAsync(ISymbolValidator symbolValidator, CancellationToken ct = default);
}

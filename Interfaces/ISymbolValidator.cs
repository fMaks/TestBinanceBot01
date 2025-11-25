// Interfaces/ISymbolValidator.cs
public interface ISymbolValidator
{
    Task<List<string>> ValidateSymbolsAsync(List<string> symbols, CancellationToken ct = default);
}
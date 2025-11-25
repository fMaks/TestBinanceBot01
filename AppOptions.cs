public sealed class AppOptions
{
    public List<string> Symbols { get; set; } = new();
    public string Postgres { get; set; } = "";
    public int BatchSize { get; set; } = 100;

    public List<string> GetValidatedSymbols()
    {
        var validSymbols = new List<string>();
        var invalidSymbols = new List<string>();

        foreach (var symbol in Symbols)
        {
            if (IsValidSymbol(symbol))
            {
                validSymbols.Add(symbol.ToUpperInvariant());
            }
            else
            {
                invalidSymbols.Add(symbol);
            }
        }

        if (invalidSymbols.Count > 0)
        {
            Console.WriteLine($"⚠️ Invalid symbols: {string.Join(", ", invalidSymbols)}");
        }

        return validSymbols;
    }

    private static bool IsValidSymbol(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return false;
        if (symbol.Length < 4 || symbol.Length > 12) return false;
        return symbol.All(c => char.IsLetterOrDigit(c));
    }

}

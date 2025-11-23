public sealed class AppOptions
{
    public string[] Symbols { get; set; } = [];
    public string Postgres { get; set; } = "";
    public int BatchSize { get; set; } = 5; // по умолчанию = 5
}

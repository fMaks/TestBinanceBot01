public sealed record Trade(
    long Id,
    string Symbol,
    decimal Price,
    decimal Quantity,
    long TradeId,
    DateTime TradeTime);
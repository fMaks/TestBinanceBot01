
//public record Trade
//{
//    private int id;
//    private string symbol;
//    private decimal price;
//    private decimal quantity;
//    private long tradeId;
//    private DateTime tradeTime;

//    public Trade(int Id, string Symbol, decimal Price, decimal Quantity, long TradeId, DateTime TradeTime)
//    {
//        id = Id;
//        symbol = Symbol;
//        price = Price;
//        quantity = Quantity;
//        tradeId = TradeId;
//        tradeTime = TradeTime;
//    }
//}

public sealed record Trade(
    long Id,
    string Symbol,
    decimal Price,
    decimal Quantity,
    long TradeId,
    DateTime TradeTime);
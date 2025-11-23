CREATE TABLE trades (
    symbol TEXT NOT NULL,
    price DECIMAL NOT NULL,
    quantity DECIMAL NOT NULL,
    trade_id BIGINT NOT NULL,
    utime TIMESTAMPTZ NOT NULL,
    PRIMARY KEY (symbol, trade_id)
);
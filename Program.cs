using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;

var builder = Host.CreateApplicationBuilder(args);

// 🔹 Логирование конфигурации для отладки
var connStr = builder.Configuration.GetConnectionString("Postgres");
Console.WriteLine($"🔍 Connection string: {(string.IsNullOrEmpty(connStr) ? "MISSING" : "OK")}");

// Настройки
builder.Services.Configure<AppOptions>(
    builder.Configuration.GetSection(nameof(AppOptions)));

builder.Services.AddScoped<TradeRepository>();

//builder.Services.AddSingleton<ITradeBatchWriter, TradeBatchWriter>();
//builder.Services.AddHostedService<TradeBatchWriter>(); // ← он же IHostedService
builder.Services.AddSingleton<ITradeBatchWriter, DirectWriter>();
builder.Services.AddHostedService<BinanceWsClient>();

var host = builder.Build();
await host.RunAsync();

Console.ReadLine();

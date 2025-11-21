using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(new ConfigurationBuilder()
        .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
        .Build())
    .CreateBootstrapLogger(); // Для логирования до загрузки Host

try
{
    Log.Information("------------------------------------------------");
    Log.Information("Starting up...");

    var builder = Host.CreateApplicationBuilder(args);

    builder.Logging.ClearProviders(); // Убираем стандартные (Console, Debug)
    builder.Logging.AddSerilog(Log.Logger);

    builder.Services.Configure<AppOptions>(
        builder.Configuration.GetSection(nameof(AppOptions)));

    builder.Services.AddScoped<TradeRepository>();
    builder.Services.AddSingleton<ITradeBatchWriter, TradeBatchWriter>();
    builder.Services.AddHostedService<TradeBatchWriter>(); // ← он же IHostedService
    builder.Services.AddHostedService<BinanceWsClient>();

    var host = builder.Build();
    Log.Information("Application built. Running...");
    await host.RunAsync();

}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    await Log.CloseAndFlushAsync();
}

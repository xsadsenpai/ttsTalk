using Serilog;
using TolkTtsBot.Hubs;
using TolkTtsBot.Models;
using TolkTtsBot.Services;

Log.Logger = new LoggerConfiguration().WriteTo.Console().CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((ctx, svc, cfg) =>
        cfg.ReadFrom.Configuration(ctx.Configuration)
           .ReadFrom.Services(svc)
           .Enrich.FromLogContext()
           .Enrich.WithProperty("Service", "TolkTtsBot"));

    builder.Services.Configure<BotOptions>(
        builder.Configuration.GetSection(BotOptions.Section));
    builder.Services.Configure<TtsOptions>(
        builder.Configuration.GetSection(TtsOptions.Section));
    builder.Services.Configure<BrowserOptions>(
        builder.Configuration.GetSection(BrowserOptions.Section));

    // HTTP клиент для Silero sidecar
    builder.Services.AddHttpClient<ITtsService, SileroTtsService>((sp, client) =>
    {
        var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<TtsOptions>>().Value;
        client.BaseAddress = new Uri(opts.SidecarUrl);
        client.Timeout     = TimeSpan.FromSeconds(opts.TimeoutSeconds + 5);
    });

    // HTTP клиент для Толка (авторизация) — без BaseAddress, хост берётся из URL комнаты
    builder.Services.AddHttpClient<TolkChatService>(client =>
    {
        client.Timeout = TimeSpan.FromSeconds(20);
        client.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/124.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.Add("Accept",     "application/json");
        client.DefaultRequestHeaders.Add("X-Platform", "web");
    });

    builder.Services.AddSingleton<IBrowserService, PlaywrightBrowserService>();
    builder.Services.AddSingleton<TolkChatService>();
    builder.Services.AddSingleton<BotOrchestrator>();
    builder.Services.AddControllers();
    builder.Services.AddSignalR();
    builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
        p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

    // Render задаёт PORT через env; дефолт 10000
    var port = Environment.GetEnvironmentVariable("PORT") ?? "10000";
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

    var app = builder.Build();

    // Логируем пути для диагностики
    var contentRoot = AppContext.BaseDirectory;
    var logger      = app.Services.GetRequiredService<ILogger<Program>>();
    logger.LogInformation("ContentRoot: {R}", contentRoot);
    logger.LogInformation("PORT: {P}", port);
    logger.LogInformation("wwwroot exists: {E}",
        Directory.Exists(Path.Combine(contentRoot, "wwwroot")));

    app.UseSerilogRequestLogging(o => o.MessageTemplate =
        "HTTP {RequestMethod} {RequestPath} → {StatusCode} ({Elapsed:0}ms)");

    app.UseCors();
    app.UseDefaultFiles();
    app.UseStaticFiles();
    app.UseRouting();
    app.MapControllers();
    app.MapHub<BotHub>("/hub");

    // Fallback — отдаём index.html для любого не-API маршрута
    app.MapFallback(async ctx =>
    {
        var idx = Path.Combine(contentRoot, "wwwroot", "index.html");
        if (File.Exists(idx))
        {
            ctx.Response.ContentType = "text/html; charset=utf-8";
            await ctx.Response.SendFileAsync(idx);
        }
        else
        {
            ctx.Response.StatusCode = 404;
            await ctx.Response.WriteAsync($"index.html not found at: {idx}");
        }
    });

    logger.LogInformation("TolkTtsBot запущен на порту {P}", port);
    await app.RunAsync();
}
catch (Exception ex) { Log.Fatal(ex, "Сбой запуска"); throw; }
finally { Log.CloseAndFlush(); }

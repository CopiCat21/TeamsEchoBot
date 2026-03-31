using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Builder.Integration.AspNet.Core;
using Microsoft.Bot.Connector.Authentication;
using Serilog;
using TeamsEchoBot.Bot;
using TeamsEchoBot.Models;
using TeamsEchoBot.Services;

AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
{
    Log.Fatal(e.ExceptionObject as Exception, "UNHANDLED EXCEPTION: {Msg}",
        e.ExceptionObject?.ToString() ?? "unknown");
    Log.CloseAndFlush();
};

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console(outputTemplate:
        "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File("logs/teamsechobot-.txt",
        rollingInterval: RollingInterval.Day,
        outputTemplate:
        "[{Timestamp:yyyy-MM-dd HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

try
{
    Log.Information("Starting TeamsEchoBot...");

    var builder = WebApplication.CreateBuilder(args);
    builder.Host.UseSerilog();
    builder.Services.Configure<HostOptions>(options =>
    {
        options.ShutdownTimeout = TimeSpan.FromSeconds(15);
    });

    // ─── Configuration ────────────────────────────────────────────────────
    var botConfig = builder.Configuration.GetSection("Bot").Get<BotConfiguration>()
        ?? throw new InvalidOperationException("Missing 'Bot' section in appsettings.json");

    var speechConfig = builder.Configuration.GetSection("AzureSpeech").Get<SpeechConfiguration>()
        ?? throw new InvalidOperationException("Missing 'AzureSpeech' section in appsettings.json");

    // ─── Kestrel HTTPS ────────────────────────────────────────────────────
    builder.WebHost.ConfigureKestrel(options =>
    {
        options.ListenAnyIP(443, listenOptions =>
        {
            var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadOnly);

            var certs = store.Certificates.Find(
                X509FindType.FindByThumbprint,
                botConfig.CertThumbprint,
                validOnly: false);

            if (certs.Count == 0)
                throw new InvalidOperationException(
                    $"Cert '{botConfig.CertThumbprint}' not found in LocalMachine\\My.");

            listenOptions.UseHttps(new HttpsConnectionAdapterOptions
            {
                ServerCertificate = certs[0]
            });

            Log.Information("Kestrel HTTPS on 443 with cert: {Subject}", certs[0].Subject);
            store.Close();
        });
    });

    // ─── Services ─────────────────────────────────────────────────────────
    builder.Services.AddControllers().AddNewtonsoftJson();

    // Bot + Speech config
    builder.Services.AddSingleton(botConfig);
    builder.Services.AddSingleton(speechConfig);

    // Graph Communications calling service
    builder.Services.AddSingleton<BotService>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<BotService>());

    // Bot Framework (messaging)
    builder.Services.AddSingleton<BotFrameworkAuthentication>(sp =>
    {
        // Use the same AadAppId/AadAppSecret for both calling and messaging
        return new ConfigurationBotFrameworkAuthentication(
            builder.Configuration);
    });
    builder.Services.AddSingleton<IBotFrameworkHttpAdapter, AdapterWithErrorHandler>();
    builder.Services.AddTransient<IBot, TranscriptBot>();

    // ─── App ──────────────────────────────────────────────────────────────
    var app = builder.Build();
    app.UseRouting();
    app.MapControllers();

    Log.Information("TeamsEchoBot running.");
    Log.Information("  Calling webhook: {CallbackUri}", botConfig.CallbackUri);
    Log.Information("  Messaging endpoint: https://{Dns}/api/messages", botConfig.ServiceDnsName);
    Log.Information("Send 'join <meeting-url>' to the bot in Teams to start transcribing.");

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "TeamsEchoBot terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
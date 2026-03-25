using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Serilog;
using TeamsEchoBot.Models;
using TeamsEchoBot.Services;

AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
{
    Log.Fatal(e.ExceptionObject as Exception, "UNHANDLED EXCEPTION: {Msg}",
        e.ExceptionObject?.ToString() ?? "unknown");
    Log.CloseAndFlush();
};

// ─── Bootstrap Serilog early so startup errors are captured ───────────────────
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File("logs/teamsechobot-.txt", rollingInterval: RollingInterval.Day,
        outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

try
{
    Log.Information("Starting TeamsEchoBot...");

    var builder = WebApplication.CreateBuilder(args);

    // ─── Serilog ──────────────────────────────────────────────────────────────
    builder.Host.UseSerilog();
    builder.Services.Configure<Microsoft.Extensions.Hosting.HostOptions>(options =>
    {
        // Give the media platform 15 seconds to drain on Ctrl+C
        // Default is 5 seconds which is too short for active calls
        options.ShutdownTimeout = TimeSpan.FromSeconds(15);
    });

    // ─── Configuration ────────────────────────────────────────────────────────
    var botConfig = builder.Configuration.GetSection("Bot").Get<BotConfiguration>()
        ?? throw new InvalidOperationException("Missing 'Bot' configuration section in appsettings.json");

    var speechConfig = builder.Configuration.GetSection("AzureSpeech").Get<SpeechConfiguration>()
        ?? throw new InvalidOperationException("Missing 'AzureSpeech' configuration section in appsettings.json");

    // ─── Kestrel — bind HTTPS on 443 using the certificate from the Windows cert store ──
    // CRITICAL: We load the cert from the LOCAL MACHINE store by thumbprint.
    // This must be the same cert whose thumbprint is bound to 0.0.0.0:443 via netsh.
    // Teams validates TLS strictly — cert CN/SAN must match ServiceDnsName.
    builder.WebHost.ConfigureKestrel(options =>
    {
        // HTTP on 80 is NOT needed — Teams only calls HTTPS
        options.ListenAnyIP(443, listenOptions =>
        {
            var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadOnly);

            var certs = store.Certificates.Find(
                X509FindType.FindByThumbprint,
                botConfig.CertThumbprint,
                validOnly: false); // validOnly: false allows self-signed during dev

            if (certs.Count == 0)
                throw new InvalidOperationException(
                    $"Certificate with thumbprint '{botConfig.CertThumbprint}' not found in LocalMachine\\My store. " +
                    "Run: Get-ChildItem Cert:\\LocalMachine\\My  to list available thumbprints.");

            listenOptions.UseHttps(new HttpsConnectionAdapterOptions
            {
                ServerCertificate = certs[0]
            });

            Log.Information("Kestrel: bound HTTPS on port 443 using cert subject: {Subject}", certs[0].Subject);
            store.Close();
        });
    });

    // ─── Services ─────────────────────────────────────────────────────────────
    builder.Services.AddControllers()
        .AddNewtonsoftJson(); // Graph SDK requires Newtonsoft

    builder.Services.AddSingleton(botConfig);
    builder.Services.AddSingleton(speechConfig);

    // BotService is the core singleton that owns the Graph Communications Client
    // and all active call handlers. Must be singleton — the media platform is
    // not designed to be reconstructed per-request.
    builder.Services.AddSingleton<BotService>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<BotService>());

    // ─── App pipeline ─────────────────────────────────────────────────────────
    var app = builder.Build();

    // Force BotService to initialize at startup (not lazily on first request)
    // so that any configuration errors surface immediately in the logs.
    // var botService = app.Services.GetRequiredService<BotService>();
    // botService.Initialize();

    app.UseRouting();
    app.MapControllers();

    Log.Information("TeamsEchoBot running. Webhook: {CallbackUri}", botConfig.CallbackUri);
    Log.Information("Waiting for Postman POST to /api/joinCall to start a session...");

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "TeamsEchoBot terminated unexpectedly during startup");
}
finally
{
    Log.CloseAndFlush();
}

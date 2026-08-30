using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog;
using SslExpireNotify.Worker;
using SslExpireNotify.Worker.Options;

// A Windows Service starts with %SystemRoot%\System32 as its current directory. Serilog's rolling
// file sink resolves a relative path against that directory, not against the executable's own folder,
// so without this every log line ends up under System32\logs instead of the install folder.
Directory.SetCurrentDirectory(AppContext.BaseDirectory);

// Subjects and templates are Thai; without this the console renders them as mojibake when the service
// is run interactively. Fails silently when there is no console (i.e. when running as a service).
try
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;
}
catch (IOException)
{
}

// A bootstrap logger so configuration or validation failures during start-up are still visible.
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    // A Windows service starts with %SystemRoot%\System32 as its working directory, so the content root
    // is pinned to the install folder; otherwise appsettings.json and Templates/ would not be found.
    var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
    {
        Args = args,
        ContentRootPath = AppContext.BaseDirectory
    });

    builder.Services.AddWindowsService(options => options.ServiceName = "SslExpireNotify");

    builder.Services.AddSerilog((services, configuration) => configuration
        .ReadFrom.Configuration(builder.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        // Lines logged outside a job run still need something in the RunId slot.
        .Enrich.WithProperty("RunId", "-"));

    builder.Services
        .AddSslExpireNotifyOptions(builder.Configuration)
        .AddSslExpireNotifyServices()
        .AddSslExpireNotifyScheduler(builder.Configuration);

    builder.Services.AddHostedService<StartupBanner>();

    var host = builder.Build();
    await host.RunAsync();

    return 0;
}
catch (Exception ex)
{
    Log.Fatal(ex, "SslExpireNotify could not start");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}

/// <summary>Logs the effective, risky parts of the configuration once at start-up.</summary>
internal sealed class StartupBanner : IHostedService
{
    private readonly IOptions<MailApiOptions> _mailApi;
    private readonly IOptions<JobOptions> _job;
    private readonly IOptions<RecipientsOptions> _recipients;
    private readonly IOptions<SmtpFallbackOptions> _smtp;
    private readonly ILogger<StartupBanner> _logger;

    public StartupBanner(
        IOptions<MailApiOptions> mailApi,
        IOptions<JobOptions> job,
        IOptions<RecipientsOptions> recipients,
        IOptions<SmtpFallbackOptions> smtp,
        ILogger<StartupBanner> logger)
    {
        _mailApi = mailApi;
        _job = job;
        _recipients = recipients;
        _smtp = smtp;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var job = _job.Value;

        _logger.LogInformation(
            "SslExpireNotify starting. Cron '{Cron}' in {TimeZone}, misfire {Misfire}, RunOnStartup={RunOnStartup}, DryRun={DryRun}, levels [{Levels}]",
            job.CronSchedule, job.TimeZoneId, job.MisfirePolicy, job.RunOnStartup, job.DryRun,
            string.Join(", ", job.AlertLevels.Select(l => $"{l.Level}<={l.Days}d/{l.RepeatEveryDays}d")));

        _logger.LogInformation(
            "Mail channel {Channel}, SMTP fallback {FallbackState}, SendToCustomer={SendToCustomer}",
            _mailApi.Value.PreferredChannel,
            _smtp.Value.Enabled ? $"enabled ({_smtp.Value.Host}:{_smtp.Value.Port})" : "disabled",
            _recipients.Value.SendToCustomer);

        if (_mailApi.Value.AllowInvalidCertificate)
        {
            _logger.LogWarning(
                "MailApi:AllowInvalidCertificate is TRUE - TLS certificate validation for {Url} is disabled. Turn this off in production.",
                _mailApi.Value.Url);
        }

        if (job.DryRun)
        {
            _logger.LogWarning("Job:DryRun is TRUE - no alerts will be written and no email will be sent.");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

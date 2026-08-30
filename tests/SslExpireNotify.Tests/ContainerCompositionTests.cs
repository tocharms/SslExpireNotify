using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SslExpireNotify.Worker;
using SslExpireNotify.Worker.Jobs;
using SslExpireNotify.Worker.Options;
using SslExpireNotify.Worker.Services;
using Xunit;

namespace SslExpireNotify.Tests;

/// <summary>
/// Builds the real container from the shipped appsettings.json. A container that cannot be built only
/// fails when Quartz first fires the job - at 00:30, in production - so it is checked here instead.
/// </summary>
public class ContainerCompositionTests
{
    private static ServiceProvider BuildProvider(Dictionary<string, string?>? overrides = null)
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .AddInMemoryCollection(overrides ?? [])
            .Build();

        var services = new ServiceCollection();
        // The generic host registers these two for free; here they have to be supplied by hand.
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddLogging();
        services
            .AddSslExpireNotifyOptions(configuration)
            .AddSslExpireNotifyServices();

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
    }

    [Fact]
    public void The_job_and_everything_it_needs_can_be_resolved()
    {
        using var provider = BuildProvider();

        Assert.NotNull(provider.GetRequiredService<SslExpireCheckJob>());
    }

    [Fact]
    public void Every_registered_service_can_be_resolved()
    {
        using var provider = BuildProvider();

        Assert.NotNull(provider.GetRequiredService<ICertificateAlertService>());
        Assert.NotNull(provider.GetRequiredService<ICompositeEmailSender>());
        Assert.NotNull(provider.GetRequiredService<IMailApiEmailSender>());
        Assert.NotNull(provider.GetRequiredService<ISmtpEmailSender>());
        Assert.NotNull(provider.GetRequiredService<IEmailComposer>());
        Assert.NotNull(provider.GetRequiredService<IJobLockService>());
        Assert.NotNull(provider.GetRequiredService<IJobClock>());
    }

    [Fact]
    public void The_shipped_appsettings_produces_the_documented_alert_levels()
    {
        using var provider = BuildProvider();

        var resolver = provider.GetRequiredService<IAlertLevelResolver>();

        Assert.Equal("NOTICE", resolver.Resolve(30)?.Level);
        Assert.Equal("WARNING", resolver.Resolve(15)?.Level);
        Assert.Equal("URGENT", resolver.Resolve(7)?.Level);
        Assert.Equal("EXPIRED", resolver.Resolve(0)?.Level);
        Assert.Null(resolver.Resolve(31));
    }

    [Fact]
    public void The_shipped_appsettings_passes_every_validator()
    {
        using var provider = BuildProvider();

        Assert.NotNull(provider.GetRequiredService<IOptions<JobOptions>>().Value.CronSchedule);
        Assert.NotEmpty(provider.GetRequiredService<IOptions<MailApiOptions>>().Value.Url);
        Assert.NotEmpty(provider.GetRequiredService<IOptions<EmailTemplateOptions>>().Value.TemplateFiles);
        Assert.NotEmpty(provider.GetRequiredService<IOptions<AckOptions>>().Value.AckBaseUrl);
    }

    [Fact]
    public void Every_template_path_in_the_shipped_appsettings_points_at_a_real_file()
    {
        using var provider = BuildProvider();

        var options = provider.GetRequiredService<IOptions<EmailTemplateOptions>>().Value;
        var templates = provider.GetRequiredService<IEmailTemplateService>();

        foreach (var path in options.TemplateFiles.Values
                     .Concat(options.CustomerTemplateFiles.Values)
                     .Append(options.ContractTemplateFile)
                     .Distinct())
        {
            Assert.False(string.IsNullOrWhiteSpace(templates.Load(path)), $"Template '{path}' is empty.");
        }
    }

    [Fact]
    public void A_broken_alert_level_table_stops_the_container_from_starting()
    {
        // Severity 4 with the largest Days: EXPIRED would swallow every other level.
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["Job:AlertLevels:3:Days"] = "999"
        });

        var exception = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<JobOptions>>().Value);

        Assert.Contains(exception.Failures, f => f.Contains("inconsistent"));
    }

    [Fact]
    public void An_empty_ack_base_url_stops_the_container_from_starting()
    {
        using var provider = BuildProvider(new Dictionary<string, string?> { ["AckBaseUrl"] = "" });

        Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<AckOptions>>().Value);
    }
}

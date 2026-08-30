using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Quartz;
using SslExpireNotify.Worker.Jobs;
using SslExpireNotify.Worker.Options;
using SslExpireNotify.Worker.Repositories;
using SslExpireNotify.Worker.Services;

namespace SslExpireNotify.Worker;

/// <summary>
/// Composition of the service. Kept out of Program.cs so the container can be built and verified by tests.
/// </summary>
public static class ServiceRegistration
{
    public static IServiceCollection AddSslExpireNotifyOptions(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<JobOptions>()
            .Bind(configuration.GetSection(JobOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<JobOptions>, JobOptionsValidator>();

        services.AddOptions<MailApiOptions>()
            .Bind(configuration.GetSection(MailApiOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<MailApiOptions>, MailApiOptionsValidator>();

        services.AddOptions<RecipientsOptions>()
            .Bind(configuration.GetSection(RecipientsOptions.SectionName));

        services.AddOptions<SmtpFallbackOptions>()
            .Bind(configuration.GetSection(SmtpFallbackOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<SmtpFallbackOptions>, SmtpFallbackOptionsValidator>();

        services.AddOptions<EmailTemplateOptions>()
            .Bind(configuration.GetSection(EmailTemplateOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<EmailTemplateOptions>, EmailTemplateOptionsValidator>();

        // AckBaseUrl lives at the root of appsettings.json.
        services.AddOptions<AckOptions>()
            .Configure<IConfiguration>((options, config) => options.AckBaseUrl = config["AckBaseUrl"] ?? string.Empty)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<AckOptions>, AckOptionsValidator>();

        return services;
    }

    public static IServiceCollection AddSslExpireNotifyServices(this IServiceCollection services)
    {
        services.AddSingleton<IDbConnectionFactory, SqlConnectionFactory>();
        services.AddSingleton<IDbResilience, DbResilience>();
        services.AddSingleton<ISslCertificateRepository, SslCertificateRepository>();
        services.AddSingleton<IAlertRepository, AlertRepository>();
        services.AddSingleton<IJobRunHistoryRepository, JobRunHistoryRepository>();

        services.AddSingleton<IJobClock, JobClock>();
        services.AddSingleton<IAlertLevelResolver>(serviceProvider =>
            new AlertLevelResolver(serviceProvider.GetRequiredService<IOptions<JobOptions>>().Value.AlertLevels));
        services.AddSingleton<IEmailTemplateService, EmailTemplateService>();
        services.AddSingleton<IEmailComposer, EmailComposer>();
        services.AddSingleton<IJobLockService, JobLockService>();
        services.AddSingleton<ICertificateAlertService, CertificateAlertService>();

        services.AddHttpClient(MailApiEmailSender.HttpClientName)
            .ConfigureHttpClient((serviceProvider, client) =>
            {
                var options = serviceProvider.GetRequiredService<IOptions<MailApiOptions>>().Value;
                client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
            })
            .ConfigurePrimaryHttpMessageHandler(serviceProvider =>
            {
                var options = serviceProvider.GetRequiredService<IOptions<MailApiOptions>>().Value;
                var handler = new HttpClientHandler();

                if (options.AllowInvalidCertificate)
                {
                    handler.ServerCertificateCustomValidationCallback =
                        HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
                }

                return handler;
            });

        services.AddSingleton<IMailApiEmailSender, MailApiEmailSender>();
        services.AddSingleton<ISmtpEmailSender, SmtpEmailSender>();
        services.AddSingleton<ICompositeEmailSender, CompositeEmailSender>();

        // Quartz resolves the job from the container, so it has to be registered as well.
        services.AddTransient<SslExpireCheckJob>();

        return services;
    }

    public static IServiceCollection AddSslExpireNotifyScheduler(this IServiceCollection services, IConfiguration configuration)
    {
        var jobOptions = configuration.GetSection(JobOptions.SectionName).Get<JobOptions>() ?? new JobOptions();

        // Bound explicitly to the configured zone: a production box left on UTC must not silently shift the run.
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(jobOptions.TimeZoneId);
        var fireOnceNow = string.Equals(jobOptions.MisfirePolicy, "FireOnceNow", StringComparison.OrdinalIgnoreCase);

        services.AddQuartz(quartz =>
        {
            quartz.AddJob<SslExpireCheckJob>(job => job.WithIdentity(SslExpireCheckJob.Key));

            quartz.AddTrigger(trigger => trigger
                .ForJob(SslExpireCheckJob.Key)
                .WithIdentity("ssl-expire-cron", SslExpireCheckJob.Key.Group)
                .WithCronSchedule(jobOptions.CronSchedule, cron =>
                {
                    cron.InTimeZone(timeZone);

                    if (fireOnceNow)
                    {
                        cron.WithMisfireHandlingInstructionFireAndProceed();
                    }
                    else
                    {
                        cron.WithMisfireHandlingInstructionDoNothing();
                    }
                }));

            if (jobOptions.RunOnStartup)
            {
                quartz.AddTrigger(trigger => trigger
                    .ForJob(SslExpireCheckJob.Key)
                    .WithIdentity("ssl-expire-startup", SslExpireCheckJob.Key.Group)
                    .StartNow());
            }
        });

        services.AddQuartzHostedService(options =>
        {
            options.WaitForJobsToComplete = true;
            options.AwaitApplicationStarted = true;
        });

        return services;
    }
}

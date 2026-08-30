using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;

namespace SslExpireNotify.Worker.Repositories;

public interface IDbResilience
{
    Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken);

    Task ExecuteAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken);
}

/// <summary>
/// Retries transient database faults (connection blips, deadlock victims, lock timeouts). Permanent errors
/// such as a syntax error or a constraint violation are not retried.
/// </summary>
public sealed class DbResilience : IDbResilience
{
    private static readonly int[] TransientErrorNumbers =
    [
        -2,     // client side timeout
        20,     // instance not currently able to accept a connection
        64,     // connection was successfully established but then failed
        233,    // no process on the other end of the pipe
        1205,   // deadlock victim
        1222,   // lock request timeout
        4060,   // cannot open database
        10053, 10054, 10060, // network transport level errors
        40197, 40501, 40613, 49918, 49919, 49920 // Azure SQL throttling / unavailable
    ];

    private readonly ResiliencePipeline _pipeline;

    public DbResilience(ILogger<DbResilience> logger)
    {
        _pipeline = new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder().Handle<SqlException>(IsTransient),
                MaxRetryAttempts = 3,
                BackoffType = DelayBackoffType.Constant,
                DelayGenerator = args => ValueTask.FromResult<TimeSpan?>(args.AttemptNumber switch
                {
                    0 => TimeSpan.FromMilliseconds(200),
                    1 => TimeSpan.FromMilliseconds(500),
                    _ => TimeSpan.FromSeconds(1)
                }),
                OnRetry = args =>
                {
                    logger.LogWarning(
                        args.Outcome.Exception,
                        "Transient database fault, retrying (attempt {Attempt}) in {Delay}",
                        args.AttemptNumber + 1,
                        args.RetryDelay);
                    return ValueTask.CompletedTask;
                }
            })
            .Build();
    }

    public static bool IsTransient(SqlException exception) =>
        exception.Errors.Cast<SqlError>().Any(e => TransientErrorNumbers.Contains(e.Number));

    public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken) =>
        await _pipeline.ExecuteAsync(async ct => await operation(ct).ConfigureAwait(false), cancellationToken)
            .ConfigureAwait(false);

    public async Task ExecuteAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken) =>
        await _pipeline.ExecuteAsync(async ct => await operation(ct).ConfigureAwait(false), cancellationToken)
            .ConfigureAwait(false);
}

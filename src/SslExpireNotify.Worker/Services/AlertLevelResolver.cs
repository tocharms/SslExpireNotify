using SslExpireNotify.Worker.Options;

namespace SslExpireNotify.Worker.Services;

public interface IAlertLevelResolver
{
    /// <summary>Most severe level whose Days threshold covers <paramref name="daysRemaining"/>, or null when none applies.</summary>
    AlertLevelOptions? Resolve(int daysRemaining);

    /// <summary>Level definition by name, or null when the name is not configured (any more).</summary>
    AlertLevelOptions? GetByName(string? level);

    /// <summary>Severity of a level name; unknown names rank below every configured level.</summary>
    int SeverityOf(string? level);

    IReadOnlyList<AlertLevelOptions> Levels { get; }
}

public sealed class AlertLevelResolver : IAlertLevelResolver
{
    private readonly List<AlertLevelOptions> _levels;

    /// <summary>
    /// Single constructor on purpose: an overload taking IOptions&lt;JobOptions&gt; makes the type ambiguous
    /// for the DI container. Program.cs builds it from configuration with an explicit factory.
    /// </summary>
    public AlertLevelResolver(IEnumerable<AlertLevelOptions> levels)
    {
        // Most severe first so Resolve can take the first match.
        _levels = levels.OrderByDescending(l => l.Severity).ToList();
    }

    public IReadOnlyList<AlertLevelOptions> Levels => _levels;

    public AlertLevelOptions? Resolve(int daysRemaining) =>
        _levels.FirstOrDefault(l => daysRemaining <= l.Days);

    public AlertLevelOptions? GetByName(string? level) =>
        string.IsNullOrWhiteSpace(level)
            ? null
            : _levels.FirstOrDefault(l => string.Equals(l.Level, level, StringComparison.OrdinalIgnoreCase));

    public int SeverityOf(string? level) => GetByName(level)?.Severity ?? int.MinValue;
}

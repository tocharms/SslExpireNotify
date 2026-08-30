using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace SslExpireNotify.Worker.Services;

public interface IEmailTemplateService
{
    /// <summary>Reads a template file, caching it for the rest of the run.</summary>
    string Load(string relativePath);

    /// <summary>Replaces placeholders. Values without a matching placeholder are ignored silently.</summary>
    string Render(string template, IReadOnlyDictionary<string, string> values);

    /// <summary>Builds the &lt;tr&gt; block for the grouped EXPIRED mail from the row template inside the file.</summary>
    string BuildCertRows(string template, IEnumerable<CertRow> rows);

    /// <summary>Removes the commented out row template so it does not travel with the sent mail.</summary>
    string StripRowTemplate(string template);

    /// <summary>Drops every cached file; called once at the start of each job run.</summary>
    void ResetCache();
}

/// <summary>One line of the grouped EXPIRED table.</summary>
public sealed record CertRow(string CustomerName, string Domain, string ExpiredDate);

public sealed class EmailTemplateService : IEmailTemplateService
{
    private static readonly Regex RowTemplateComment = new(
        @"<!--\s*ROW TEMPLATE.*?-->",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex RowTemplateRow = new(
        @"<!--\s*ROW TEMPLATE.*?(?<row><tr\b.*?</tr>).*?-->",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private readonly ConcurrentDictionary<string, string> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<EmailTemplateService> _logger;
    private readonly string _baseDirectory;

    public EmailTemplateService(ILogger<EmailTemplateService> logger)
        : this(logger, AppContext.BaseDirectory)
    {
    }

    public EmailTemplateService(ILogger<EmailTemplateService> logger, string baseDirectory)
    {
        _logger = logger;
        _baseDirectory = baseDirectory;
    }

    public void ResetCache() => _cache.Clear();

    public string Load(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new InvalidOperationException("Template path is not configured.");
        }

        return _cache.GetOrAdd(relativePath, path =>
        {
            var fullPath = Path.IsPathRooted(path)
                ? path
                : Path.Combine(_baseDirectory, path.Replace('/', Path.DirectorySeparatorChar));

            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException($"Email template '{path}' was not found at '{fullPath}'.", fullPath);
            }

            _logger.LogDebug("Loaded email template {TemplatePath}", fullPath);
            return File.ReadAllText(fullPath, Encoding.UTF8);
        });
    }

    public string Render(string template, IReadOnlyDictionary<string, string> values)
    {
        if (string.IsNullOrEmpty(template))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(template);
        foreach (var (key, value) in values)
        {
            builder.Replace("{" + key + "}", value ?? string.Empty);
        }

        return builder.ToString();
    }

    public string BuildCertRows(string template, IEnumerable<CertRow> rows)
    {
        var match = RowTemplateRow.Match(template);
        if (!match.Success)
        {
            throw new InvalidOperationException(
                "The grouped EXPIRED template does not contain a '<!-- ROW TEMPLATE ... <tr>...</tr> ... -->' block to build {certRows} from.");
        }

        var rowTemplate = match.Groups["row"].Value;
        var builder = new StringBuilder();

        foreach (var row in rows)
        {
            builder.AppendLine(Render(rowTemplate, new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["customerName"] = HtmlText.Encode(row.CustomerName),
                ["domain"] = HtmlText.Encode(row.Domain),
                ["expiredDate"] = HtmlText.Encode(row.ExpiredDate)
            }));
        }

        return builder.ToString().TrimEnd();
    }

    public string StripRowTemplate(string template) =>
        RowTemplateComment.Replace(template, string.Empty);
}

public static class HtmlText
{
    /// <summary>Database text goes into an HTML mail body, so it is always encoded first.</summary>
    public static string Encode(string? value) =>
        System.Net.WebUtility.HtmlEncode(value ?? string.Empty);
}

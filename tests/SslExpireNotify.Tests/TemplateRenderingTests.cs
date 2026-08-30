using Microsoft.Extensions.Logging.Abstractions;
using SslExpireNotify.Worker.Services;
using Xunit;

namespace SslExpireNotify.Tests;

public class TemplateRenderingTests : IDisposable
{
    private readonly string _directory;
    private readonly EmailTemplateService _service;

    public TemplateRenderingTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "ssl-expire-notify-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _service = new EmailTemplateService(NullLogger<EmailTemplateService>.Instance, _directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }

    private string WriteTemplate(string name, string content)
    {
        File.WriteAllText(Path.Combine(_directory, name), content);
        return name;
    }

    [Fact]
    public void Values_without_a_matching_placeholder_are_ignored()
    {
        var rendered = _service.Render("<p>{domain}</p>", new Dictionary<string, string>
        {
            ["domain"] = "www.example.co.th",
            ["daysOverdue"] = "12",          // not present in this template
            ["certStatusLineThai"] = "..."   // not present either
        });

        Assert.Equal("<p>www.example.co.th</p>", rendered);
    }

    [Fact]
    public void Every_occurrence_of_a_placeholder_is_replaced()
    {
        var rendered = _service.Render("{domain} / {domain}", new Dictionary<string, string> { ["domain"] = "a.co.th" });

        Assert.Equal("a.co.th / a.co.th", rendered);
    }

    [Fact]
    public void Templates_are_read_from_disk_only_once_per_run()
    {
        var name = WriteTemplate("cached.html", "<p>first</p>");

        Assert.Equal("<p>first</p>", _service.Load(name));

        File.WriteAllText(Path.Combine(_directory, name), "<p>second</p>");
        Assert.Equal("<p>first</p>", _service.Load(name));   // still the cached copy

        _service.ResetCache();
        Assert.Equal("<p>second</p>", _service.Load(name));  // a new run picks the file up again
    }

    [Fact]
    public void A_missing_template_file_is_reported_with_its_path()
    {
        var exception = Assert.Throws<FileNotFoundException>(() => _service.Load("does-not-exist.html"));

        Assert.Contains("does-not-exist.html", exception.Message);
    }

    [Fact]
    public void Cert_rows_are_built_from_the_row_template_inside_the_file()
    {
        var name = WriteTemplate("list.html", """
            <table>
            {certRows}
            <!-- ROW TEMPLATE:
            <tr><td>{customerName}</td><td>{domain}</td><td>{expiredDate}</td></tr>
            -->
            </table>
            """);

        var template = _service.Load(name);

        var rows = _service.BuildCertRows(template,
        [
            new CertRow("Alpha Trading", "a.co.th", "1 Mar 2026"),
            new CertRow("Beta Logistics", "b.co.th", "8 Mar 2026")
        ]);

        Assert.Contains("<tr><td>Alpha Trading</td><td>a.co.th</td><td>1 Mar 2026</td></tr>", rows);
        Assert.Contains("<tr><td>Beta Logistics</td><td>b.co.th</td><td>8 Mar 2026</td></tr>", rows);
        Assert.Equal(2, rows.Split("<tr>").Length - 1);
    }

    [Fact]
    public void The_commented_out_row_template_never_reaches_the_recipient()
    {
        var name = WriteTemplate("list.html", """
            <table>{certRows}
            <!-- ROW TEMPLATE:
            <tr><td>{customerName}</td></tr>
            -->
            </table>
            """);

        var stripped = _service.StripRowTemplate(_service.Load(name));

        Assert.DoesNotContain("ROW TEMPLATE", stripped);
        Assert.DoesNotContain("{customerName}", stripped);
    }

    [Fact]
    public void Database_text_is_html_encoded_before_it_lands_in_a_row()
    {
        var name = WriteTemplate("list.html", """
            {certRows}
            <!-- ROW TEMPLATE:
            <tr><td>{customerName}</td><td>{domain}</td><td>{expiredDate}</td></tr>
            -->
            """);

        var rows = _service.BuildCertRows(_service.Load(name),
            [new CertRow("A & B <script>", "x.co.th", "1 Mar 2026")]);

        Assert.Contains("A &amp; B &lt;script&gt;", rows);
        Assert.DoesNotContain("<script>", rows);
    }

    [Fact]
    public void A_list_template_without_a_row_template_is_reported_clearly()
    {
        var name = WriteTemplate("broken.html", "<table>{certRows}</table>");

        var exception = Assert.Throws<InvalidOperationException>(
            () => _service.BuildCertRows(_service.Load(name), [new CertRow("A", "b", "c")]));

        Assert.Contains("ROW TEMPLATE", exception.Message);
    }

    [Fact]
    public void The_shipped_expired_digest_template_can_produce_rows()
    {
        // Guards the real file: the row template comment has to stay parseable.
        var service = new EmailTemplateService(NullLogger<EmailTemplateService>.Instance, AppContext.BaseDirectory);
        var template = service.Load("Templates/ssl-expiry-notice-expired.html");

        var rows = service.BuildCertRows(template, [new CertRow("Alpha Trading", "a.co.th", "1 Mar 2026")]);

        Assert.Contains("Alpha Trading", rows);
        Assert.Contains("a.co.th", rows);
        Assert.Contains("1 Mar 2026", rows);
        Assert.StartsWith("<tr", rows.TrimStart());
    }
}

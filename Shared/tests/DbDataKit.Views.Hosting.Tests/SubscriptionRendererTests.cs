using System.Text;
using System.Text.Json.Nodes;
using DbDataKit.Delivery;
using DbDataKit.State;
using DbDataKit.State.Tests;
using DbDataKit.Views.Export;

namespace DbDataKit.Views.Hosting.Tests;

/// <summary>An exporter that draws nothing: the PDF is the print page's own HTML, so a test can read which layout and which rows the module composed for whom.</summary>
internal sealed class PageAsPdfExporter : IReportExporter
{
    public ExportAvailability Availability { get; } = new(true, "fake", "fake", "system");

    public BundledFonts? Fonts => null;

    public Task<ExportAvailability> CheckAsync(CancellationToken cancellationToken = default) => Task.FromResult(Availability);

    public Task<ExportResult> ExportPdfAsync(PrintJob job, CancellationToken cancellationToken = default) => Task.FromResult(new ExportResult(Encoding.UTF8.GetBytes(job.Html), 1, "fake", [], []));

    public Task<SvgResult> ExportSvgAsync(PrintJob job, string chartId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    /// <summary>When true, a picture is "made": the PNG signature then the page's HTML, so a test can read what was composed for the picture as for the PDF.</summary>
    public bool MakesPictures { get; init; }

    public Task<PngResult> ExportPngAsync(PrintJob job, int maxHeight = 4000, CancellationToken cancellationToken = default) =>
        MakesPictures ? Task.FromResult(new PngResult([137, 80, 78, 71, .. Encoding.UTF8.GetBytes(job.Html)], 794, 1123, "fake", []))
                      : throw new ExportException("unavailable", "this exporter makes no pictures of a page.");

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class TestPrincipals(TestCallers callers) : ISubscriberPrincipals
{
    public Task<ViewsPrincipal?> ForAsync(string subscriber, CancellationToken cancellationToken) => Task.FromResult(subscriber switch
    {
        "wendy" => callers.Tokens["west"],
        "erin" => callers.Tokens["east"],
        "alice" => callers.Tokens["all"],
        "scoped" => callers.Tokens["app-none"],       // a principal narrowed to no model at all: the document is refused for it
        _ => null,                                      // gone
    });
}

/// <summary>
/// Plan 32 DBR18 against the real module: a subscription is exported as the subscriber (their rows, their audit row), with the layout the subscription names or the module's own rule, a failed widget is said, a subscriber the module refuses loses access, and a host
/// without an exporter says which setting is missing. The data exports (CSV, xlsx) need no browser; the PDF is the print page itself (<see cref="PageAsPdfExporter"/>), so what is asserted is what the module composed.
/// </summary>
public sealed class SubscriptionRendererTests : IDisposable
{
    private readonly StateEngines engines = new();
    private readonly List<string> folders = [];

    public void Dispose()
    {
        engines.Dispose();
        foreach (var f in folders) try { Directory.Delete(f, true); } catch (IOException) { }
    }

    private string PageAssets()
    {
        var folder = Path.Combine(Path.GetTempPath(), "print-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        folders.Add(folder);
        File.WriteAllText(Path.Combine(folder, "print.js"), "window.__reportReady = true;");
        File.WriteAllText(Path.Combine(folder, "print.css"), "body { }");
        return folder;
    }

    private async Task<(ViewsHarness H, SubscriptionRenderer Renderer)> StartAsync(bool exporter = true, bool pictures = false)
    {
        var h = await ViewsHarness.StartAsync(engines, StateEngineIds.Sqlite, exporter: exporter ? new PageAsPdfExporter { MakesPictures = pictures } : null, pageAssets: exporter ? PageAssets() : null);
        h.PublishDataBoard();
        // the table about the category alone cannot be answered: the host refuses it, so the widget is an error entry and the others render
        h.Host.Before = (_, request) => request.Text.Contains("category", StringComparison.Ordinal) && !request.Text.Contains("region", StringComparison.Ordinal)
            ? throw new DbDataKit.Serving.ServingDeniedException("Source 'warehouse' is not available.") : Task.CompletedTask;
        return (h, new SubscriptionRenderer(h.Module.Api!, h.Published, new TestPrincipals(h.Callers), new SubscriptionRendererOptions("https://console.example/views/{kind}/{name}")));
    }

    private static SubscriptionRender Ask(string subscriber, string kind = "dashboard", string view = "rows", string format = "pdf", string? layout = null, string? widget = null, bool preview = false) =>
        new(Guid.NewGuid(), subscriber, kind, view, new Dictionary<string, JsonNode?>(), layout, format, widget, preview);

    private static List<string> Rows(RenderResult.Rendered r) => [.. Encoding.UTF8.GetString(r.Content).Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(l => l.Split(',')[0])];

    [Fact]
    public async Task A_subscription_that_asks_for_a_preview_gets_a_picture_drawn_as_the_subscriber_and_one_that_does_not_gets_none()
    {
        var (h, renderer) = await StartAsync(pictures: true);
        await using var _ = h;
        var plain = Assert.IsType<RenderResult.Rendered>(await renderer.RenderAsync(Ask("wendy"), default));
        Assert.Null(plain.Preview);
        var west = Assert.IsType<RenderResult.Rendered>(await renderer.RenderAsync(Ask("wendy", preview: true), default));
        var east = Assert.IsType<RenderResult.Rendered>(await renderer.RenderAsync(Ask("erin", preview: true), default));
        Assert.Equal(new byte[] { 137, 80, 78, 71 }, west.Preview![..4]);
        Assert.NotEqual(west.Preview, east.Preview);                                          // each picture holds that person's rows
        Assert.Contains("West", Encoding.UTF8.GetString(west.Preview!));
        Assert.DoesNotContain("East", Encoding.UTF8.GetString(west.Preview!));
        Assert.DoesNotContain(west.Notes, n => n.Contains("preview", StringComparison.OrdinalIgnoreCase));      // the denied widget is noted, the picture is not
        // the picture is audited as the PDF is: as the subscriber, with its format
        Assert.Contains(h.Audit.Read(new AuditQuery(Verb: "view.export", Limit: 100)), r => r.Entry.Detail?.Contains("\"png\"", StringComparison.Ordinal) == true);
        // a picture is never made for rows: csv and xlsx have none
        var csv = Assert.IsType<RenderResult.Rendered>(await renderer.RenderAsync(Ask("wendy", format: "csv", widget: "by_region", preview: true), default));
        Assert.Null(csv.Preview);
    }

    [Fact]
    public async Task A_preview_that_cannot_be_made_does_not_stop_the_pdf_and_the_message_says_so()
    {
        var (h, renderer) = await StartAsync();       // this exporter makes no pictures
        await using var _ = h;
        var result = Assert.IsType<RenderResult.Rendered>(await renderer.RenderAsync(Ask("wendy", preview: true), default));
        Assert.Null(result.Preview);
        Assert.NotEmpty(result.Content);
        Assert.Contains(result.Notes, n => n.Contains("preview picture could not be made", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Two_subscribers_of_one_dashboard_with_different_rules_get_different_attachments_and_each_export_is_audited_as_that_person()
    {
        var (h, renderer) = await StartAsync();
        await using var _ = h;
        var west = Assert.IsType<RenderResult.Rendered>(await renderer.RenderAsync(Ask("wendy", format: "csv", widget: "by_region"), default));
        var east = Assert.IsType<RenderResult.Rendered>(await renderer.RenderAsync(Ask("erin", format: "csv", widget: "by_region"), default));
        var all = Assert.IsType<RenderResult.Rendered>(await renderer.RenderAsync(Ask("alice", format: "csv", widget: "by_region"), default));

        Assert.Equal(["West"], Rows(west));
        Assert.Equal(["East"], Rows(east));
        Assert.Equal(["East", "West"], Rows(all).Order(StringComparer.Ordinal));
        Assert.NotEqual(west.Content, east.Content);
        Assert.Equal(("rows-by_region.csv", "text/csv; charset=utf-8", "Rows"), (west.FileName, west.MediaType, west.Title));
        Assert.Equal("https://console.example/views/dashboard/rows", west.Link);
        Assert.Contains("The rows of Rows are attached as CSV", west.Summary);

        var audited = h.Audit.Read(new AuditQuery(Verb: "view.export", Limit: 10)).Select(r => r.Entry.ActorId).Order().ToList();
        Assert.Equal(["u-all", "u-east", "u-west"], audited);                             // the rows were read as each subscriber, never as the runner
    }

    [Fact]
    public async Task A_workbook_is_exported_as_the_subscriber_too_and_needs_no_browser()
    {
        var (h, renderer) = await StartAsync(exporter: false);
        await using var _ = h;
        var xlsx = Assert.IsType<RenderResult.Rendered>(await renderer.RenderAsync(Ask("wendy", format: "xlsx"), default));
        Assert.Equal("rows-desk.xlsx", xlsx.FileName);
        Assert.Equal([0x50, 0x4B], xlsx.Content.Take(2).Select(b => (int)b));         // a zip
    }

    [Fact]
    public async Task A_subscription_naming_a_layout_and_one_naming_none_get_the_layouts_the_module_gives_and_a_failed_widget_is_said()
    {
        var (h, renderer) = await StartAsync();
        await using var _ = h;
        var named = Assert.IsType<RenderResult.Rendered>(await renderer.RenderAsync(Ask("wendy", layout: "kpi"), default));
        var none = Assert.IsType<RenderResult.Rendered>(await renderer.RenderAsync(Ask("wendy"), default));
        Assert.Equal("rows-kpi.pdf", named.FileName);
        Assert.Equal("rows-desk.pdf", none.FileName);                                  // none: the module's rule for the print medium, which is the default layout
        var kpi = JsonNode.Parse(PrintJson(named))!;
        var desk = JsonNode.Parse(PrintJson(none))!;
        Assert.Equal("kpi", (string)kpi["layout"]!);
        Assert.Equal("desk", (string)desk["layout"]!);
        Assert.Equal("u-west", (string)desk["viewer"]!);

        // the desk layout places by_category, which the module denies to this subscriber (the West caller): the page carries its error block and the message says which widget and why
        Assert.Equal("error", (string)desk["rendered"]!["widgets"]!["by_category"]!["type"]!);
        var note = Assert.Single(none.Notes);
        Assert.Contains("The widget 'by_category' could not be resolved (denied)", note);
        Assert.Empty(named.Notes);                                                      // the kpi layout does not place it

        var report = Assert.IsType<RenderResult.Rendered>(await renderer.RenderAsync(Ask("alice", kind: "report", view: "summary", layout: "print"), default));
        Assert.Equal("summary-print.pdf", report.FileName);
        Assert.Equal("Summary", report.Title);
        var unknown = await renderer.RenderAsync(Ask("alice", layout: "poster"), default);
        Assert.Equal("DBD-VIEW-057", Assert.IsType<RenderResult.Failed>(unknown).Code);
    }

    private static string PrintJson(RenderResult.Rendered r)
    {
        var html = Encoding.UTF8.GetString(r.Content);
        var start = html.IndexOf("type=\"application/json\">", StringComparison.Ordinal) + "type=\"application/json\">".Length;
        return html[start..html.IndexOf("</script>", start, StringComparison.Ordinal)].Replace("\\u003c", "<");
    }

    [Fact]
    public async Task A_subscriber_the_module_refuses_or_cannot_act_as_loses_access()
    {
        var (h, renderer) = await StartAsync();
        await using var _ = h;
        var scoped = await renderer.RenderAsync(Ask("scoped", format: "csv", widget: "by_region"), default);
        Assert.IsType<RenderResult.AccessLost>(scoped);
        var gone = await renderer.RenderAsync(Ask("nobody"), default);
        Assert.Contains("identity home does not know this person", Assert.IsType<RenderResult.AccessLost>(gone).Reason);
        var unpublished = await renderer.RenderAsync(Ask("alice", view: "removed-dashboard"), default);
        Assert.Contains("removed-dashboard", Assert.IsType<RenderResult.AccessLost>(unpublished).Reason);
        Assert.Contains(h.Audit.Read(new AuditQuery(Verb: "view.export", Limit: 10)), r => r.Entry.Outcome == "refused");
    }

    [Fact]
    public async Task A_subscriber_denied_every_widget_of_the_layout_loses_access_and_one_denied_some_gets_the_rest_with_a_note()
    {
        var (h, renderer) = await StartAsync();
        await using var _ = h;
        h.Host.Before = (_, _) => throw new DbDataKit.Serving.ServingDeniedException("Source 'warehouse' is not available.");
        var all = await renderer.RenderAsync(Ask("alice", layout: "kpi"), default);                 // the kpi layout places one widget, and it is denied
        Assert.Contains("every widget of the dashboard 'rows' is denied to this person (revenue)", Assert.IsType<RenderResult.AccessLost>(all).Reason);
        var some = Assert.IsType<RenderResult.Rendered>(await renderer.RenderAsync(Ask("alice"), default));    // the desk layout places a text that cannot be denied
        Assert.NotEmpty(some.Notes);
    }

    [Fact]
    public async Task A_host_without_an_exporter_says_which_setting_is_missing_for_a_pdf_and_still_sends_a_csv()
    {
        var (h, renderer) = await StartAsync(exporter: false);
        await using var _ = h;
        var pdf = Assert.IsType<RenderResult.Failed>(await renderer.RenderAsync(Ask("alice"), default));
        Assert.Equal("unavailable", pdf.Code);
        Assert.Contains("views.export", pdf.Reason);
        Assert.IsType<RenderResult.Rendered>(await renderer.RenderAsync(Ask("alice", format: "csv", widget: "by_region"), default));
    }

    [Fact]
    public async Task The_whole_way_two_subscribers_through_the_service_the_outbox_shows_both_attachments_and_a_lost_subscriber_stops()
    {
        var (h, renderer) = await StartAsync();
        await using var _ = h;
        var database = engines.Provision(StateEngineIds.Sqlite).Open([DeliveryModule.Module], time: h.Clock);
        var payloads = new PayloadStore(database);
        var channel = new Channel();
        var delivery = new DeliveryService(new DeliveryStore(database), payloads, new Channels(channel), DeliveryPolicy.Default, new SecretScrubber(), h.Clock, null, "sender");
        var store = new SubscriptionStore(database);
        var service = new SubscriptionService(store, delivery, renderer, new People(), h.Clock);
        SubscriptionFields Fields(string name) => new(name, "dashboard", "rows", null, null, "csv", "by_region", "mail", null, "0 7 * * *", "UTC");
        var wendy = store.Create(Fields("Regions (Wendy)"), "wendy", "Wendy West", "wendy", ViewsHarness.Start);
        var erin = store.Create(Fields("Regions (Erin)"), "erin", "Erin East", "erin", ViewsHarness.Start);
        var lost = store.Create(Fields("Regions (Scoped)"), "scoped", "Sam Scoped", "alice", ViewsHarness.Start);

        var results = (await service.RunDueAsync(default)).ToDictionary(r => r.Name);
        Assert.Equal(("sent", "sent", "access-lost"), (results["Regions (Wendy)"].Outcome, results["Regions (Erin)"].Outcome, results["Regions (Scoped)"].Outcome));
        var messages = new DeliveryStore(database).List().Where(m => m.Attachments.Count > 0).ToDictionary(m => m.OwnerPerson!);
        string Body(MessageRow m) { using var r = payloads.OpenRead(m.Attachments[0].Payload); using var ms = new MemoryStream(); r.CopyTo(ms); return Encoding.UTF8.GetString(ms.ToArray()); }
        Assert.Contains("West", Body(messages["wendy"]));
        Assert.DoesNotContain("East", Body(messages["wendy"]));
        Assert.Contains("East", Body(messages["erin"]));
        Assert.DoesNotContain("West", Body(messages["erin"]));
        Assert.Equal("stopped", store.Find(lost)!.Status);
        Assert.Equal("active", store.Find(wendy)!.Status);
        Assert.Equal("active", store.Find(erin)!.Status);
    }

    private sealed class Channel : IDeliveryChannel
    {
        public string Name => "mail";

        public string Kind => "email";

        public int? LimitPerMinute => null;

        public Task<DeliveryResult> SendAsync(DeliveryPayload payload, CancellationToken cancellationToken) => Task.FromResult(DeliveryResult.Sent());
    }

    private sealed class Channels(IDeliveryChannel channel) : IDeliveryChannels
    {
        public IReadOnlyList<string> Names => [channel.Name];

        public IDeliveryChannel? Find(string name) => name == channel.Name ? channel : null;
    }

    private sealed class People : ISubscriberDirectory
    {
        public Task<SubscriberContact?> FindAsync(string person, CancellationToken cancellationToken) =>
            Task.FromResult<SubscriberContact?>(new SubscriberContact(person, person, person + "@example.com"));
    }
}

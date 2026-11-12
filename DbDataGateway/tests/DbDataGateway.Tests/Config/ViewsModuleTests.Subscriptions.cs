using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using DbDataKit.Delivery;
using DbDataKit.Runner;
using DbDataKit.State;
using DbDataKit.Views.Export;
using DbDataGateway.Server.Hosting;
using DbDataGateway.Server.Serving;
using DbDataGateway.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataGateway.Tests.Config;

/// <summary>Plan 32, DBR18: the Gateway composes and runs the <c>views-export</c> task in process. Through the real host (state, identity home, serving core, views module): a subscription that is due is drawn by the module as its subscriber and handed to delivery.</summary>
public sealed partial class ViewsModuleTests
{
    /// <summary>An exporter that returns the print page's HTML as the "PDF", so the rows composed for a person can be read without a browser.</summary>
    private sealed class PageAsPdfExporter : IReportExporter
    {
        public ExportAvailability Availability { get; } = new(true, "fake", "fake", "system");

        public BundledFonts? Fonts => null;

        public Task<ExportAvailability> CheckAsync(CancellationToken cancellationToken = default) => Task.FromResult(Availability);

        public Task<ExportResult> ExportPdfAsync(PrintJob job, CancellationToken cancellationToken = default) => Task.FromResult(new ExportResult(Encoding.UTF8.GetBytes(job.Html), 1, "fake", [], []));

        public Task<SvgResult> ExportSvgAsync(PrintJob job, string chartId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>A channel named <c>mail</c> that keeps what it was given.</summary>
    private sealed class CapturingChannels : IDeliveryChannels, IDeliveryChannel
    {
        public ConcurrentQueue<DeliveryPayload> Received { get; } = new();

        public IReadOnlyList<string> Names => ["mail"];

        public string Name => "mail";

        public string Kind => "email";

        public int? LimitPerMinute => null;

        public IDeliveryChannel? Find(string name) => string.Equals(name, Name, StringComparison.OrdinalIgnoreCase) ? this : null;

        public Task<DeliveryResult> SendAsync(DeliveryPayload payload, CancellationToken cancellationToken)
        {
            Received.Enqueue(payload);
            return Task.FromResult(DeliveryResult.Sent());
        }
    }

    private static SubscriptionFields SalesSubscription(string name, bool preview = false, string? channelKind = null) =>
        new(name, "dashboard", "sales", null, null, "pdf", null, "mail", null, "0 7 * * *", "UTC", preview, channelKind);

    [Fact]
    public async Task A_subscription_that_is_due_is_drawn_by_the_gateway_as_its_subscriber_and_handed_to_delivery_with_the_exporter_faked()
    {
        var channels = new CapturingChannels();
        overrides = services =>
        {
            services.AddSingleton<IReportExporter>(new PageAsPdfExporter());
            services.AddSingleton<IDeliveryChannels>(channels);
        };
        var assets = Path.Combine(dataDirectory, "print");           // the print page's files: the fake exporter never runs them, but the module wants them there
        Directory.CreateDirectory(assets);
        File.WriteAllText(Path.Combine(assets, "print.css"), "body { }");
        File.WriteAllText(Path.Combine(assets, "print.js"), "window.__reportReady = true;");
        await StartAsync(true, $"--Gateway:Views:Export:PageAssets={assets}");
        var state = Get<StateDatabase>();
        var (ada, _) = HomeSeed.Person(state, "Ada", "ada@example.invalid", Analysts);
        var (bob, _) = HomeSeed.Person(state, "Bob", "bob@example.invalid", "visitors");
        PublishModel();
        Assert.Equal(200, Get<GatewayViewsPublishing>().Publish("dashboard", "sales", Dashboard(), "test").Status);
        Poll();

        var subscriptions = Get<GatewaySubscriptions>();
        Assert.True(Get<GatewayViews>().CanExport);
        var store = subscriptions.Subscriptions;
        var due = DateTimeOffset.UtcNow.AddMinutes(-1);
        var adas = store.Create(SalesSubscription("Sales for Ada"), ada.Id, ada.DisplayName, ada.Id, due);
        var bobs = store.Create(SalesSubscription("Sales for Bob"), bob.Id, bob.DisplayName, bob.Id, due);
        var gone = store.Create(SalesSubscription("Sales for a person the home does not know"), "no-such-subject", "Gone", "no-such-subject", due);

        var result = await subscriptions.RunPassAsync(default);

        Assert.NotNull(result);
        Assert.Equal(3, result!.End.Processed);
        // Ada's data source group is the one the data source names: her document is rendered with the rows and goes to the outbox, addressed to her
        var sent = store.Find(adas)!;
        Assert.True(sent.LastOutcome == SubscriptionOutcomes.Sent, $"{sent.LastOutcome}: {sent.LastDetail}");
        var outbox = new DeliveryStore(state).List(source: $"subscription:{adas:D}");
        var message = Assert.Single(outbox);
        Assert.Equal(["ada@example.invalid"], message.Recipients);
        Assert.Equal("Sales", message.Subject);
        using (var read = new PayloadStore(state).OpenRead(Assert.Single(message.Attachments).Payload))
        using (var bytes = new MemoryStream())
        {
            read.CopyTo(bytes);
            var page = Encoding.UTF8.GetString(bytes.ToArray());
            Assert.Contains("net_revenue", page);
        }

        // a person the home does not know is lost access: stopped, nothing sent; Bob's group names no data source, so his document has an error where the rows would be (or stops): never Ada's rows
        Assert.Equal("stopped", store.Find(gone)!.Status);
        Assert.DoesNotContain(new DeliveryStore(state).List(source: $"subscription:{gone:D}"), m => m.Attachments.Count > 0);
        var bobsRow = store.Find(bobs)!;
        if (bobsRow.LastOutcome == SubscriptionOutcomes.Sent)
        {
            var bobsMessage = Assert.Single(new DeliveryStore(state).List(source: $"subscription:{bobs:D}", limit: 5), m => m.Attachments.Count > 0);
            using var read = new PayloadStore(state).OpenRead(bobsMessage.Attachments[0].Payload);
            using var bytes = new MemoryStream();
            read.CopyTo(bytes);
            Assert.DoesNotContain("\"text\":\"", Encoding.UTF8.GetString(bytes.ToArray()).Split("by_region")[0].Split("revenue")[^1]);
        }
        else
        {
            Assert.Equal("stopped", bobsRow.Status);
        }

        // the runner's record: one run of the task, by the Gateway
        var run = new RunStore(state, GatewaySubscriptions.RunService).Get(result.RunId);
        Assert.NotNull(run);
        Assert.Equal(ViewsExportTasks.Package, run!.Package);

        // the sender of this instance hands the outbox to the channel
        await subscriptions.Delivery!.RunOnceAsync(default);
        Assert.Contains(channels.Received, p => p.Recipients.Contains("ada@example.invalid") && p.Attachments.Count == 1);
    }

    [Fact]
    public async Task An_instance_without_an_exporter_claims_no_subscription()
    {
        overrides = null;
        await StartAsync();
        var state = Get<StateDatabase>();
        var (ada, _) = HomeSeed.Person(state, "Ada", "ada@example.invalid", Analysts);
        var subscriptions = Get<GatewaySubscriptions>();
        var id = subscriptions.Subscriptions.Create(SalesSubscription("Sales for Ada"), ada.Id, ada.DisplayName, ada.Id, DateTimeOffset.UtcNow.AddMinutes(-1));

        Assert.False(Get<GatewayViews>().CanExport);
        Assert.Null(await subscriptions.RunPassAsync(default));
        Assert.Equal("active", subscriptions.Subscriptions.Find(id)!.Status);
        Assert.Null(subscriptions.Subscriptions.Find(id)!.LastRunAt);                    // not taken, not failed: it waits for a host that can draw it
    }

    [Fact]
    public async Task A_gateway_with_no_delivery_channels_enqueues_by_the_kind_the_console_stored_and_sends_nothing_itself()
    {
        overrides = services => services.AddSingleton<IReportExporter>(new PageAsPdfExporter());
        var assets = Path.Combine(dataDirectory, "print");
        Directory.CreateDirectory(assets);
        File.WriteAllText(Path.Combine(assets, "print.css"), "body { }");
        File.WriteAllText(Path.Combine(assets, "print.js"), "window.__reportReady = true;");
        await StartAsync(true, $"--Gateway:Views:Export:PageAssets={assets}");
        var state = Get<StateDatabase>();
        var (ada, _) = HomeSeed.Person(state, "Ada", "ada@example.invalid", Analysts);
        PublishModel();
        Assert.Equal(200, Get<GatewayViewsPublishing>().Publish("dashboard", "sales", Dashboard(), "test").Status);
        Poll();
        var subscriptions = Get<GatewaySubscriptions>();
        Assert.True(Get<GatewayViews>().CanExport);
        Assert.False(subscriptions.HasChannels);

        var due = DateTimeOffset.UtcNow.AddMinutes(-1);
        var known = subscriptions.Subscriptions.Create(SalesSubscription("Sales for Ada", channelKind: "email"), ada.Id, ada.DisplayName, ada.Id, due);
        var unknown = subscriptions.Subscriptions.Create(SalesSubscription("Saved before the kind was stored") with { Channel = "other" }, ada.Id, ada.DisplayName, ada.Id, due);
        var result = await subscriptions.RunPassAsync(default);

        Assert.NotNull(result);
        Assert.Equal(SubscriptionOutcomes.Sent, subscriptions.Subscriptions.Find(known)!.LastOutcome);
        var message = Assert.Single(new DeliveryStore(state).List(source: $"subscription:{known:D}"));
        Assert.Equal(("mail", "pending"), (message.Channel, message.Status));        // written for the console's sender; this process holds no channel and sends nothing
        Assert.Equal(["ada@example.invalid"], message.Recipients);
        Assert.Equal(SubscriptionOutcomes.Failed, subscriptions.Subscriptions.Find(unknown)!.LastOutcome);   // no stored kind: not enqueued, and it says why
        Assert.Empty(new DeliveryStore(state).List(source: $"subscription:{unknown:D}"));
    }

    [Fact]
    public async Task A_subscription_is_drawn_to_a_real_pdf_by_the_gateways_own_browser_for_each_subscriber()
    {
        var browser = SystemChromium();
        if (browser is null) Assert.Skip("No Chromium found (DBDATAKIT_TEST_CHROMIUM, DBDATAKIT_TEST_CHROME_SYSTEM or /opt/pw-browsers): the Gateway's subscription test with a browser was not run.");
        var assets = Path.Combine(dataDirectory, "print");
        Directory.CreateDirectory(assets);
        File.WriteAllText(Path.Combine(assets, "print.css"), "body { font-family: sans-serif }");
        File.WriteAllText(Path.Combine(assets, "print.js"), """
            (function () {
              var p = JSON.parse(document.getElementById('dbdatakit-print').textContent), w = p.rendered.widgets, root = document.getElementById('root');
              Object.keys(w).forEach(function (id) { var d = document.createElement('div'); d.textContent = id + ' ' + w[id].type + (w[id].type === 'metric' ? ' ' + w[id].value.text : ''); root.appendChild(d); });
              document.fonts.ready.then(function () { window.__reportReady = true; });
            })();
            """);
        var channels = new CapturingChannels();
        overrides = services => services.AddSingleton<IDeliveryChannels>(channels);
        await StartAsync(true, "--Gateway:Views:Export:Mode=system", $"--Gateway:Views:Export:Path={browser}", $"--Gateway:Views:Export:PageAssets={assets}", $"--Gateway:Views:Export:NoSandbox={Environment.IsPrivilegedProcess}");
        var state = Get<StateDatabase>();
        var (ada, _) = HomeSeed.Person(state, "Ada", "ada@example.invalid", Analysts);
        PublishModel();
        Assert.Equal(200, Get<GatewayViewsPublishing>().Publish("dashboard", "sales", Dashboard(), "test").Status);
        Poll();

        var subscriptions = Get<GatewaySubscriptions>();
        var id = subscriptions.Subscriptions.Create(SalesSubscription("Sales for Ada"), ada.Id, ada.DisplayName, ada.Id, DateTimeOffset.UtcNow.AddMinutes(-1));
        var result = await subscriptions.RunPassAsync(default);

        Assert.Equal(TaskEndState.Succeeded, result!.End.State);
        await subscriptions.Delivery!.RunOnceAsync(default);
        var payload = Assert.Single(channels.Received);
        var attachment = Assert.Single(payload.Attachments);
        Assert.Equal("application/pdf", attachment.MediaType);
        using var pdf = UglyToad.PdfPig.PdfDocument.Open(attachment.Content);
        var text = string.Join(" ", pdf.GetPages().SelectMany(p => p.GetWords()).Select(w => w.Text));
        Assert.Contains("revenue metric", text);
        // the export of a subscription is audited as the subscriber, with the browser's version
        var events = new AuditLog(state, DbDataGateway.Server.State.GatewayState.ServiceId).Read(new AuditQuery(Verb: "view.export"));
        Assert.Contains(events, e => e.Entry.Outcome == "ok" && e.Entry.Detail!.Contains("Chrome/", StringComparison.Ordinal));
        Assert.Equal(SubscriptionOutcomes.Sent, subscriptions.Subscriptions.Find(id)!.LastOutcome);
    }
}

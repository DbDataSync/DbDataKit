using DbDataKit.Delivery;
using DbDataKit.Secrets;
using DbDataKit.State;
using DbDataKit.State.Tests;

namespace DbDataKit.Quality.Runner.Tests;

/// <summary>Plan 23a FQ13: a failed check writes one alert through the delivery service, marked once per failure and once per recovery, on every state engine, with a fake clock.</summary>
public sealed class QualityAlertsTests : IDisposable
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-11-12T12:00:00Z");

    private readonly StateEngines _engines = new();
    private readonly QualityData _data = new();

    public static TheoryData<string> Engines => StateEngines.Engines;

    public void Dispose()
    {
        _data.Dispose();
        _engines.Dispose();
    }

    private sealed class Channels(params string[] names) : IDeliveryChannels
    {
        public IReadOnlyList<string> Names => names;

        public IDeliveryChannel? Find(string name) => names.Contains(name) ? new Quiet(name) : null;
    }

    private sealed class Quiet(string name) : IDeliveryChannel
    {
        public string Name => name;

        public string Kind => "scripted";

        public int? LimitPerMinute => null;

        public Task<DeliveryResult> SendAsync(DeliveryPayload payload, CancellationToken cancellationToken) => Task.FromResult(DeliveryResult.Sent());
    }

    private sealed class Fixture
    {
        public required StateTestClock Clock { get; init; }
        public required QualityStore Store { get; init; }
        public required DeliveryStore Messages { get; init; }
        public required DeliveryService Delivery { get; init; }
    }

    private Fixture Open(string engine, params string[] channels)
    {
        var clock = new StateTestClock(Start);
        var database = _engines.Provision(engine).Open([QualityModule.Module, DeliveryModule.Module, RunsModule.Module], time: clock);
        var messages = new DeliveryStore(database);
        return new Fixture
        {
            Clock = clock,
            Store = new QualityStore(database),
            Messages = messages,
            Delivery = new DeliveryService(messages, new PayloadStore(database), new Channels(channels), DeliveryPolicy.Default, new SecretScrubber(), clock, null, "test"),
        };
    }

    private static QualityRunService Service(Fixture f, IQualitySource source, string[]? recipients = null) =>
        new(source, f.Store, time: f.Clock, alerts: new DeliveryQualityAlerts(f.Delivery, new QualityAlertSettings("alerts", recipients ?? ["data@example.com"], ConsoleAddress: new Uri("https://console.example/"))));

    [Theory, MemberData(nameof(Engines))]
    public async Task A_check_failing_twice_sends_one_alert_and_passing_again_sends_one_recovered(string engine)
    {
        var f = Open(engine, "alerts");
        var service = Service(f, _data.Source([QualityData.NotNullCustomer("customer_warn", CheckSeverity.Warn)]));

        await service.RunAsync(null);
        f.Clock.Advance(TimeSpan.FromMinutes(10));
        await service.RunAsync(null);
        Assert.Single(f.Messages.List(source: "alert:check"));

        _data.Execute("UPDATE orders SET customer_id = 99 WHERE customer_id IS NULL");
        f.Clock.Advance(TimeSpan.FromMinutes(10));
        await service.RunAsync(null);
        f.Clock.Advance(TimeSpan.FromMinutes(10));
        await service.RunAsync(null);

        var all = f.Messages.List(source: "alert:check").OrderBy(m => m.Subject).ToList();
        Assert.Equal(2, all.Count);
        Assert.Equal("Failed: customer_warn on duckdb:///orders", all.Single(m => m.Subject.StartsWith("Failed", StringComparison.Ordinal)).Subject);
        Assert.StartsWith("Recovered: customer_warn", all.Single(m => m.Subject.StartsWith("Recovered", StringComparison.Ordinal)).Subject);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task The_body_names_the_check_the_dataset_the_measure_and_the_sample_keys_and_goes_to_the_owners_too(string engine)
    {
        var f = Open(engine, "alerts");
        var dataset = _data.Dataset([QualityData.NotNullCustomer()], keys: ["id"]) with { Owners = ["owner@example.com", "DATA@example.com"] };
        var service = Service(f, new StaticQualitySource([dataset]));

        await service.RunAsync(null);

        var message = Assert.Single(f.Messages.List(source: "alert:check"));
        Assert.Equal("Blocked: customer_present on duckdb:///orders", message.Subject);
        Assert.Equal(["data@example.com", "owner@example.com"], message.Recipients);
        Assert.Contains("Dataset: duckdb:///orders", message.BodyText);
        Assert.Contains("Check: customer_present (not_null, block)", message.BodyText);
        Assert.Contains("measured 2, allowed 0", message.BodyText);
        Assert.Contains("Sample keys: id=2; id=4", message.BodyText);
        Assert.Equal("https://console.example/quality", message.Link);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_check_that_cannot_run_alerts_once_and_says_so_without_a_data_value(string engine)
    {
        var f = Open(engine, "alerts");
        var service = Service(f, _data.Source([QualityData.NotNullCustomer()], down: true));

        await service.RunAsync(null);
        f.Clock.Advance(TimeSpan.FromMinutes(5));
        await service.RunAsync(null);

        var message = Assert.Single(f.Messages.List(source: "alert:check"));
        Assert.StartsWith("Could not check: customer_present", message.Subject);
        Assert.Contains("It does not block (on_error: alert).", message.BodyText);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_first_pass_is_quiet_and_an_alert_that_cannot_be_written_does_not_fail_the_evaluation(string engine)
    {
        var f = Open(engine, "alerts");
        _data.Execute("UPDATE orders SET customer_id = 99 WHERE customer_id IS NULL");
        var quiet = await Service(f, _data.Source([QualityData.NotNullCustomer()])).RunAsync(null);
        Assert.Empty(f.Messages.List(source: "alert:check"));
        Assert.Empty(quiet.AlertProblems!);

        // the channel the alerts name is not configured: the check is still evaluated and recorded, and the run says what went wrong
        _data.Execute("UPDATE orders SET customer_id = NULL WHERE id = 1");
        f.Clock.Advance(TimeSpan.FromMinutes(5));
        var report = await new QualityRunService(_data.Source([QualityData.NotNullCustomer()]), f.Store, time: f.Clock,
            alerts: new DeliveryQualityAlerts(f.Delivery, new QualityAlertSettings("nowhere"))).RunAsync(null);

        Assert.Equal(1, report.Failed);
        Assert.Equal(CheckOutcome.Fail, f.Store.Status(QualityData.Orders, "customer_present")!.Outcome);
        Assert.Contains("the alert was not sent", Assert.Single(report.AlertProblems!));
        Assert.Contains("'nowhere'", report.AlertProblems![0]);
    }
}

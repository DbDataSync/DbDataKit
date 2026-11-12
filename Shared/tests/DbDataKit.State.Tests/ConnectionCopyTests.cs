using DbDataKit.State.Snapshots;

namespace DbDataKit.State.Tests;

/// <summary>Plan 27a IX23: the runner's copies of console connections (<c>runner.connection_copy</c>) and the posture choices of bindings kept in the same shape by the console (<c>bindings.posture_choice</c>), on every engine.</summary>
public sealed class ConnectionCopyTests : IDisposable
{
    private readonly StateEngines _engines = new();
    private readonly StateTestClock _clock = new(DateTimeOffset.Parse("2026-11-11T10:00:00Z"));

    public void Dispose() => _engines.Dispose();

    private StateDatabase Open(string engine) => _engines.Provision(engine).Open([RunnerModule.Module, BindingPostureModule.Module], StateScope.Standalone, time: _clock);

    [Fact]
    public void The_bindings_module_follows_the_conventions_and_is_kept_per_install_with_one_table()
    {
        Assert.Empty(StateConventions.Violations(BindingPostureModule.Module));
        Assert.Equal((StateScopeKind.Install, "bindings"), (BindingPostureModule.Module.Scope, BindingPostureModule.Module.Name));
        Assert.Equal(["posture_choice"], StateTableDefinitions.Read(BindingPostureModule.Module).Select(t => t.Name).Order());
    }

    [Fact]
    public void The_version_is_the_hash_of_the_text_and_line_endings_and_a_byte_order_mark_do_not_change_it()
    {
        var text = "schema: connection/2\nname: crm\nkind: http\n";
        Assert.Equal(ConnectionCopyVersion.Of(text), ConnectionCopyVersion.Of(text.Replace("\n", "\r\n", StringComparison.Ordinal)));
        Assert.Equal(ConnectionCopyVersion.Of(text), ConnectionCopyVersion.Of("﻿" + text));
        Assert.NotEqual(ConnectionCopyVersion.Of(text), ConnectionCopyVersion.Of(text + "# more\n"));
        Assert.StartsWith("sha256:", ConnectionCopyVersion.Of(text), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_copy_is_put_replaced_read_listed_and_removed(string engine)
    {
        var copies = new ConnectionCopies(Open(engine));
        Assert.Empty(copies.List());
        var first = copies.Put("crm", "http", "name: crm\n", "ada");
        _clock.Advance(TimeSpan.FromMinutes(5));
        var second = copies.Put("crm", "http", "name: crm\n# changed\n", "grace");
        copies.Put("mail", "smtp", "name: mail\n", "ada");

        Assert.NotEqual(first.Version, second.Version);
        var read = copies.Get("crm")!;
        Assert.Equal((second.Version, "http", "grace"), (read.Version, read.Kind, read.SentBy));
        Assert.Equal(second.Definition, read.Definition);
        Assert.Equal(["crm", "mail"], copies.List().Select(c => c.Name));
        Assert.Null(copies.Get("none"));
        Assert.True(copies.Delete("crm"));
        Assert.False(copies.Delete("crm"));
        Assert.Equal(["mail"], copies.List().Select(c => c.Name));
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void The_same_posture_code_keeps_a_choice_in_each_modules_own_table_and_keeps_the_date_of_a_choice_that_moved(string engine)
    {
        var database = Open(engine);
        var runner = new PostureChoices(database);
        var bindings = new PostureChoices(database, BindingPostureModule.Name);
        var earlier = DateTimeOffset.Parse("2026-11-01T08:00:00Z");
        runner.Record("bindings/runner/orders/source/dev", PostureSetting.ProductionBindingOnNonProduction.Name, "ada", "live crm");
        bindings.Record("bindings/delivery/alerts/test", PostureSetting.ProductionBindingOnNonProduction.Name, "grace", "live hook", earlier);

        Assert.Equal(["bindings/runner/orders/source/dev"], runner.List().Select(c => c.Scope));
        var moved = Assert.Single(bindings.List());
        Assert.Equal(("bindings/delivery/alerts/test", "grace", earlier), (moved.Scope, moved.ChosenBy, moved.ChosenAt));
        Assert.True(bindings.Revoke("bindings/delivery/alerts/test", PostureSetting.ProductionBindingOnNonProduction.Name));
        Assert.Empty(bindings.List());
        Assert.Single(runner.List());
    }
}

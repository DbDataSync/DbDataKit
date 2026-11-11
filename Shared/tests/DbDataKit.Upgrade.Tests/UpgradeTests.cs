using DbDataKit.Update.Detect;

namespace DbDataKit.Upgrade.Tests;

/// <summary>UC09: prepare verifies and reports; apply drains, swaps and the new version answers /version; a build that does not become healthy is rolled back.</summary>
public sealed class UpgradeTests : IDisposable
{
    private readonly Machine _machine = new();

    [Fact]
    public async Task Prepare_downloads_checks_and_reports_what_will_happen_and_changes_nothing()
    {
        _machine.Release(Machine.New, generation: 1);
        var report = await _machine.Create().PrepareAsync(null, default);
        Assert.Equal("prepared", (string?)report["state"]);
        Assert.Equal(Machine.New, (string?)report["to"]);
        Assert.True((bool?)report["can_apply"]);
        Assert.True((bool?)report["artifact"]!["verified"]);
        Assert.Equal(5, report["steps"]!.AsArray().Count);
        Assert.Equal(Machine.Old, await _machine.ServedVersionAsync());
        Assert.Equal(Machine.Old, File.ReadAllText(Path.Combine(_machine.Program, "version.txt")));
        Assert.Equal("prepared", (string?)_machine.Service.Status()["state"]);
    }

    [Fact]
    public async Task A_tampered_download_is_refused_and_nothing_is_staged()
    {
        var zip = _machine.Release(Machine.New);
        File.AppendAllText(zip, "tampered"); // the zip changed after its checksum was written; its size changed too
        var service = _machine.Create();
        var ex = await Assert.ThrowsAsync<UpgradeException>(() => service.PrepareAsync(null, default));
        Assert.Equal(422, ex.Status);
        Assert.Contains("was refused and nothing was changed", ex.Message);
        Assert.Equal("none", (string?)service.Status()["state"]);
    }

    [Fact]
    public async Task A_download_of_the_right_size_with_other_content_is_refused_by_its_hash()
    {
        var zip = _machine.Release(Machine.New);
        var bytes = File.ReadAllBytes(zip);
        bytes[^3] ^= 0xFF; // same size, one byte different
        File.WriteAllBytes(zip, bytes);
        var service = _machine.Create();
        var ex = await Assert.ThrowsAsync<UpgradeException>(() => service.PrepareAsync(null, default));
        Assert.Contains("sha256 does not match", ex.Message);
        Assert.Equal("none", (string?)service.Status()["state"]);
    }

    [Fact]
    public async Task A_zip_the_checksums_do_not_list_is_not_used()
    {
        _machine.Release(Machine.New, listInSums: false);
        var ex = await Assert.ThrowsAsync<UpgradeException>(() => _machine.Create().PrepareAsync(null, default));
        Assert.Contains("does not list", ex.Message);
    }

    [Fact]
    public async Task A_release_that_would_leave_a_paired_server_outside_N_minus_1_is_refused_naming_it()
    {
        _machine.Release(Machine.New, generation: 4);
        var report = await _machine.Create(() => [new PeerGeneration("console-a", 1)]).PrepareAsync(null, default);
        Assert.Equal("refused", (string?)report["state"]);
        Assert.False((bool?)report["can_apply"]);
        Assert.Contains("console-a", (string?)report["message"]);
        Assert.Contains("3 generations apart", (string?)report["message"]);
        Assert.Equal("none", (string?)_machine.Service.Status()["state"]);
    }

    [Fact]
    public async Task The_newest_release_being_the_running_one_is_up_to_date()
    {
        _machine.Release(Machine.Old);
        var report = await _machine.Create().PrepareAsync(null, default);
        Assert.Equal("up_to_date", (string?)report["state"]);
        Assert.False((bool?)report["can_apply"]);
    }

    [Fact]
    public async Task A_container_is_shown_the_command_and_nothing_is_downloaded()
    {
        _machine.Release(Machine.New);
        var container = new InstallEnvironment("/app/dbdatatest", "/app/", new Dictionary<string, string?> { ["DOTNET_RUNNING_IN_CONTAINER"] = "true" });
        var report = await _machine.Create(environment: container).PrepareAsync(null, default);
        Assert.Equal("instructions", (string?)report["state"]);
        Assert.False((bool?)report["can_apply"]);
        Assert.Contains("container", (string?)report["instruction"]!["text"]);
        Assert.False(Directory.Exists(Path.Combine(_machine.Program + ".upgrade", "updates", "downloads")));
        var ex = await Assert.ThrowsAsync<UpgradeException>(() => _machine.Service.ApplyAsync(default));
        Assert.Equal(409, ex.Status);
    }

    [Fact]
    public async Task Scoop_is_shown_its_own_command()
    {
        _machine.Release(Machine.New);
        var scoop = new InstallEnvironment("/home/u/scoop/apps/dbdatatest/current/dbdatatest", "/home/u/scoop/apps/dbdatatest/current/", new Dictionary<string, string?>());
        var report = await _machine.Create(environment: scoop).PrepareAsync(null, default);
        Assert.Equal("scoop update dbdatatest", (string?)report["instruction"]!["command"]);
    }

    [Fact]
    public async Task Apply_drains_swaps_and_the_new_version_answers_version()
    {
        _machine.Release(Machine.New, generation: 1);
        var service = _machine.Create();
        await service.PrepareAsync(null, default);
        var work = _machine.Drain.TrackWork(); // a query that is running
        var apply = service.ApplyAsync(default);
        await Task.Delay(300);
        Assert.False(apply.IsCompleted, "the drain waits for the work running");
        Assert.True(_machine.Drain.IsDraining);
        Assert.Equal("draining", (string?)service.Status()["state"]);
        Assert.Equal(Machine.Old, await _machine.ServedVersionAsync()); // nothing was stopped while work runs
        work.Dispose();
        var started = await apply;
        Assert.Equal("applying", (string?)started["state"]);
        await _machine.Launcher.Running!;

        Assert.Equal(Machine.New, await _machine.ServedVersionAsync());
        Assert.Equal(["stop", "start"], _machine.Unit.Calls);
        Assert.Equal(Machine.New, File.ReadAllText(Path.Combine(_machine.Program, "version.txt")));
        Assert.Equal(Machine.Old, File.ReadAllText(Path.Combine(_machine.Program + ".upgrade", "versions", Machine.Old, "version.txt"))); // the old version stays whole beside it
        var status = service.Status();
        Assert.Equal("committed", (string?)status["state"]);
        Assert.Equal(Machine.New, (string?)status["plan"]!["to"]);
        Assert.All(status["plan"]!["steps"]!.AsArray(), s => Assert.Equal("Done", (string?)s!["state"]));
    }

    [Fact]
    public async Task Apply_that_cannot_drain_in_time_changes_nothing_and_the_server_takes_work_again()
    {
        _machine.Release(Machine.New);
        var service = _machine.Create(drain: TimeSpan.FromMilliseconds(300));
        await service.PrepareAsync(null, default);
        using var work = _machine.Drain.TrackWork();
        var ex = await Assert.ThrowsAsync<UpgradeException>(() => service.ApplyAsync(default));
        Assert.Equal(409, ex.Status);
        Assert.Contains("never stopped silently", ex.Message);
        Assert.False(_machine.Drain.IsDraining);
        Assert.Empty(_machine.Unit.Calls);
        Assert.Equal(Machine.Old, await _machine.ServedVersionAsync());
        Assert.Equal("prepared", (string?)service.Status()["state"]); // it can be applied again
    }

    [Fact]
    public async Task A_new_build_that_does_not_become_healthy_is_rolled_back_and_the_old_version_serves_again()
    {
        _machine.Release(Machine.New, broken: true);
        var service = _machine.Create();
        await service.PrepareAsync(null, default);
        await service.ApplyAsync(default);
        await _machine.Launcher.Running!;

        Assert.Equal(Machine.Old, await _machine.ServedVersionAsync());
        Assert.Equal(Machine.Old, File.ReadAllText(Path.Combine(_machine.Program, "version.txt")));
        Assert.False(File.Exists(Path.Combine(_machine.Program, "broken")));
        Assert.Equal(["stop", "start", "stop", "start"], _machine.Unit.Calls);
        var status = service.Status();
        Assert.Equal("rolled_back", (string?)status["state"]);
        Assert.Contains("did not become healthy", (string?)status["message"]);
        Assert.Contains("did not become healthy", (string?)status["plan"]!["reason"]);
    }

    [Fact]
    public async Task Apply_without_a_prepared_upgrade_is_refused()
    {
        var ex = await Assert.ThrowsAsync<UpgradeException>(() => _machine.Create().ApplyAsync(default));
        Assert.Equal(409, ex.Status);
        Assert.Contains("prepare", ex.Message);
    }

    [Fact]
    public async Task A_server_started_by_hand_is_not_upgraded_from_the_console()
    {
        _machine.Release(Machine.New);
        var service = new UpgradeService(new UpgradeOptions { Product = Machine.Product, Version = Machine.Old, ProgramDirectory = _machine.Program, Environment = new InstallEnvironment("/opt/x", "/opt/", new Dictionary<string, string?>()) },
            ReleaseFeed.Create(new UpgradeFeedSettings { Mirror = _machine.Mirror }), _machine.Drain, _machine.Launcher);
        var report = await service.PrepareAsync(null, default);
        Assert.Equal("instructions", (string?)report["state"]);
        Assert.Contains("not started as a Windows service or a systemd unit", (string?)report["reason"]);
    }

    [Fact]
    public async Task Prepare_again_after_a_rollback_starts_a_new_journal()
    {
        _machine.Release(Machine.New, broken: true);
        var service = _machine.Create();
        await service.PrepareAsync(null, default);
        await service.ApplyAsync(default);
        await _machine.Launcher.Running!;
        Assert.Equal("rolled_back", (string?)service.Status()["state"]);
        var again = await service.PrepareAsync(null, default);
        Assert.Equal("prepared", (string?)again["state"]);
        Assert.Equal("prepared", (string?)service.Status()["state"]);
    }

    [Fact]
    public async Task A_journal_of_another_schema_is_refused_naming_the_file_and_is_not_written_over()
    {
        _machine.Release(Machine.New);
        var service = _machine.Create();
        var journal = Path.Combine(_machine.Program + ".upgrade", "updates", "update-plan.json");
        Directory.CreateDirectory(Path.GetDirectoryName(journal)!);
        File.WriteAllText(journal, "{\"schema\":\"update-plan/9\"}");
        var ex = await Assert.ThrowsAsync<UpgradeException>(() => service.PrepareAsync(null, default));
        Assert.Equal(409, ex.Status);
        Assert.Contains(journal, ex.Message);
        Assert.Contains("update-plan/9", ex.Message);
        Assert.Equal("{\"schema\":\"update-plan/9\"}", File.ReadAllText(journal));
        Assert.Contains(journal, (string?)service.Status()["problem"]);
    }

    public void Dispose() => _machine.Dispose();
}

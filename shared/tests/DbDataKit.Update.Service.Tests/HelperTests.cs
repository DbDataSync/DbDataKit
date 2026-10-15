using System.Text.Json;

namespace DbDataKit.Update.Service.Tests;

public class HelperTests
{
    [Fact]
    public async Task Success_CommitsAndSwapsVersion()
    {
        using var e = new Env();
        e.Health.Healthy.Add("1.5.0");
        e.Stage("1.5.0");
        var r = await e.Helper().RunAsync();
        Assert.Equal(UpdateOutcomeKind.Committed, r.Kind);
        Assert.Equal("1.5.0", e.Pointer.CurrentVersion());
        Assert.Equal("new 1.5.0", File.ReadAllText(Path.Combine(e.Layout.VersionsDir, "1.5.0", "app.exe")));
        Assert.True(Directory.Exists(Path.Combine(e.Layout.VersionsDir, "1.0.0")), "previous version is retained");
        Assert.Equal(["stop", "start"], e.Service.Calls);
        Assert.Equal("migrated", e.ConfigText);
        Assert.True(e.Service.Running);
        var plan = PlanStore.Load(e.Layout)!;
        Assert.Equal(PlanState.Committed, plan.State);
        Assert.All(plan.Steps, s => Assert.Equal(StepState.Done, s.State));
        Assert.Contains("Committed", File.ReadAllText(e.Layout.ResultPath));
    }

    [Fact]
    public async Task Rerun_AfterCommit_IsNoOp()
    {
        using var e = new Env();
        e.Health.Healthy.Add("1.5.0");
        e.Stage("1.5.0");
        await e.Helper().RunAsync();
        e.Service.Calls.Clear();
        var r = await e.Helper().RunAsync();
        Assert.Equal(UpdateOutcomeKind.Committed, r.Kind);
        Assert.Empty(e.Service.Calls);
    }

    [Fact]
    public async Task NoPlan_ReportsNoPlan()
    {
        using var e = new Env();
        Assert.Equal(UpdateOutcomeKind.NoPlan, (await e.Helper().RunAsync()).Kind);
    }

    [Fact]
    public async Task FailedHealth_RollsBackPointerConfigAndService()
    {
        using var e = new Env();
        // 1.5.0 never becomes healthy
        e.Stage("1.5.0");
        var r = await e.Helper().RunAsync();
        Assert.Equal(UpdateOutcomeKind.RolledBack, r.Kind);
        Assert.Equal("1.0.0", e.Pointer.CurrentVersion());
        Assert.Equal("v1", e.ConfigText);
        Assert.True(e.Service.Running);
        Assert.Equal(["stop", "start", "stop", "start"], e.Service.Calls);
        Assert.False(Directory.Exists(Path.Combine(e.Layout.VersionsDir, "1.5.0")));
        var res = File.ReadAllText(e.Layout.ResultPath);
        Assert.Contains("RolledBack", res);
        Assert.Contains("Health check failed", res);
    }

    [Fact]
    public async Task Interrupted_AfterStart_RollsBackOnNextRun()
    {
        using var e = new Env();
        e.Stage("1.5.0");
        e.Health.ThrowCancel = true; // helper "killed" while polling health
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => e.Helper().RunAsync());
        Assert.Equal("1.5.0", e.Pointer.CurrentVersion());
        Assert.Equal(PlanState.InProgress, PlanStore.Load(e.Layout)!.State);

        e.Health.ThrowCancel = false;
        var r = await e.Helper().RunAsync(); // fresh helper process
        Assert.Equal(UpdateOutcomeKind.RolledBack, r.Kind);
        Assert.Equal("1.0.0", e.Pointer.CurrentVersion());
        Assert.Equal("v1", e.ConfigText);
        Assert.True(e.Service.Running);
    }

    [Fact]
    public async Task Interrupted_BeforeRepoint_ResumesAndCommits()
    {
        using var e = new Env();
        e.Health.Healthy.Add("1.5.0");
        e.Stage("1.5.0");
        e.Migration.ThrowCancel = true;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => e.Helper().RunAsync());
        Assert.Equal("1.0.0", e.Pointer.CurrentVersion());
        var plan = PlanStore.Load(e.Layout)!;
        Assert.Equal(StepState.Running, plan.StateOf(StepName.Migrate));
        Assert.Equal(RecoveryAction.Resume, RecoveryPlanner.Decide(plan));

        e.Migration.ThrowCancel = false;
        e.Service.Running = false; // service is down from the interrupted attempt
        var r = await e.Helper().RunAsync();
        Assert.Equal(UpdateOutcomeKind.Committed, r.Kind);
        Assert.Equal("1.5.0", e.Pointer.CurrentVersion());
        Assert.True(e.Service.Running);
    }

    [Fact]
    public async Task TamperedArtifact_RejectedWithoutTouchingTheService()
    {
        using var e = new Env();
        e.Health.Healthy.Add("1.5.0");
        var plan = e.Stage("1.5.0");
        var art = e.Layout.ResolveUnderUpdates(plan.ArtifactFile);
        var bytes = File.ReadAllBytes(art);
        bytes[^1] ^= 0xFF;
        File.WriteAllBytes(art, bytes);
        var r = await e.Helper().RunAsync();
        Assert.Equal(UpdateOutcomeKind.Rejected, r.Kind);
        Assert.Empty(e.Service.Calls);
        Assert.Equal("1.0.0", e.Pointer.CurrentVersion());
        Assert.False(Directory.Exists(Path.Combine(e.Layout.VersionsDir, "1.5.0")));
    }

    [Fact]
    public async Task TamperedManifest_EvenWithPlanHashFixedUp_FailsSignature()
    {
        using var e = new Env();
        var plan = e.Stage("1.5.0");
        var mf = e.Layout.ResolveUnderUpdates(plan.ManifestFile);
        var text = File.ReadAllText(mf).Replace("example.invalid", "evil.invalid");
        File.WriteAllText(mf, text);
        plan.ManifestSha256 = ManifestSigner.Sha256Hex(File.ReadAllBytes(mf)); // attacker updates the plan too
        PlanStore.Save(e.Layout, plan);
        var r = await e.Helper().RunAsync();
        Assert.Equal(UpdateOutcomeKind.Rejected, r.Kind);
        Assert.Contains("Signature does not match", r.Message);
        Assert.Empty(e.Service.Calls);
    }

    [Fact]
    public async Task ManifestSwappedAfterPlanning_HashRecordedInPlanCatchesIt()
    {
        using var e = new Env();
        var plan = e.Stage("1.5.0");
        plan.ManifestSha256 = new string('a', 64);
        PlanStore.Save(e.Layout, plan);
        var r = await e.Helper().RunAsync();
        Assert.Equal(UpdateOutcomeKind.Rejected, r.Kind);
        Assert.Contains("hash", r.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WrongKey_Rejected()
    {
        using var e = new Env();
        e.Stage("1.5.0", signer: new KeyPair("k1")); // attacker key claiming the trusted id
        var r = await e.Helper().RunAsync();
        Assert.Equal(UpdateOutcomeKind.Rejected, r.Kind);
        Assert.Empty(e.Service.Calls);
    }

    [Fact]
    public async Task UnknownKeyId_Rejected()
    {
        using var e = new Env();
        e.Stage("1.5.0", signer: new KeyPair("other"));
        var r = await e.Helper().RunAsync();
        Assert.Equal(UpdateOutcomeKind.Rejected, r.Kind);
        Assert.Contains("not trusted", r.Message);
    }

    [Fact]
    public async Task DowngradeAttempt_RejectedUnlessAdminAllows()
    {
        using var e = new Env("2.0.0");
        e.Health.Healthy.Add("1.0.0");
        e.Stage("1.0.0");
        var r = await e.Helper().RunAsync();
        Assert.Equal(UpdateOutcomeKind.Rejected, r.Kind);
        Assert.Contains("downgrade", r.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("2.0.0", e.Pointer.CurrentVersion());

        using var e2 = new Env("2.0.0");
        e2.Health.Healthy.Add("1.0.0");
        e2.Stage("1.0.0");
        var r2 = await e2.Helper(new UpdateHelperOptions { AllowDowngrade = true }).RunAsync();
        Assert.Equal(UpdateOutcomeKind.Committed, r2.Kind);
        Assert.Equal("1.0.0", e2.Pointer.CurrentVersion());
    }

    [Fact]
    public async Task RevokedVersion_Rejected()
    {
        using var e = new Env();
        e.Stage("1.5.0", tweak: m => m with { Revoked = true });
        var r = await e.Helper().RunAsync();
        Assert.Equal(UpdateOutcomeKind.Rejected, r.Kind);
        Assert.Contains("revoked", r.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(e.Service.Calls);
    }

    [Fact]
    public async Task ZipSlipArchive_RejectedAndNothingWrittenOutside()
    {
        using var e = new Env();
        e.Stage("1.5.0", artifact: Env.Zip(("app.exe", "x"), ("../../escaped.txt", "pwn")));
        var r = await e.Helper().RunAsync();
        Assert.Equal(UpdateOutcomeKind.Rejected, r.Kind);
        Assert.Contains("Unsafe archive", r.Message);
        Assert.False(File.Exists(Path.Combine(e.Root, "..", "escaped.txt")));
        Assert.False(File.Exists(Path.Combine(e.Layout.VersionsDir, "escaped.txt")));
        Assert.Empty(e.Service.Calls);
        Assert.Equal("1.0.0", e.Pointer.CurrentVersion());
    }

    [Theory]
    [InlineData("manifest-traversal")]
    [InlineData("artifact-absolute")]
    [InlineData("to-traversal")]
    [InlineData("from-traversal")]
    [InlineData("health-remote")]
    [InlineData("wrong-product")]
    public async Task MaliciousPlan_Rejected(string kind)
    {
        using var e = new Env();
        var plan = e.Stage("1.5.0");
        switch (kind)
        {
            case "manifest-traversal": plan.ManifestFile = "../../../outside/release.json"; break;
            case "artifact-absolute": plan.ArtifactFile = OperatingSystem.IsWindows() ? @"C:\Windows\notepad.exe" : "/bin/sh"; break;
            case "to-traversal": plan.To = "..\\..\\evil"; break;
            case "from-traversal": plan.From = "../x"; break;
            case "health-remote": plan.HealthUrl = "http://attacker.example/health"; break;
            case "wrong-product": plan.Product = "dbdatasync"; break;
        }
        PlanStore.Save(e.Layout, plan);
        var r = await e.Helper().RunAsync();
        Assert.Equal(UpdateOutcomeKind.Rejected, r.Kind);
        Assert.Empty(e.Service.Calls);
        Assert.Equal("1.0.0", e.Pointer.CurrentVersion());
    }

    [Fact]
    public async Task CorruptPlan_IsQuarantined()
    {
        using var e = new Env();
        Directory.CreateDirectory(e.Layout.UpdatesDir);
        File.WriteAllText(e.Layout.PlanPath, "{ not json");
        var r = await e.Helper().RunAsync();
        Assert.Equal(UpdateOutcomeKind.Rejected, r.Kind);
        Assert.True(File.Exists(e.Layout.PlanPath + ".rejected"));
        Assert.False(File.Exists(e.Layout.PlanPath));
    }

    [Fact]
    public async Task StalePlan_FromDoesNotMatchInstalled_Rejected()
    {
        using var e = new Env();
        var plan = e.Stage("1.5.0");
        plan.From = "0.9.0";
        PlanStore.Save(e.Layout, plan);
        var r = await e.Helper().RunAsync();
        Assert.Equal(UpdateOutcomeKind.Rejected, r.Kind);
        Assert.Contains("stale", r.Message);
    }

    [Fact]
    public async Task IrreversibleMigration_WithoutVerifiedBackup_IsRolledBackBeforeAnyChange()
    {
        using var e = new Env();
        e.Migration.Irreversible = true;
        e.Stage("1.5.0");
        var r = await e.Helper().RunAsync(); // no backup hook registered
        Assert.Equal(UpdateOutcomeKind.RolledBack, r.Kind);
        Assert.Equal(0, e.Migration.Runs);
        Assert.Equal("1.0.0", e.Pointer.CurrentVersion());
        Assert.True(e.Service.Running);
    }

    [Fact]
    public async Task IrreversibleMigration_FailingHealth_NeedsOperatorAndIsNotRolledBack()
    {
        using var e = new Env();
        e.Migration.Irreversible = true;
        e.Stage("1.5.0");
        var r = await e.Helper(backup: new FakeBackup(true)).RunAsync();
        Assert.Equal(UpdateOutcomeKind.NeedsOperator, r.Kind);
        Assert.Equal("1.5.0", e.Pointer.CurrentVersion());
        Assert.Equal("migrated", e.ConfigText);
        Assert.True(PlanStore.Load(e.Layout)!.IrreversibleMigrationStarted);
        // A later run must not roll back either.
        Assert.Equal(UpdateOutcomeKind.NeedsOperator, (await e.Helper(backup: new FakeBackup(true)).RunAsync()).Kind);
        Assert.Equal("1.5.0", e.Pointer.CurrentVersion());
    }

    [Fact]
    public async Task IrreversibleMigration_InterruptedAfterStart_ResumesForward()
    {
        using var e = new Env();
        e.Migration.Irreversible = true;
        e.Health.Healthy.Add("1.5.0");
        e.Stage("1.5.0");
        e.Migration.ThrowCancel = true;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => e.Helper(backup: new FakeBackup(true)).RunAsync());
        Assert.Equal(RecoveryAction.ResumeForwardOnly, RecoveryPlanner.Decide(PlanStore.Load(e.Layout)!));
        e.Migration.ThrowCancel = false;
        var r = await e.Helper(backup: new FakeBackup(true)).RunAsync();
        Assert.Equal(UpdateOutcomeKind.Committed, r.Kind);
    }

    [Fact]
    public async Task Retention_PrunesOldVersions()
    {
        using var e = new Env("1.0.0");
        foreach (var v in new[] { "0.7.0", "0.8.0", "0.9.0" }) Directory.CreateDirectory(Path.Combine(e.Layout.VersionsDir, v));
        e.Health.Healthy.Add("1.5.0");
        e.Stage("1.5.0");
        await e.Helper(new UpdateHelperOptions { RetainVersions = 2 }).RunAsync();
        var left = e.Layout.InstalledVersions().OrderBy(x => x).ToArray();
        Assert.Equal(["0.9.0", "1.0.0", "1.5.0"], left);
    }

    [Fact]
    public void PlanJournal_RoundTripsAndSurvivesTornTempFile()
    {
        using var e = new Env();
        var plan = e.Stage("1.5.0");
        File.WriteAllText(e.Layout.PlanPath + ".tmp", "{ torn"); // leftover from a power cut mid-write
        var loaded = PlanStore.Load(e.Layout)!;
        Assert.Equal(plan.Id, loaded.Id);
        Assert.Equal(8, loaded.Steps.Count);
        Assert.Contains("\"state\": \"Pending\"", File.ReadAllText(e.Layout.PlanPath));
        using var _ = JsonDocument.Parse(File.ReadAllText(e.Layout.PlanPath));
    }
}

public class RecoveryPlannerTests
{
    private static UpdatePlan P(PlanState s, params (StepName, StepState)[] steps)
    {
        var p = new UpdatePlan { State = s };
        foreach (var (n, st) in steps) p.Step(n).State = st;
        return p;
    }

    [Fact] public void Terminal_NothingToDo() { Assert.Equal(RecoveryAction.NothingToDo, RecoveryPlanner.Decide(P(PlanState.Committed))); Assert.Equal(RecoveryAction.NothingToDo, RecoveryPlanner.Decide(P(PlanState.RolledBack))); }
    [Fact] public void Pending_Resumes() => Assert.Equal(RecoveryAction.Resume, RecoveryPlanner.Decide(P(PlanState.Pending)));
    [Fact] public void BeforeRepoint_Resumes() => Assert.Equal(RecoveryAction.Resume, RecoveryPlanner.Decide(P(PlanState.InProgress, (StepName.Verify, StepState.Done), (StepName.Stop, StepState.Done), (StepName.Backup, StepState.Running))));
    [Fact] public void AfterRepoint_RollsBack() => Assert.Equal(RecoveryAction.Rollback, RecoveryPlanner.Decide(P(PlanState.InProgress, (StepName.Repoint, StepState.Done), (StepName.Start, StepState.Running))));
    [Fact] public void FailedStep_RollsBack() => Assert.Equal(RecoveryAction.Rollback, RecoveryPlanner.Decide(P(PlanState.InProgress, (StepName.Migrate, StepState.Failed))));
    [Fact] public void RollingBack_ContinuesRollback() => Assert.Equal(RecoveryAction.Rollback, RecoveryPlanner.Decide(P(PlanState.RollingBack)));
    [Fact] public void Failed_NeedsOperator() => Assert.Equal(RecoveryAction.NeedsOperator, RecoveryPlanner.Decide(P(PlanState.Failed)));

    [Fact]
    public void IrreversibleStarted_NeverRollsBack()
    {
        var p = P(PlanState.InProgress, (StepName.Repoint, StepState.Done));
        p.IrreversibleMigrationStarted = true;
        Assert.Equal(RecoveryAction.ResumeForwardOnly, RecoveryPlanner.Decide(p));
        p.Step(StepName.HealthCheck).State = StepState.Failed;
        Assert.Equal(RecoveryAction.NeedsOperator, RecoveryPlanner.Decide(p));
    }
}

public class LayoutTests
{
    [Theory]
    [InlineData("1.2.3", true)]
    [InlineData("1.2.3-beta.1", true)]
    [InlineData("..", false)]
    [InlineData("1.2.3/../..", false)]
    [InlineData("..\\evil", false)]
    [InlineData("C:", false)]
    [InlineData("", false)]
    [InlineData("latest", false)]
    public void SafeVersion(string v, bool ok) => Assert.Equal(ok, InstallLayout.IsSafeVersion(v));

    [Theory]
    [InlineData("staging/1.0.0/release.json", true)]
    [InlineData("staging\\1.0.0\\release.json", true)]
    [InlineData("../x", false)]
    [InlineData("staging/../../x", false)]
    [InlineData("/etc/passwd", false)]
    [InlineData("C:\\x", false)]
    public void ResolveUnderUpdates(string rel, bool ok)
    {
        var l = new InstallLayout(Path.Combine(Path.GetTempPath(), "layout-test"));
        if (ok) Assert.StartsWith(l.UpdatesDir, l.ResolveUnderUpdates(rel));
        else Assert.Throws<UpdatePlanException>(() => l.ResolveUnderUpdates(rel));
    }

    [Fact]
    public void LinkPointer_SwitchesCurrent()
    {
        var root = Path.Combine(Path.GetTempPath(), "dbdul-" + Guid.NewGuid().ToString("N"));
        try
        {
            var l = new InstallLayout(root);
            Directory.CreateDirectory(Path.Combine(l.VersionsDir, "1.0.0"));
            Directory.CreateDirectory(Path.Combine(l.VersionsDir, "2.0.0"));
            File.WriteAllText(Path.Combine(l.VersionsDir, "2.0.0", "f.txt"), "two");
            var p = new LinkCurrentPointer(l);
            try { p.Set("1.0.0"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Assert.Skip("Cannot create directory links here: " + ex.Message); }
            Assert.Equal("1.0.0", p.CurrentVersion());
            p.Set("2.0.0");
            Assert.Equal("2.0.0", p.CurrentVersion());
            Assert.Equal("two", File.ReadAllText(Path.Combine(l.CurrentLink, "f.txt")));
            Assert.Throws<UpdatePlanException>(() => p.Set("../x"));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}

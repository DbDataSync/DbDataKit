using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataGateway.Server.Configuration;
using DbDataGateway.Server.Export;
using DbDataKit.Views.Export;
using DbDataKit.Web.Trust;

namespace DbDataGateway.Server.Management;

/// <summary>
/// The export component on the management API (plan 32 DBR16): the console's "PDF export is not installed: Install" card calls the same <see cref="ExportInstaller"/> the command <c>dbdatagateway export install</c> runs.
/// <code>
/// GET  /api/v1/views/export          {platform, supported, installed, whole, directory, release, chromium, fonts, mode, configured, pins_verified, pins_note, problems, summary, install: {state, step, bytes, total, message, restart_needed}}  (admin)
/// POST /api/v1/views/export/install  {version?, force?}  starts the install in the background and answers 202 with the same body (admin); a second call while one runs answers the running one
/// </code>
/// The release address is the host's (<c>DBDATAKIT_EXPORT_RELEASE_BASE</c>, the upgrade mirror, else the product's GitHub release), never a caller's: a screen cannot point a host at another source. A finished install sets
/// <c>views.export</c> in <c>server.yaml</c> as a commit by the caller (<see cref="GatewayServerSettings.SetAsync"/>), and says a restart is needed (the views module reads it at start). Every start and its outcome is audited.
/// </summary>
public sealed class GatewayExportManagement(ExportInstaller installer, Func<GatewayOptions> options, string dataDirectory, GatewayServerSettings settings, IAuditLog audit, TimeProvider? clock = null)
{
    public const string Route = GatewayManagement.Prefix + "/views/export";

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly object _gate = new();
    private ExportInstallProgress _progress = new("idle", "", 0, 0, null);
    private bool _restartNeeded;
    private Task? _running;

    public static bool Handles(string route) => route == Route || route == Route + "/install";

    /// <summary>The background install of the last start, for a test to wait for.</summary>
    public Task? Running { get { lock (_gate) return _running; } }

    public async Task<ManagementResult> HandleAsync(string method, string route, string? body, ManagementCaller caller, CancellationToken cancellationToken)
    {
        if (!caller.Holds(ServerRole.Admin)) return ManagementResult.Problem(403, "Installing the export component needs the admin role.");
        if (route == Route) return method == "GET" ? ManagementResult.Ok(Body()) : ManagementResult.Problem(405, "The export component is read with GET and installed with POST .../install.");
        if (method != "POST") return ManagementResult.Problem(405, "The export component is installed with POST.");
        JsonObject? json = null;
        try { if (!string.IsNullOrWhiteSpace(body)) json = JsonNode.Parse(body) as JsonObject ?? throw new JsonException(); }
        catch (JsonException) { return ManagementResult.Problem(400, "The body is a JSON object: {\"version\": \"<release>\", \"force\": false}, or empty."); }
        var request = new ExportInstallRequest(json?["version"]?.GetValue<string>() is { Length: > 0 } v ? v : null, null, null, null, json?["force"]?.GetValue<bool>() ?? false);
        lock (_gate)
        {
            if (_running is { IsCompleted: false }) return new ManagementResult(202, Body());
            _progress = new ExportInstallProgress("running", "starting", 0, 0, null);
            _restartNeeded = false;
            _running = Task.Run(() => RunAsync(request, caller), CancellationToken.None);
        }
        Record(caller, "export.install", "started" + (request.Version is null ? "" : " " + request.Version));
        return new ManagementResult(202, Body());
    }

    private async Task RunAsync(ExportInstallRequest request, ManagementCaller caller)
    {
        try
        {
            var result = await installer.InstallAsync(request, new ExportProgressSink(p => { lock (_gate) _progress = p; })).ConfigureAwait(false);
            lock (_gate) _progress = new ExportInstallProgress("running", "setting views.export", result.DownloadedBytes, result.DownloadedBytes, null);
            var written = await settings.SetAsync(ExportComponent.SettingsFor(result.Directory), caller, $"PDF export component {result.Version} installed", CancellationToken.None).ConfigureAwait(false);
            if (written.Status != 200)
            {
                var why = written.Body["error"]?.GetValue<string>() ?? "server.yaml was refused";
                Record(caller, "export.install", "failed: views.export not set: " + why);
                Finish("failed", $"The component is installed in {result.Directory}, but views.export could not be set: {why}", false);
                return;
            }
            var restart = written.Body["restart_needed"]?.GetValue<bool>() ?? true;
            Record(caller, "export.install", (result.AlreadyInstalled ? "already installed " : "installed ") + result.Version + (result.Pins.Verified ? " (" + result.Pins.Note.TrimEnd('.') + ")" : ": " + result.Pins.Note));
            Finish("done", (result.AlreadyInstalled ? $"Release {result.Version} was already installed; views.export points at it." : $"Release {result.Version} is installed (Chromium {result.Chromium}). Restart the gateway to use it.") + " " + result.Pins.Sentence, restart);
        }
        catch (ExportInstallException ex)
        {
            Record(caller, "export.install", "failed: " + ex.Message.Split('\n')[0].Trim());
            Finish("failed", ex.Message, false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Record(caller, "export.install", "failed: " + ex.Message);
            Finish("failed", "The install failed: " + ex.Message, false);
        }
    }

    private void Finish(string state, string message, bool restart)
    {
        lock (_gate) { _progress = new ExportInstallProgress(state, state == "done" ? "finished" : "stopped", _progress.Bytes, _progress.Total, message); _restartNeeded = restart; }
    }

    private JsonObject Body()
    {
        var body = ExportComponentStatus.Describe(options().Views.Export, dataDirectory);
        ExportInstallProgress progress;
        bool restart;
        lock (_gate) { progress = _progress; restart = _restartNeeded; }
        body["install"] = new JsonObject { ["state"] = progress.State, ["step"] = progress.Step, ["bytes"] = progress.Bytes, ["total"] = progress.Total, ["message"] = progress.Message, ["restart_needed"] = restart };
        return body;
    }

    private void Record(ManagementCaller caller, string command, string outcome) => audit.Append(caller.Audit(command, "views.export", outcome, _clock.GetUtcNow()));
}

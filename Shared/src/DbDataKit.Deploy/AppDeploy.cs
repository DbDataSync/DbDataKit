using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataKit.Apps;
using DbDataKit.State;

namespace DbDataKit.Deploy;

/// <summary>A built app read for sending: its name (from its manifest) and the body a Gateway's app calls take (the manifest, the files in base64, the note).</summary>
public sealed record AppToDeploy(string Name, JsonObject Body);

/// <summary>
/// A built app deployed to a Gateway: the Gateway first checks what publishing would do (<c>POST /api/v1/apps/check</c>; its refusal, in its words, ends the deploy before anything is stored), then the version is published
/// (<c>PUT /api/v1/apps/{name}</c>), or sent to the Gateway's <see cref="PreviewChannel"/> with an instance's preview pointed at it. The caller reads the app first (<see cref="Read"/>), then sends it through its link (<see cref="SendAsync"/>).
/// </summary>
public static class AppDeploy
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>The Gateway channel a preview with data goes to.</summary>
    public const string PreviewChannel = "preview";

    /// <summary>Reads the app in <paramref name="appFolder"/> (a build's output) with its <paramref name="note"/>. An app that cannot be read, or whose manifest is not valid, is a <see cref="DeployException"/>.</summary>
    public static AppToDeploy Read(string appFolder, string? note, Action<string> step)
    {
        step("Read the built app.");
        AppPackage package;
        try { package = AppPackage.FromFolder(appFolder); }
        catch (Exception ex) when (ex is AppPackageException or IOException) { throw new DeployException($"The built app cannot be sent: {ex.Message}", 409, inner: ex); }
        var body = new JsonObject { ["manifest"] = package.ManifestYaml, ["files"] = new JsonObject(package.Files.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => KeyValuePair.Create(f.Key, (JsonNode?)Convert.ToBase64String(f.Value)))), ["note"] = note };
        var name = AppManifestLoader.Load(package.ManifestYaml) is { Ok: true } load ? load.Manifest!.Name : throw new DeployException("The built app's manifest is not valid.", 409);
        return new AppToDeploy(name, body);
    }

    /// <summary>
    /// Sends <paramref name="app"/> to the Gateway <paramref name="target"/> through <paramref name="link"/>: the check, then the version, or with <paramref name="previewInstance"/> the preview channel and that instance's preview.
    /// <paramref name="step"/> is told each step as it happens. Returns what the Gateway answered, with the app, the target's name and its address.
    /// </summary>
    public static async Task<JsonObject> SendAsync(IDefinitionsLink link, DeployTarget target, AppToDeploy app, Action<string> step, CancellationToken ct = default, string? previewInstance = null)
    {
        var name = app.Name;
        async Task<JsonObject> Call(HttpMethod method, string path, string doing, JsonObject? content = null)
        {
            HttpResponseMessage response;
            try { response = await link.SendAsync(method, path, JsonContent.Create(content ?? app.Body, options: Json), ct); }
            catch (HttpRequestException ex) { throw new DeployException($"{target.Url} could not be reached ({ex.Message}).", 502, inner: ex); }
            using (response)
            {
                var text = await response.Content.ReadAsStringAsync(ct);
                JsonObject? answer = null;
                try { answer = string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text) as JsonObject; }
                catch (JsonException) { /* not JSON: the text is the answer */ }
                if (!response.IsSuccessStatusCode)
                    throw new DeployException($"The Gateway refused {doing} ({(int)response.StatusCode}): {(string?)answer?["error"] ?? (text.Length > 0 ? text : response.ReasonPhrase)}", response.StatusCode == System.Net.HttpStatusCode.Forbidden ? 403 : 409);
                return answer ?? [];
            }
        }

        step($"Asked {target.Url} what publishing {name} would do.");
        var check = await Call(HttpMethod.Post, "apps/check", $"the check of the app {name}");
        if (check["diagnostics"] is JsonArray diagnostics && diagnostics.Any(d => (string?)d?["severity"] is "error" or "Error"))
            throw new DeployException("The Gateway's check of the app found errors: " + string.Join(" ", diagnostics.Where(d => (string?)d?["severity"] is "error" or "Error").Select(d => (string?)d?["message"] ?? d!.ToJsonString())), 409);
        if (previewInstance is not null)
        {
            // not production: the build goes to the Gateway's preview channel and a preview of the instance runs it; that instance reads the data the production one reads and writes nothing
            step($"Published {name} to the preview channel of {target.Url}.");
            var sent = await Call(HttpMethod.Put, $"apps/{Uri.EscapeDataString(name)}/channels/{PreviewChannel}", $"the preview of the app {name}");
            var instance = await Call(HttpMethod.Put, $"app-instances/{Uri.EscapeDataString(previewInstance)}/channels/{PreviewChannel}", $"the preview of the instance {previewInstance}", new JsonObject { ["app"] = name });
            step($"Saved the preview of {previewInstance}{((string?)instance["address"] is { } at ? " at " + at : "")}.");
            sent["app"] = name;
            sent["instance"] = previewInstance;
            sent["preview_path"] = (string?)instance["path"];
            sent["preview_address"] = (string?)instance["address"];
            sent["target"] = target.Name;
            sent["gateway"] = target.Url;
            return sent;
        }
        step($"Published {name} to {target.Url}.");
        var published = await Call(HttpMethod.Put, $"apps/{Uri.EscapeDataString(name)}", $"the app {name}");
        published["app"] = name;
        published["target"] = target.Name;
        published["gateway"] = target.Url;
        return published;
    }
}

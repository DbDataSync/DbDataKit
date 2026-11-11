using System.Text.Json;

namespace DbDataKit.Update;

/// <summary>GitHub Releases. Expects each release to carry assets <c>release.json</c> and <c>release.json.sig</c>. Draft releases are ignored.</summary>
public sealed class GitHubReleaseSource(string owner, string repo, HttpClient http, string apiBase = "https://api.github.com") : IReleaseSource
{
    public string Name => $"github:{owner}/{repo}";

    public async Task<IReadOnlyList<ReleaseRef>> ListAsync(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{apiBase.TrimEnd('/')}/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/releases?per_page=50");
        req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
        req.Headers.TryAddWithoutValidation("User-Agent", "DbDataKit.Update");
        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
        var list = new List<ReleaseRef>();
        foreach (var rel in doc.RootElement.EnumerateArray())
        {
            if (rel.TryGetProperty("draft", out var d) && d.GetBoolean()) continue;
            var tag = rel.GetProperty("tag_name").GetString();
            if (!ReleaseVersion.TryParse(tag, out var v)) continue;
            Uri? man = null, sig = null;
            if (rel.TryGetProperty("assets", out var assets))
                foreach (var a in assets.EnumerateArray())
                {
                    var name = a.GetProperty("name").GetString();
                    var url = a.GetProperty("browser_download_url").GetString();
                    if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var u)) continue;
                    if (name == "release.json") man = u;
                    else if (name == "release.json.sig") sig = u;
                }
            if (man is not null && sig is not null) list.Add(new ReleaseRef(v.ToString(), man, sig, Name));
        }
        return list;
    }
}

/// <summary>NuGet v3 flat container (<c>{base}/{id}/index.json</c>). Lists versions only; NuGet offers no signed manifest, so refs are unsigned hints (Notify only).</summary>
public sealed class NuGetFlatContainerSource(string packageId, HttpClient http, string baseUrl = "https://api.nuget.org/v3-flatcontainer") : IReleaseSource
{
    public string Name => $"nuget:{packageId}";

    public async Task<IReadOnlyList<ReleaseRef>> ListAsync(CancellationToken ct)
    {
        var url = $"{baseUrl.TrimEnd('/')}/{packageId.ToLowerInvariant()}/index.json";
        using var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
        return doc.RootElement.GetProperty("versions").EnumerateArray()
            .Select(e => e.GetString()).Where(s => ReleaseVersion.TryParse(s, out _))
            .Select(s => new ReleaseRef(ReleaseVersion.Parse(s!).ToString(), null, null, Name)).ToList();
    }
}

/// <summary>Folder or UNC share (air-gapped). Layout: <c>&lt;root&gt;/&lt;version&gt;/release.json[.sig]</c>.</summary>
public sealed class FolderReleaseSource(string root) : IReleaseSource
{
    public string Name => $"folder:{root}";

    public Task<IReadOnlyList<ReleaseRef>> ListAsync(CancellationToken ct)
    {
        var list = new List<ReleaseRef>();
        if (Directory.Exists(root))
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                var man = Path.Combine(dir, "release.json");
                var sig = man + ".sig";
                if (!ReleaseVersion.TryParse(Path.GetFileName(dir), out var v) || !File.Exists(man) || !File.Exists(sig)) continue;
                list.Add(new ReleaseRef(v.ToString(), new Uri(Path.GetFullPath(man)), new Uri(Path.GetFullPath(sig)), Name));
            }
        return Task.FromResult<IReadOnlyList<ReleaseRef>>(list);
    }
}

/// <summary>A single well-known manifest URL ("latest"); signature at <c>url + ".sig"</c>. Version is learned only after verification.</summary>
public sealed class UrlReleaseSource(Uri manifestUrl) : IReleaseSource
{
    public string Name => $"url:{manifestUrl}";

    public Task<IReadOnlyList<ReleaseRef>> ListAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<ReleaseRef>>([new ReleaseRef("", manifestUrl, new Uri(manifestUrl.AbsoluteUri + ".sig"), Name)]);
}

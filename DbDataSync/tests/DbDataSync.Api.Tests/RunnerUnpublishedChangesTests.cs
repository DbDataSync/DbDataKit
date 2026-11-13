using System.Net.Http.Json;
using System.Text.Json;
using DbDataSync.Api.Configuration;
using DbDataSync.Libraries;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DbDataSync.Api.Tests;

/// <summary>Plan 28c E12g (E22 precondition P9) over the Sync API: <c>GET /api/runner/package/unpublished</c> says whether the repository changed since the last publish to the linked package and channel, and which replications.</summary>
public sealed class RunnerUnpublishedChangesTests(RunnerOwnedApiFactory factory) : IClassFixture<RunnerOwnedApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public async Task A_change_after_a_publish_shows_with_its_replication_and_a_new_publish_clears_it()
    {
        var repo = factory.Services.GetRequiredService<ApiOptions>().RepoRoot;
        factory.RunnerOwns("any-" + Guid.NewGuid().ToString("N")[..6]);     // sets the link: server runner-1, channel prod, package sales-set
        var never = (await _client.GetFromJsonAsync<PublishState>("/api/runner/package/unpublished", Json))!;

        PublishedDefinition.Record(repo, new PublishedMarker("sales-set", "prod", "runner-1", 7, "stage", DateTimeOffset.UtcNow, DefinitionDigest.Of(repo)));
        var clean = (await _client.GetFromJsonAsync<PublishState>("/api/runner/package/unpublished", Json))!;
        var folder = Path.Combine(repo, "config", "replications", "edited-after-publish");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "task.yaml"), "name: edited-after-publish\n");
        var changed = (await _client.GetFromJsonAsync<PublishState>("/api/runner/package/unpublished", Json))!;
        PublishedDefinition.Record(repo, new PublishedMarker("sales-set", "prod", "runner-1", 8, "stage", DateTimeOffset.UtcNow, DefinitionDigest.Of(repo)));
        var again = (await _client.GetFromJsonAsync<PublishState>("/api/runner/package/unpublished", Json))!;

        Assert.False(never.EverPublished);
        Assert.Equal((true, false, 7), (clean.EverPublished, clean.Unpublished, clean.Version));
        Assert.True(changed.Unpublished);
        Assert.Equal(["edited-after-publish"], changed.Replications);
        Assert.Equal((false, 8), (again.Unpublished, again.Version));
    }
}

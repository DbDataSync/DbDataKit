using DbDataKit.Web.Trust;
using Xunit;

namespace DbDataKit.Web.Tests;

public class WriteGuardTests
{
    /// <summary>A file-like thing whose version is a function of its content, read from the thing itself every time.</summary>
    private sealed class Document
    {
        public string? Content;
        public string? Version => Content is null ? null : "v" + Content.Length + Content.GetHashCode();
    }

    private static Task<WriteResult<string>> Write(WriteGuard guard, Document doc, string? expected, string content, Func<Task>? slow = null) =>
        guard.RunAsync("doc", expected, () => Task.FromResult(doc.Version), async () => { if (slow is not null) await slow(); doc.Content = content; return content; });

    [Fact]
    public async Task A_write_based_on_the_current_version_is_applied()
    {
        var doc = new Document { Content = "one" };

        var result = await Write(new WriteGuard(), doc, doc.Version, "two");

        Assert.True(result.Applied);
        Assert.Equal("two", doc.Content);
    }

    [Fact]
    public async Task Two_concurrent_writes_from_the_same_version_apply_once_and_the_second_is_refused_with_the_current_version()
    {
        var doc = new Document { Content = "one" };
        var guard = new WriteGuard();
        var read = doc.Version;
        var firstHasStarted = new TaskCompletionSource();
        var releaseFirst = new TaskCompletionSource();

        // the first write is slow, so the second arrives while it holds the key
        var first = Write(guard, doc, read, "from-first", async () => { firstHasStarted.SetResult(); await releaseFirst.Task; });
        await firstHasStarted.Task;
        var second = Write(guard, doc, read, "from-second");
        releaseFirst.SetResult();

        var results = await Task.WhenAll(first, second);

        Assert.True(results[0].Applied);
        Assert.False(results[1].Applied);
        Assert.Equal(doc.Version, results[1].CurrentVersion);
        Assert.Equal("from-first", doc.Content);
    }

    [Fact]
    public async Task A_change_made_outside_the_guard_is_caught_because_the_version_is_read_from_the_thing()
    {
        var doc = new Document { Content = "one" };
        var read = doc.Version;
        doc.Content = "edited by hand";

        var result = await Write(new WriteGuard(), doc, read, "mine");

        Assert.False(result.Applied);
        Assert.Equal(doc.Version, result.CurrentVersion);
        Assert.Equal("edited by hand", doc.Content);
    }

    [Fact]
    public async Task Creating_holds_only_when_nothing_is_there()
    {
        var doc = new Document();
        var guard = new WriteGuard();

        Assert.True((await Write(guard, doc, null, "new")).Applied);
        var again = await Write(guard, doc, null, "newer");

        Assert.False(again.Applied);
        Assert.Equal(doc.Version, again.CurrentVersion);
        Assert.Equal("new", doc.Content);
    }

    [Fact]
    public async Task Writing_to_something_that_has_gone_is_refused_with_no_current_version()
    {
        var doc = new Document();

        var result = await Write(new WriteGuard(), doc, "v-old", "x");

        Assert.False(result.Applied);
        Assert.Null(result.CurrentVersion);
    }

    [Fact]
    public async Task Different_keys_do_not_wait_for_each_other()
    {
        var guard = new WriteGuard();
        var release = new TaskCompletionSource();
        var started = new TaskCompletionSource();
        var slow = guard.RunAsync("a", null, () => Task.FromResult<string?>(null), async () => { started.SetResult(); await release.Task; return 1; });
        await started.Task;

        var other = await guard.RunAsync("b", null, () => Task.FromResult<string?>(null), () => Task.FromResult(2)).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(other.Applied);
        release.SetResult();
        await slow;
    }

    [Fact]
    public async Task The_throwing_form_carries_the_version_to_read_again()
    {
        var ex = await Assert.ThrowsAsync<StaleWriteException>(() =>
            new WriteGuard().RunOrThrowAsync("k", "old", () => Task.FromResult<string?>("new"), () => Task.FromResult(0)));

        Assert.Equal("new", ex.CurrentVersion);
        Assert.Contains("new", ex.Message);
    }
}

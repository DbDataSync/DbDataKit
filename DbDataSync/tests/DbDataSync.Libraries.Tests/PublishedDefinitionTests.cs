using DbDataSync.Libraries;
using Xunit;

namespace DbDataSync.Libraries.Tests;

/// <summary>Plan 28c E12g (E22 precondition P9): the record of what was last published and the comparison of the working tree with it, which the screens show as "unpublished changes".</summary>
public sealed class PublishedDefinitionTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dbdatasync-published-").FullName;

    public PublishedDefinitionTests()
    {
        Write("config/connections/src/connection.yaml", "name: src\n");
        Write("config/replications/orders/task.yaml", "name: orders\n");
        Write("config/replications/orders/table-mappings/t1.yaml", "name: t1\n");
        Write("config/replications/events/task.yaml", "name: events\n");
        Write("libraries/npgsql/library.csx", "#r \"nuget: Npgsql, 9.0.3\"\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* temp */ }
    }

    private void Write(string relative, string text)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private void Publish(string package = "sales", string channel = "prod") =>
        PublishedDefinition.Record(_root, new PublishedMarker(package, channel, "runner-1", 3, "stage", DateTimeOffset.UtcNow, DefinitionDigest.Of(_root)));

    [Fact]
    public void Nothing_published_from_here_is_not_unpublished_changes_it_is_nothing_to_show()
    {
        var state = PublishedDefinition.StateOf(_root, "sales", "prod");

        Assert.False(state.EverPublished);
        Assert.False(state.Unpublished);
    }

    [Fact]
    public void A_tree_as_it_was_published_has_no_changes_and_names_the_version()
    {
        Publish();

        var state = PublishedDefinition.StateOf(_root, "sales", "prod");

        Assert.Equal((true, false, false, 3, "stage"), (state.EverPublished, state.Unpublished, state.SharedChanged, state.Version, state.Mode));
        Assert.Empty(state.Replications);
    }

    [Fact]
    public void An_edit_of_one_replication_names_that_replication_only_and_a_shared_edit_says_shared()
    {
        Publish();
        Write("config/replications/orders/table-mappings/t1.yaml", "name: t1\ncolumns: changed\n");

        var one = PublishedDefinition.StateOf(_root, "sales", "prod");

        Assert.True(one.Unpublished);
        Assert.Equal(["orders"], one.Replications);
        Assert.False(one.SharedChanged);

        Write("config/connections/src/connection.yaml", "name: src\nhost: other\n");
        var shared = PublishedDefinition.StateOf(_root, "sales", "prod");
        Assert.True(shared.SharedChanged);
    }

    [Fact]
    public void A_replication_added_or_removed_is_a_change_and_publishing_again_clears_it()
    {
        Publish();
        Write("config/replications/newone/task.yaml", "name: newone\n");
        File.Delete(Path.Combine(_root, "config/replications/events/task.yaml"));

        Assert.Equal(["events", "newone"], PublishedDefinition.StateOf(_root, "sales", "prod").Replications);

        Publish();
        Assert.False(PublishedDefinition.StateOf(_root, "sales", "prod").Unpublished);
    }

    [Fact]
    public void A_publish_to_another_package_or_channel_is_not_this_ones_and_line_endings_and_local_files_are_not_changes()
    {
        Publish();
        Assert.False(PublishedDefinition.StateOf(_root, "other", "prod").EverPublished);
        Assert.False(PublishedDefinition.StateOf(_root, "sales", "dev").EverPublished);

        File.WriteAllText(Path.Combine(_root, "config/replications/orders/task.yaml"), "name: orders\r\n");     // a checkout's line endings
        Write("config/secrets/key.txt", "never published");                                                        // local to the machine
        Write("libraries/npgsql/lib/Npgsql.dll", "rebuilt per platform");

        Assert.False(PublishedDefinition.StateOf(_root, "sales", "prod").Unpublished);
    }

    [Fact]
    public void A_marker_that_does_not_read_counts_as_never_published()
    {
        Publish();
        File.WriteAllText(PublishedDefinition.PathOf(_root), "{ not json");

        Assert.False(PublishedDefinition.StateOf(_root, "sales", "prod").EverPublished);
    }
}

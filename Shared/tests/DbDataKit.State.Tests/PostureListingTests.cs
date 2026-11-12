using DbDataKit.State;

namespace DbDataKit.State.Tests;

/// <summary>Plan 27a IX25: the combined, read-only list of the posture choices of the console's table and the Runners' (<see cref="PostureListing"/>).</summary>
public sealed class PostureListingTests
{
    private const string Setting = "production_binding_on_non_production";
    private static PostureChoice Choice(string scope, string by, int day, string setting = Setting, string? note = null) => new(scope, setting, by, new DateTimeOffset(2026, 11, day, 9, 0, 0, TimeSpan.Zero), note);

    [Fact]
    public void A_choice_held_by_two_tables_is_listed_once_naming_both_and_showing_the_first_record()
    {
        var list = PostureListing.Merge(
        [
            new("console", Choice("bindings/runner/orders/source/dev", "cy", 12)),
            new("runner-a", Choice("bindings/runner/orders/source/dev", "bo", 11)),
            new("runner-b", Choice("bindings/runner/orders/source/dev", "di", 13)),
            new("console", Choice("bindings/delivery/alerts/test", "ada", 10)),
        ]);
        Assert.Equal(["bindings/delivery/alerts/test", "bindings/runner/orders/source/dev"], list.Select(e => e.Scope));
        var once = list[1];
        Assert.Equal(["console", "runner-a", "runner-b"], once.HeldBy);
        Assert.Equal("bo", once.ChosenBy);
        Assert.Contains("that production uses", once.Effect);
        Assert.NotNull(once.Default);
    }

    [Fact]
    public void The_same_scope_under_another_setting_is_another_choice_and_an_unknown_setting_is_listed_without_words()
    {
        var list = PostureListing.Merge(
        [
            new("console", Choice("*", "ada", 10, "run_in_process")),
            new("runner-a", Choice("*", "bo", 11, "longer_limits")),
            new("runner-a", Choice("*", "bo", 11, "from_a_newer_runner", "note")),
        ]);
        Assert.Equal(3, list.Count);
        Assert.Null(list.Single(e => e.Setting == "from_a_newer_runner").Effect);
        Assert.Equal("note", list.Single(e => e.Setting == "from_a_newer_runner").Note);
        Assert.Empty(PostureListing.Merge([]));
    }
}

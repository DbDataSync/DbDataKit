using DbDataKit.Connections.Bindings;

namespace DbDataKit.Connections.Abstractions.Tests;

/// <summary>Plan 27a IX25: the value a group's binding row keeps (<see cref="BindingValueText"/>) and how two sides' values compare (<see cref="BindingCompare"/>).</summary>
public sealed class BindingCompareTests
{
    [Fact]
    public void A_binding_is_one_short_text_and_reads_back_as_the_same_choices()
    {
        BindingEntry[] entries = [new("claude", "claude-sonnet-4-5"), new("local", "llama3.1:8b")];
        var text = BindingValueText.Encode(entries);
        Assert.Equal("claude/claude-sonnet-4-5, local/llama3.1:8b", text);
        var back = BindingValueText.Decode(text, wantsModel: true, most: 8, out var problem);
        Assert.Null(problem);
        Assert.Equal(entries.Select(e => (e.Connection, e.Model)), back!.Select(e => (e.Connection, e.Model)));
        Assert.Equal("slack-ops", BindingValueText.Encode([new BindingEntry("slack-ops")]));
        Assert.Equal("", BindingValueText.Encode([]));
    }

    [Theory]
    [InlineData("", true, "no value")]
    [InlineData("claude", true, "names no model")]
    [InlineData("claude/model", false, "names a model")]
    [InlineData("a b/model", true, "connection's name")]
    [InlineData("a/m, b/n", false, "names a model")]
    public void A_value_that_is_not_a_choice_of_the_slot_is_refused_in_words(string text, bool wantsModel, string words)
    {
        Assert.Null(BindingValueText.Decode(text, wantsModel, 8, out var problem));
        Assert.Contains(words, problem, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_slot_takes_no_more_choices_than_its_kind_allows()
    {
        Assert.Null(BindingValueText.Decode("a, b", wantsModel: false, most: 1, out var problem));
        Assert.Contains("one choice", problem);
        Assert.NotNull(BindingValueText.Decode("a", wantsModel: false, most: 1, out _));
    }

    [Fact]
    public void Two_values_compare_as_same_differs_or_held_by_one_side_only()
    {
        Assert.Equal(CompareState.Same, BindingCompare.Of("a", "a"));
        Assert.Equal(CompareState.Same, BindingCompare.Of(null, null));
        Assert.Equal(CompareState.Differs, BindingCompare.Of("a", "b"));
        Assert.Equal(CompareState.OnlyHere, BindingCompare.Of("a", null));
        Assert.Equal(CompareState.OnlyThere, BindingCompare.Of(null, "b"));
    }

    [Fact]
    public void A_slot_of_another_server_stands_for_a_console_slot_only_by_the_registrys_names()
    {
        Assert.Equal(("models", SlotKind.ModelChat, "default"), BindingCompare.ConsoleSlotOf("model:default"));
        Assert.Equal(("models", SlotKind.ModelEmbeddings, "embeddings"), BindingCompare.ConsoleSlotOf("model:embeddings"));
        Assert.Equal(("delivery", SlotKind.Delivery, "alerts"), BindingCompare.ConsoleSlotOf("delivery:alerts"));
        Assert.Null(BindingCompare.ConsoleSlotOf("source"));
        Assert.Null(BindingCompare.ConsoleSlotOf("model:"));
        Assert.Equal("model:default", BindingCompare.SlotNameOf(SlotKind.ModelChat, "default"));
        Assert.Equal("delivery:alerts", BindingCompare.SlotNameOf(SlotKind.Delivery, "alerts"));
    }
}

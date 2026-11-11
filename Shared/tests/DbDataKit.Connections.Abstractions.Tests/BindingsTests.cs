using DbDataKit.Config.Yaml;
using DbDataKit.Connections.Bindings;

namespace DbDataKit.Connections.Abstractions.Tests;

/// <summary>
/// Plan 27a IX23: one reader, checker, writer and resolver of <c>bindings/1</c> for every slot that binds a name to a connection per release channel (model slots and delivery channels). The one rule: own entry, else the written <c>*</c>, else unbound.
/// </summary>
public sealed class BindingsTests
{
    private const string Full = """
        bindings: 1
        models:
          default:
            prod: [ { connection: anthropic-main, model: claude-sonnet }, { connection: ollama-local, model: "llama3.1:8b" } ]
            "*": [ { connection: ollama-local, model: "llama3.1:8b" } ]
          embeddings:
            prod: { connection: ollama-embed, model: nomic-embed-text }
        delivery:
          mail: smtp-main
          alerts: { prod: slack-ops, test: slack-sandbox }
        """;

    private static BindingSet Read(string yaml) { var r = BindingsLoader.Load(yaml, "bindings.yml"); Assert.True(r.Ok, string.Join("\n", r.Diagnostics)); return r.Bindings!; }

    private static ConfigDiagnostic Refused(string yaml, string code)
    {
        var r = BindingsLoader.Load(yaml, "bindings.yml");
        Assert.False(r.Ok);
        return Assert.Single(r.Diagnostics, d => d.Code == code);
    }

    private static readonly IBindingTargets Targets = new FixedBindingTargets(
    [
        new("anthropic-main", ConnectionKinds.ModelProvider, "chat", ["claude-sonnet"]),
        new("ollama-local", ConnectionKinds.ModelProvider, "chat", ["llama3.1:8b"]),
        new("ollama-embed", ConnectionKinds.ModelProvider, "embeddings", ["nomic-embed-text"]),
        new("smtp-main", ConnectionKinds.Smtp, CanSend: true),
        new("slack-ops", ConnectionKinds.Http, CanSend: true),
        new("slack-sandbox", ConnectionKinds.Http, CanSend: true),
        new("plain-api", ConnectionKinds.Http),
    ]);

    [Fact]
    public void A_file_reads_into_slots_of_three_kinds_with_the_kind_taken_from_where_it_is_written()
    {
        var set = Read(Full);
        Assert.Equal(SlotKind.ModelChat, set.Find(SlotKind.ModelChat, "default")!.Kind);
        Assert.Equal(SlotKind.ModelEmbeddings, set.Find(SlotKind.ModelEmbeddings, "embeddings")!.Kind);
        Assert.Equal(["mail", "alerts"], set.OfKind(SlotKind.Delivery).Select(s => s.Name));
        var primary = set.Resolve(SlotKind.ModelChat, "default", "prod");
        Assert.Equal(["anthropic-main", "ollama-local"], primary.Entries.Select(e => e.Connection));
        Assert.Equal("llama3.1:8b", primary.Entries[1].Model);
        Assert.True(primary.Entries[0].Line > 0);
    }

    [Fact]
    public void A_release_channel_with_an_entry_of_its_own_uses_it_and_one_without_takes_the_star_entry_only_when_it_is_written()
    {
        var set = Read(Full);
        var own = set.Resolve(SlotKind.ModelChat, "default", "prod");
        Assert.False(own.IsWildcard);
        var viaStar = set.Resolve(SlotKind.ModelChat, "default", "preview");
        Assert.True(viaStar.IsWildcard);
        Assert.Equal("ollama-local", viaStar.Primary!.Connection);

        // delivery has no star: test is bound, dev is not, and dev does not get prod's Slack
        var dev = set.Resolve(SlotKind.Delivery, "alerts", "dev");
        Assert.False(dev.IsBound);
        Assert.Contains("'alerts'", dev.Unbound, StringComparison.Ordinal);
        Assert.Contains("'dev'", dev.Unbound, StringComparison.Ordinal);
        Assert.Contains("prod, test", dev.Unbound, StringComparison.Ordinal);
        Assert.Equal("slack-sandbox", set.Resolve(SlotKind.Delivery, "alerts", "test").Primary!.Connection);
    }

    [Fact]
    public void A_name_alone_binds_production_only_and_an_absent_release_channel_is_production()
    {
        var set = Read(Full);
        Assert.Equal("smtp-main", set.Resolve(SlotKind.Delivery, "mail", null).Primary!.Connection);
        Assert.Equal("smtp-main", set.Resolve(SlotKind.Delivery, "mail", " ").Primary!.Connection);
        Assert.False(set.Resolve(SlotKind.Delivery, "mail", "test").IsBound);
    }

    [Fact]
    public void A_slot_that_is_not_in_the_file_is_unbound_in_words_and_a_promotion_names_what_is_missing()
    {
        var set = Read(Full);
        var none = set.Resolve(SlotKind.ModelChat, "review", "prod");
        Assert.False(none.IsBound);
        Assert.Contains("bound nowhere", none.Unbound, StringComparison.Ordinal);
        Assert.Equal(["review"], set.Unbound(SlotKind.ModelChat, ["default", "review", "review"], "prod"));
        Assert.Equal(["alerts", "mail"], set.Unbound(SlotKind.Delivery, ["mail", "alerts"], "dev"));
    }

    [Fact]
    public void An_empty_file_binds_nothing_and_a_write_reads_back_as_it_was()
    {
        Assert.Empty(Read("").Slots);
        var set = Read(Full);
        var again = Read(BindingsWriter.Write(set));
        Assert.Equal(BindingsWriter.Write(set), BindingsWriter.Write(again));
        Assert.Equal(set.Resolve(SlotKind.ModelChat, "default", "prod").Entries.Select(e => (e.Connection, e.Model)), again.Resolve(SlotKind.ModelChat, "default", "prod").Entries.Select(e => (e.Connection, e.Model)));
        Assert.Equal("slack-sandbox", again.Resolve(SlotKind.Delivery, "alerts", "test").Primary!.Connection);
        Assert.Contains("\"mail\": \"smtp-main\"", BindingsWriter.Write(set), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("bindings: 1\nmodels:\n  default:\n    prod: [ { connection: a, mode: x, model: m } ]\n")]                         // unknown key
    [InlineData("bindings: 1\nmodels:\n  default:\n    prod: [ { connection: a } ]\n")]                                              // a model slot needs a model
    [InlineData("bindings: 1\nmodels:\n  default:\n    prod: a\n")]                                                                // a bare name for a model slot
    [InlineData("bindings: 1\nmodels:\n  embeddings:\n    prod: [ { connection: a, model: m }, { connection: b, model: n } ]\n")]   // one model for an index
    [InlineData("bindings: 1\ndelivery:\n  alerts: { prod: [ a, b ] }\n")]                                                          // one connection per delivery channel
    [InlineData("bindings: 1\ndelivery:\n  alerts: { prod: { connection: a, model: m } }\n")]                                      // no model for a delivery channel
    [InlineData("bindings: 1\ndelivery:\n  alerts: { \"pre view\": a }\n")]                                                         // bad release channel name
    [InlineData("bindings: 1\ndelivery:\n  alerts: { prod: 1bad }\n")]                                                              // bad connection name
    [InlineData("bindings: 1\ndelivery:\n  alerts: {}\n")]                                                                          // bound nowhere
    [InlineData("bindings: 1\nmodels:\n  default: []\n")]                                                                           // an empty list
    [InlineData("bindings: 1\nsurprise: 1\n")]                                                                                      // unknown top-level key
    public void A_value_that_does_not_fit_is_refused_with_the_file_and_a_line(string yaml)
    {
        var r = BindingsLoader.Load(yaml, "bindings.yml");
        Assert.False(r.Ok);
        Assert.All(r.Diagnostics.Where(d => d.Severity == ConfigSeverity.Error), d => { Assert.Equal("bindings.yml", d.Path); Assert.True(d.Line > 0, d.ToString()); });
    }

    [Fact]
    public void The_earlier_forms_are_refused_naming_the_file_and_the_new_form()
    {
        var slots = Refused("model-slots: 1\nslots:\n  default:\n    prod: [ { provider: a, model: m } ]\n", BindingsLoader.EarlierForm);
        Assert.Equal("bindings.yml", slots.Path);
        Assert.Contains("bindings: 1", slots.Fix, StringComparison.Ordinal);
        var provider = Refused("bindings: 1\nmodels:\n  default:\n    prod: [ { provider: a, model: m } ]\n", BindingsLoader.EarlierForm);
        Assert.Contains("`connection`", provider.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("bindings: 2\n", "reads format 1")]
    [InlineData("bindings: 0\n", "positive")]
    [InlineData("models: {}\n", "bindings: 1")]
    public void The_format_number_is_required_and_a_newer_one_is_refused_in_words(string yaml, string says)
    {
        var d = Refused(yaml, BindingsLoader.FormatNumber);
        Assert.Contains(says, d.Message + d.Fix, StringComparison.Ordinal);
    }

    [Fact]
    public void The_check_names_a_connection_that_is_missing_of_the_wrong_kind_or_purpose_or_without_that_model()
    {
        var set = Read("""
            bindings: 1
            models:
              default:
                prod: [ { connection: ghost, model: m }, { connection: smtp-main, model: m }, { connection: ollama-embed, model: nomic-embed-text }, { connection: ollama-local, model: nope } ]
              embeddings:
                prod: { connection: ollama-local, model: "llama3.1:8b" }
            delivery:
              alerts: { prod: plain-api, test: slack-sandbox }
            """);
        var problems = BindingsLoader.Check(set, Targets, "bindings.yml");
        Assert.Equal(6, problems.Count);
        Assert.All(problems, p => { Assert.Equal(BindingsLoader.UnknownChoice, p.Code); Assert.True(p.Line > 0); });
        Assert.Contains(problems, p => p.Message.Contains("`ghost`", StringComparison.Ordinal) && p.Fix!.Contains("anthropic-main", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Message.Contains("not a model provider", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Message.Contains("for embeddings, not chat", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Message.Contains("`nope`", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Message.Contains("for chat, not embeddings", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Message.Contains("cannot send", StringComparison.Ordinal));
        Assert.Empty(BindingsLoader.Check(Read(Full), Targets, "bindings.yml"));
    }

    [Fact]
    public void A_set_is_changed_by_replacing_or_removing_a_slot()
    {
        var set = Read(Full);
        var changed = set.With(new SlotBinding(SlotKind.Delivery, "mail", new Dictionary<string, IReadOnlyList<BindingEntry>> { ["prod"] = [new("smtp-main")], ["test"] = [new("smtp-main")] }));
        Assert.True(changed.Resolve(SlotKind.Delivery, "mail", "test").IsBound);
        Assert.False(changed.With(new SlotBinding(SlotKind.Delivery, "mail", new Dictionary<string, IReadOnlyList<BindingEntry>>())).Resolve(SlotKind.Delivery, "mail", "prod").IsBound);
        Assert.False(set.Without(SlotKind.Delivery, "alerts").Resolve(SlotKind.Delivery, "alerts", "prod").IsBound);
    }
}

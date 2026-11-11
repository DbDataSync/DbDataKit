using DbDataKit.Agents;
using DbDataKit.Agents.Testing;
using DbDataKit.Cli;
using Microsoft.Extensions.AI;

namespace DbDataKit.Agents.Tests;

/// <summary>Plan 27a AG19: a memory persists across conversations for its person and is invisible to another; deleting removes it from the next turn; a secret is refused; turned off it is neither written nor read.</summary>
public sealed class MemoryTests
{
    private static readonly MemorySwitches On = new(true, true);

    private static AgentRunContext Ctx(string person) => new(person, "helper", "1");

    [Fact]
    public async Task A_memory_written_in_one_conversation_is_given_in_the_next_for_its_person_and_is_invisible_to_another()
    {
        var service = new MemoryService(new InMemoryMemoryStore());
        var tools = service.Tools(Ctx("alice"), On, "ws-1");
        var first = new ScriptedChatClient(ScriptStep.Call("memory_remember", new { text = "Prefers answers as a table.", kind = "person" }), ScriptStep.Say("Noted."));
        await new AgentRunner(first, tools).RunAsync([new ChatMessage(ChatRole.User, "remember I like tables")], Ctx("alice"));

        var mine = service.Inputs("alice", "ws-1", On, 500);
        var theirs = service.Inputs("bob", "ws-1", On, 500);
        Assert.Single(mine);
        Assert.Contains("Prefers answers as a table.", mine[0].Text);
        Assert.Equal("memory", mine[0].Kind);
        Assert.Empty(theirs);

        // it goes through the assembly after the agent's instructions and before references, and is recorded
        var assembled = ContextAssembler.Assemble(ContextInputs.Empty with { AgentInstructions = [ContextSource.Of("instruction", "role.md", "Be brief.")], Memories = mine, References = [ContextSource.Of("reference", "ref1", "A reference.")] });
        var text = assembled.SystemText;
        Assert.True(text.IndexOf("Be brief.", StringComparison.Ordinal) < text.IndexOf("Prefers answers as a table.", StringComparison.Ordinal));
        Assert.True(text.IndexOf("Prefers answers as a table.", StringComparison.Ordinal) < text.IndexOf("A reference.", StringComparison.Ordinal));
        Assert.Contains(assembled.Record, r => r.Layer == "memory" && r.Id == mine[0].Id);
    }

    [Fact]
    public void A_work_memory_belongs_to_the_work_and_a_person_memory_is_only_its_persons_to_edit_and_delete()
    {
        var service = new MemoryService(new InMemoryMemoryStore());
        var work = service.Remember("alice", MemoryKind.Work, "ws-1", "helper", "The sales project uses the retail model.", On);
        var person = service.Remember("alice", MemoryKind.Person, null, "helper", "Speaks German.", On);
        Assert.True(work.Ok && person.Ok);

        // bob opens the same workspace: he sees the work's memory, not alice's own
        var bobSees = service.Visible("bob", "ws-1", On);
        Assert.Equal([work.Memory!.Id], bobSees.Select(m => m.Id));
        Assert.DoesNotContain(service.Visible("bob", "ws-2", On), m => m.Kind == MemoryKind.Work);
        // bob cannot edit or delete alice's own, and cannot learn that it exists
        Assert.Contains("There is no memory", service.Forget(person.Memory!.Id, "bob", "ws-1").Message);
        Assert.Contains("There is no memory", service.Edit(person.Memory.Id, "bob", "ws-1", "x").Message);
        Assert.True(service.Edit(person.Memory.Id, "alice", null, "Speaks German and English.").Ok);
        Assert.Contains("English", service.Visible("alice", null, On).Single(m => m.Kind == MemoryKind.Person).Text);
    }

    [Fact]
    public async Task Deleting_a_memory_removes_it_from_the_next_turn()
    {
        var service = new MemoryService(new InMemoryMemoryStore());
        var remembered = service.Remember("alice", MemoryKind.Person, null, "helper", "Likes tables.", On).Memory!;
        Assert.Single(service.Inputs("alice", null, On, 500));

        var forget = service.Tools(Ctx("alice"), On, null).Single(t => t.Name == "memory_forget");
        var client = new ScriptedChatClient(ScriptStep.Call("memory_forget", new { id = remembered.Id }), ScriptStep.Say("Forgotten."));
        await new AgentRunner(client, [forget]).RunAsync([new ChatMessage(ChatRole.User, "forget that")], Ctx("alice"));

        Assert.Empty(service.Inputs("alice", null, On, 500));
        Assert.Contains("Forgotten", Transcript.ResultTexts(client.Requests[1]).Single());
    }

    [Theory]
    [InlineData("The password is hunter2hunter2")]
    [InlineData("Use key sk-ant-api03-abcdefghijklmnopqrstu")]
    [InlineData("Server=db;Database=x;User Id=sa;Password=Sup3rS3cret!;")]
    [InlineData("Authorization: Bearer abcdefghijklmnopqrstuvwxyz0123")]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----")]
    [InlineData("token = abcd1234efgh")]
    [InlineData("AKIAABCDEFGHIJKLMNOP")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef")]
    public void A_value_that_looks_like_a_secret_is_refused_in_words_and_not_written(string text)
    {
        var store = new InMemoryMemoryStore();
        var result = new MemoryService(store).Remember("alice", MemoryKind.Person, null, "helper", text, On);
        Assert.False(result.Ok);
        Assert.Contains("never holds one", result.Message);
        Assert.Empty(store.All());
    }

    [Fact]
    public void Ordinary_facts_are_remembered_and_a_long_text_is_refused()
    {
        var service = new MemoryService(new InMemoryMemoryStore());
        Assert.True(service.Remember("alice", MemoryKind.Person, null, "helper", "Works on the Sales project; the password policy is in the wiki.", On).Ok);
        Assert.Contains("at most 500", service.Remember("alice", MemoryKind.Person, null, "helper", new string('x', 501), On).Message);
    }

    [Fact]
    public void A_kind_that_is_turned_off_per_agent_or_for_the_install_is_neither_written_nor_read()
    {
        var store = new InMemoryMemoryStore();
        var service = new MemoryService(store);
        service.Remember("alice", MemoryKind.Person, null, "helper", "Likes tables.", On);

        var agentOff = MemorySwitches.For(new AgentMemory(Person: false, Workspace: true), AgentPolicy.None);
        var installOff = MemorySwitches.For(new AgentMemory(Person: true, Workspace: true), AgentPolicy.None with { PersonMemory = false });
        foreach (var switches in new[] { agentOff, installOff })
        {
            Assert.False(switches.Person);
            Assert.Empty(service.Inputs("alice", null, switches, 500));
            Assert.Contains("turned off", service.Remember("alice", MemoryKind.Person, null, "helper", "Another.", switches).Message);
        }
        Assert.Single(store.All());                                   // what was kept stays in the list
        Assert.Empty(service.Tools(Ctx("alice"), MemorySwitches.Off, null));
        var workOnly = service.Tools(Ctx("alice"), new MemorySwitches(false, true), "ws-1");
        Assert.Equal(["work"], workOnly[0].InputSchema["properties"]!["kind"]!["enum"]!.AsArray().Select(n => (string)n!));
    }

    [Fact]
    public void The_room_given_to_memories_is_respected_newest_first_and_the_tools_ask_by_their_own_prompting()
    {
        var clock = new FakeClock();
        var service = new MemoryService(new InMemoryMemoryStore(), clock);
        service.Remember("alice", MemoryKind.Person, null, "h", "old fact one two three four five", On);
        clock.Advance(TimeSpan.FromMinutes(1));
        service.Remember("alice", MemoryKind.Person, null, "h", "new fact", On);
        var fits = service.Inputs("alice", null, On, 12, new WordCounter());
        Assert.Single(fits);
        Assert.Contains("new fact", fits[0].Text);

        var tools = service.Tools(Ctx("alice"), On, null);
        Assert.Equal(Prompting.AskOnce, tools.Single(t => t.Name == "memory_remember").DeclaredPrompting);
        Assert.Equal(Prompting.Ask, tools.Single(t => t.Name == "memory_forget").DeclaredPrompting);
    }

    [Fact]
    public void The_policy_reads_and_writes_the_memory_switches_and_describes_turning_one_off()
    {
        var policy = AgentPolicyLoader.Load("agent-policy: 1\nmemory:\n  person: off\n", "p.yml").Policy!;
        Assert.False(policy.PersonMemory);
        Assert.True(policy.WorkMemory);
        Assert.False(AgentPolicyLoader.Load(PolicyEditing.Write(policy), "p.yml").Policy!.PersonMemory);
        Assert.False(PolicyEditing.Describe(AgentPolicy.None, policy).Single().Loosens);
        Assert.True(PolicyEditing.Describe(policy, AgentPolicy.None).Single().Loosens);
        Assert.Contains(AgentPolicyLoader.Load("agent-policy: 1\nmemory: { person: never }", "p.yml").Diagnostics, d => d.Message.Contains("not a memory setting"));
    }

    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 11, 10, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}

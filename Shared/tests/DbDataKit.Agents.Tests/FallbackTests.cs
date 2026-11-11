using DbDataKit.Agents;
using DbDataKit.Agents.Testing;
using Microsoft.Extensions.AI;

namespace DbDataKit.Agents.Tests;

/// <summary>Plan 27a AG21: a slot bound to an ordered list of models, and a conversation that stays on the agent version it started with.</summary>
public sealed class FallbackTests
{
    private static readonly AgentRunContext Context = new("alice", "build-helper", "3");

    private sealed class FailingClient(Exception failure) : IChatClient
    {
        public int Calls { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            throw failure;
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private static readonly ChatMessage Ask = new(ChatRole.User, "hi");

    private static RetryOptions NoWait(List<TimeSpan> waited) => new(Retries: 2, Delay: (time, _) => { waited.Add(time); return Task.CompletedTask; });

    [Fact]
    public async Task A_failing_primary_is_retried_with_back_off_then_falls_to_the_secondary_and_the_turn_records_it()
    {
        var primary = new FailingClient(new HttpRequestException("503 overloaded"));
        var secondary = new ScriptedChatClient(ScriptStep.Say("from the secondary"));
        var waited = new List<TimeSpan>();
        var recorder = new InMemoryRecorder();
        var client = new FallbackChatClient([new ModelBinding("primary", primary), new ModelBinding("secondary", secondary)], Context, recorder, retry: NoWait(waited));

        var response = await client.GetResponseAsync([Ask]);

        Assert.Equal("from the secondary", response.Text);
        Assert.Equal(3, primary.Calls);                                                    // the first try and two retries
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)], waited);           // the back-off doubles
        Assert.Equal("secondary", client.LastAnswered);
        Assert.Contains(recorder.Events, e => e.Kind == "model-answered-by" && e.Detail == "secondary (fallback)");
        Assert.Equal(3, recorder.Events.Count(e => e.Kind == "model-failed"));
    }

    [Fact]
    public async Task A_primary_that_answers_is_the_only_model_asked()
    {
        var primary = new ScriptedChatClient(ScriptStep.Say("primary"));
        var secondary = new ScriptedChatClient(ScriptStep.Say("secondary"));
        var client = new FallbackChatClient([new ModelBinding("primary", primary), new ModelBinding("secondary", secondary)], Context);

        Assert.Equal("primary", (await client.GetResponseAsync([Ask])).Text);
        Assert.Equal("primary", client.LastAnswered);
        Assert.Empty(secondary.Requests);
    }

    [Fact]
    public async Task A_fallback_the_install_policy_does_not_allow_is_never_tried()
    {
        var primary = new FailingClient(new HttpRequestException("down"));
        var cloud = new ScriptedChatClient(ScriptStep.Say("from the cloud"));
        var local = new ScriptedChatClient(ScriptStep.Say("from the local model"));
        var tenantOnly = (ModelBinding b) => !b.Cloud;                                      // a tenant-only install never falls back to a cloud provider
        var client = new FallbackChatClient([new ModelBinding("tenant", primary), new ModelBinding("cloud", cloud, Cloud: true), new ModelBinding("local", local)], Context, allowedAsFallback: tenantOnly, retry: NoWait([]));

        var response = await client.GetResponseAsync([Ask]);

        Assert.Equal("from the local model", response.Text);
        Assert.Empty(cloud.Requests);                                                       // refused by policy, not just unlucky

        var onlyCloud = new FallbackChatClient([new ModelBinding("tenant", new FailingClient(new HttpRequestException("down"))), new ModelBinding("cloud", cloud, Cloud: true)], Context, allowedAsFallback: tenantOnly, retry: NoWait([]));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => onlyCloud.GetResponseAsync([Ask]));
        Assert.Contains("No model of the slot answered", ex.Message);
        Assert.Empty(cloud.Requests);
    }

    [Fact]
    public async Task A_cancellation_is_not_retried_and_the_streaming_path_answers_too()
    {
        var cancelling = new FailingClient(new OperationCanceledException());
        var client = new FallbackChatClient([new ModelBinding("primary", cancelling), new ModelBinding("secondary", new ScriptedChatClient(ScriptStep.Say("x")))], Context, retry: NoWait([]));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetResponseAsync([Ask]));
        Assert.Equal(1, cancelling.Calls);                                                   // no retry, no fall-through

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in new FallbackChatClient([new ModelBinding("s", new ScriptedChatClient(ScriptStep.Say("streamed")))], Context).GetStreamingResponseAsync([Ask])) updates.Add(update);
        Assert.Equal("streamed", updates.ToChatResponse().Text);
    }

    [Fact]
    public void An_open_conversation_stays_on_the_version_it_started_with_and_is_offered_the_new_one()
    {
        var same = ConversationVersions.For("1", "1");
        var after = ConversationVersions.For("1", "2");

        Assert.Equal(new ConversationVersion("1", null, false, null), same);
        Assert.Equal(("1", "2", false), (after.Version, after.OfferedVersion, after.Ended));      // v1 keeps v1 after v2 activates and is offered v2
        Assert.Equal("2", ConversationVersions.For("2", "2").Version);
    }

    [Fact]
    public void An_administrator_can_end_conversations_on_a_rolled_back_version()
    {
        var rolledBack = new HashSet<string> { "2" };

        var ended = ConversationVersions.For("2", "1", rolledBack);
        var fine = ConversationVersions.For("1", "1", rolledBack);

        Assert.True(ended.Ended);
        Assert.Contains("version 2 of the agent, which was rolled back", ended.Message);
        Assert.Contains("version 1", ended.Message);
        Assert.False(fine.Ended);
    }
}

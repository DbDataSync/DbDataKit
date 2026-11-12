using DbDataKit.Backlog;
using DbDataKit.State;

namespace DbDataKit.Server;

/// <summary>
/// The backlog the console hosts (plan 27a BK03 and BK04's first host; BK02, the serving layer's <c>backlog</c> module, is not built): <c>DbDataKit.Backlog</c> over the console's own <c>backlog</c> state module, with attachments in the payload store. The console has two roles, so
/// an administrator works as a <see cref="BacklogRole.Lead"/> and a viewer as a <see cref="BacklogRole.Viewer"/> (reads, changes nothing); the rules and the history are the library's, so moving the API to the serving layer later changes the host and not the behaviour.
/// </summary>
public sealed class ConsoleBacklog
{
    public ConsoleBacklog(StateDatabase state)
    {
        Store = new BacklogStore(state);
        Service = new BacklogService(Store, payloads: new PayloadStore(state));
    }

    public BacklogStore Store { get; }

    public BacklogService Service { get; }

    /// <summary>The person an act is done as: the signed-in user, with the role the console gives them.</summary>
    public static BacklogPerson PersonOf(string id, string? name, bool admin) => new(id, string.IsNullOrWhiteSpace(name) ? id : name, admin ? BacklogRole.Lead : BacklogRole.Viewer);
}

using DbDataKit.Runner.Kit;

namespace DbDataKit.Runner.Protocol.Server;

/// <summary>A connection slot has no binding in the channel, or a binding the runner cannot turn into a login. The message names the slot and the channel.</summary>
public class SlotResolutionException(string message, Exception? inner = null) : InvalidOperationException(message, inner);

/// <summary>
/// How the runner turns a connection slot into a login (plan 28c section 6, E02). The host that owns the bindings and the secret store implements it; the protocol host only asks. Three questions: which connection does
/// each slot of a task name in its channel (<see cref="Handles"/>, answered when a task starts, so an unbound slot stops the run before a process is made), what environment does the task get for the slots that ask for injection
/// (<see cref="EnvironmentAsync"/>), and what does a task get when it asks for a slot (<see cref="ResolveAsync"/>).
/// </summary>
public interface ISlotResolver
{
    /// <summary>Slot to the name of the connection the channel binds it to, for the slots the task declares. Throws <see cref="SlotResolutionException"/> naming the slot and the channel when one is not bound.</summary>
    IReadOnlyDictionary<string, string> Handles(TaskRef task, IReadOnlyList<ConnectionSlot> slots);

    /// <summary>The variables to put in the task's environment for the slots that declare an injection form (<see cref="ConnectionSlot.Env"/>); empty when none does. The values are secrets: the caller registers them with the redactor.</summary>
    Task<IReadOnlyDictionary<string, string>> EnvironmentAsync(IReadOnlyDictionary<string, string> handles, IReadOnlyList<ConnectionSlot> slots, CancellationToken ct);

    /// <summary>The connection and login for one slot of a started run; <paramref name="role"/> is <c>default</c>, <c>read</c> or <c>write</c>.</summary>
    Task<ConnectionGrant> ResolveAsync(string slot, string connection, string role, CancellationToken ct);
}

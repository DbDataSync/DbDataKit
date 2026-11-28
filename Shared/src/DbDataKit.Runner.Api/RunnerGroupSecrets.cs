using System.Runtime.CompilerServices;
using DbDataKit.Secrets;
using DbDataKit.State;
using DbDataKit.Web.Trust;

namespace DbDataKit.Runner.Api;

/// <summary>
/// The secrets of a runner group (plan 28c E20d, owner decision 2026-11-15): a login sent to any instance of the group (<c>PUT /api/v1/secrets/{name}</c>) is kept in the state the group shares, encrypted with the group's key
/// (<see cref="GroupKey"/>, the suite's shared group key), so every instance resolves it. The key is not made on an instance: the console makes it and hands it to each instance when it joins the instance to the group
/// (<c>PUT /api/v1/group/key</c>, <see cref="GroupKeyApi"/>), and the instance keeps it in its own encrypted store under <see cref="KeySecret"/>.
/// <list type="bullet">
/// <item><b>Writing.</b> When the group has a key, a value goes to the group (an instance that does not hold the key is refused, naming it); when it has none (a runner never joined by a console), to the instance's own store, as before.</item>
/// <item><b>Reading.</b> The group's value first, then the instance's own store, so what an instance already held keeps resolving. A value only the group holds, on an instance without the key, is not found, and the words name
/// the missing key. The environment and the credentials directory still come before both (<see cref="Provider"/> is the last link of the chain).</item>
/// <item><b>Listing and removing</b> cover both.</item>
/// </list>
/// </summary>
/// <param name="state">The group's state.</param>
/// <param name="local">The instance's own encrypted store, opened when a call needs it (it holds the group's key, and what was sent before the group had one).</param>
/// <param name="localReader">The same store as a provider that answers "no value" without opening it when nothing was ever stored.</param>
/// <param name="localExists">Whether the instance's own store has a file yet (so a lookup creates no key ring).</param>
public sealed class RunnerGroupSecrets(StateDatabase state, Func<ISecretStore> local, ISecretProvider localReader, Func<bool> localExists)
{
    /// <summary>The secret that holds the group's key in each instance's own store.</summary>
    public const string KeySecret = "dbdatakit:runner:state:group-key";

    public const string CheckName = "runner-key";

    /// <summary>The audit verb of an instance given the group's key (or refused it).</summary>
    public const string JoinVerb = "runner.group.join";

    /// <summary>The runner group's key: made by the console, handed to each instance it joins.</summary>
    public static GroupKeySpec Spec { get; } = new(KeySecret, CheckName, "dbdatarunner group key", MakeWhenMissing: false,
        Missing: () => $"This runner instance has not been joined to its group: the group's key {KeySecret} is not in its store. Join it from the console (Servers, the runner, Join an instance), which hands it the key.",
        WrongKey: () => $"The group's secrets are protected with another key than the one in {KeySecret} (or the one sent). Join the instance again from the console that manages the group; a key copied by hand from another group does not open them.",
        NotAKey: reason => $"The secret {KeySecret} is not a key: {reason}");

    private readonly GroupKey _key = new(state, local, Spec);

    private bool LocalExists => localExists();

    private ISecretStore Local => local();

    public GroupKey Key => _key;

    /// <summary>Whether this instance holds the group's key; false without opening a store that was never written.</summary>
    public bool Held => localExists() && _key.Held;

    /// <summary>What <c>PUT /api/v1/secrets</c> writes to and lists from.</summary>
    public ISecretStore Store => new Composite(this);

    /// <summary>The last link of the chain a connection's references resolve against.</summary>
    public ISecretProvider Provider => new Composite(this);

    private GroupStateSecretStore Values => _values ??= new GroupStateSecretStore(() => state, () => _key, SecretName.Root + ":", storeName: "runner-group");

    private GroupStateSecretStore? _values;

    /// <summary>The names held, the group's and the instance's own, with the date the own store recorded (the group's carry none).</summary>
    public async Task<IReadOnlyList<SecretHeld>> ListHeldAsync(Func<ISecretStore, CancellationToken, Task<IReadOnlyList<SecretHeld>>> own, CancellationToken ct)
    {
        var held = new Dictionary<string, SecretHeld>(StringComparer.Ordinal);
        if (localExists())
            foreach (var item in await own(local(), ct))
                if (item.Name.ToString() != KeySecret) held[item.Name.ToString()] = item;
        await foreach (var name in Values.ListAsync("", ct)) held.TryAdd(name.ToString(), new SecretHeld(name, null));
        return [.. held.Values.OrderBy(h => h.Name.ToString(), StringComparer.Ordinal)];
    }

    private sealed class Composite(RunnerGroupSecrets owner) : ISecretStore, ISecretProvider
    {
        public string Name => "runner-store";

        public bool CanHandle(SecretRef reference) => reference.Kind == SecretRefKind.Secret && SecretName.TryParse(reference.Target, out _);

        public async ValueTask<SecretValue?> TryResolveAsync(SecretRef reference, CancellationToken ct = default)
        {
            if (!CanHandle(reference)) return null;
            if (owner.Values.Contains(reference.Target))
            {
                try { return await owner.Values.TryResolveAsync(reference, ct).ConfigureAwait(false); }
                catch (GroupKeyException ex)
                {
                    // the group holds it and this instance cannot open it: what the instance held before still serves, else the words name the key
                    var own = await owner.LocalAsync(reference, ct).ConfigureAwait(false);
                    return own ?? throw new SecretNotFoundException(reference.ToAuditString(), ex.Message, ex);
                }
            }

            return await owner.LocalAsync(reference, ct).ConfigureAwait(false);
        }

        public Task SetAsync(SecretName name, SecretValue value, CancellationToken ct = default)
        {
            Refuse(name);
            return owner._key.GroupHasKey ? owner.Values.SetAsync(name, value, ct) : owner.Local.SetAsync(name, value, ct);
        }

        private static void Refuse(SecretName name)
        {
            if (name.ToString() == KeySecret) throw new InvalidOperationException($"{KeySecret} is the group's key, which the console hands over when it joins the instance (PUT /api/v1/group/key), not a secret to send.");
        }

        public async Task DeleteAsync(SecretName name, CancellationToken ct = default)
        {
            Refuse(name);
            await owner.Values.DeleteAsync(name, ct).ConfigureAwait(false);
            if (owner.LocalExists) await owner.Local.DeleteAsync(name, ct).ConfigureAwait(false);
        }

        public async IAsyncEnumerable<SecretName> ListAsync(string prefix, [EnumeratorCancellation] CancellationToken ct = default)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            await foreach (var name in owner.Values.ListAsync(prefix, ct).ConfigureAwait(false))
                if (seen.Add(name.ToString())) yield return name;
            if (!owner.LocalExists) yield break;
            await foreach (var name in owner.Local.ListAsync(prefix, ct).ConfigureAwait(false))
                if (name.ToString() != KeySecret && seen.Add(name.ToString())) yield return name;
        }
    }

    private ValueTask<SecretValue?> LocalAsync(SecretRef reference, CancellationToken ct) =>
        // the group's key is the instance's own and is never served as a login
        reference.Target == KeySecret ? ValueTask.FromResult<SecretValue?>(null) : localReader.TryResolveAsync(reference, ct);
}

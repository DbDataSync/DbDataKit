using DbDataKit.Secrets;
using DbDataKit.State;
using DbDataGateway.Server.State;

namespace DbDataGateway.Server.Secrets;

/// <summary>
/// The secrets the clients of a group keep on it (plan 20 O04b), in the state the group shares and encrypted with its key (<see cref="GroupSessionKey"/>): a Build server's read login for a data source it published through one
/// instance is there for every instance that serves the source. Only a client's own secrets (<c>dbdatakit:gateway:managed-&lt;client&gt;:...</c>) are kept here; an operator's stay in each instance's encrypted store. On an instance
/// that is not one of a group it holds nothing and answers nothing. The store is the suite's shared one (<see cref="DbDataKit.Web.Trust.GroupStateSecretStore"/>, plan 28c E20d).
/// </summary>
/// <param name="state">The state, looked up when first needed (opening it may itself need a secret).</param>
/// <param name="key">The group's key.</param>
public sealed class GroupSecretStore(Func<StateDatabase> state, Func<GroupSessionKey> key) : ISecretStore, ISecretProvider
{
    public const string ManagedPrefix = "dbdatakit:gateway:managed-";

    private readonly DbDataKit.Web.Trust.GroupStateSecretStore _store = new(state, () => key().Key, ManagedPrefix, () => GatewayState.IsGroup(state()));

    // the listing has always named every row of the group's table, whatever its prefix
    private readonly DbDataKit.Web.Trust.GroupStateSecretStore _all = new(state, () => key().Key, "");

    public string Name => _store.Name;

    public bool Enabled => _store.Enabled;

    public bool CanHandle(SecretRef reference) => _store.CanHandle(reference);

    public ValueTask<SecretValue?> TryResolveAsync(SecretRef reference, CancellationToken ct = default) => GroupSessionKey.Translate(() => _store.TryResolveAsync(reference, ct));

    public Task SetAsync(SecretName name, SecretValue value, CancellationToken ct = default)
    {
        if (!name.ToString().StartsWith(ManagedPrefix, StringComparison.Ordinal)) throw new ArgumentException("The group keeps only its clients' secrets (dbdatakit:gateway:managed-...).", nameof(name));
        return GroupSessionKey.Translate(() => _store.SetAsync(name, value, ct));
    }

    public Task DeleteAsync(SecretName name, CancellationToken ct = default) => _store.DeleteAsync(name, ct);

    public IAsyncEnumerable<SecretName> ListAsync(string prefix, CancellationToken ct = default) => _all.ListAsync(prefix, ct);
}

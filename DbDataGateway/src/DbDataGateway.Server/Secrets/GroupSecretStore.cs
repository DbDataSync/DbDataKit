using System.Runtime.CompilerServices;
using System.Text;
using DbDataKit.Secrets;
using DbDataKit.State;
using DbDataGateway.Server.State;

namespace DbDataGateway.Server.Secrets;

/// <summary>
/// The secrets the clients of a group keep on it (plan 20 O04b), in the state the group shares and encrypted with its key (<see cref="GroupSessionKey"/>): a Build server's read login for a data source it published through one
/// instance is there for every instance that serves the source. Only a client's own secrets (<c>dbdatakit:gateway:managed-&lt;client&gt;:...</c>) are kept here; an operator's stay in each instance's encrypted store. On an instance
/// that is not one of a group it holds nothing and answers nothing.
/// </summary>
/// <param name="state">The state, looked up when first needed (opening it may itself need a secret).</param>
/// <param name="key">The group's key.</param>
public sealed class GroupSecretStore(Func<StateDatabase> state, Func<GroupSessionKey> key) : ISecretStore, ISecretProvider
{
    public const string ManagedPrefix = "dbdatakit:gateway:managed-";

    public string Name => "group";

    public bool Enabled => GatewayState.IsGroup(state());

    public bool CanHandle(SecretRef reference) => reference.Kind == SecretRefKind.Secret && reference.Target.StartsWith(ManagedPrefix, StringComparison.Ordinal) && Enabled;

    public ValueTask<SecretValue?> TryResolveAsync(SecretRef reference, CancellationToken ct = default)
    {
        if (!CanHandle(reference)) return ValueTask.FromResult<SecretValue?>(null);
        var database = state();
        var stored = database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command("trust", connection, "SELECT value FROM {table:group_secret} WHERE name = $name;");
            cmd.Bind(database, "name", reference.Target);
            return cmd.ExecuteScalar() as string;
        });
        if (stored is null) return ValueTask.FromResult<SecretValue?>(null);
        var plain = key().Unprotect(Convert.FromBase64String(stored));
        try { return ValueTask.FromResult<SecretValue?>(SecretValue.From(Encoding.UTF8.GetString(plain))); }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(plain); }
    }

    public Task SetAsync(SecretName name, SecretValue value, CancellationToken ct = default)
    {
        var full = name.ToString();
        if (!full.StartsWith(ManagedPrefix, StringComparison.Ordinal)) throw new ArgumentException("The group keeps only its clients' secrets (dbdatakit:gateway:managed-...).", nameof(name));
        var plain = Encoding.UTF8.GetBytes(value.RevealString());
        string sealedValue;
        try { sealedValue = Convert.ToBase64String(key().Protect(plain)); }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(plain); }
        var database = state();
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var transaction = connection.BeginTransaction();
            using (var delete = database.Command("trust", connection, transaction, "DELETE FROM {table:group_secret} WHERE name = $name;"))
                delete.Bind(database, "name", full).ExecuteNonQuery();
            using (var insert = database.Command("trust", connection, transaction, "INSERT INTO {table:group_secret} (name, value) VALUES ($name, $value);"))
                insert.Bind(database, "name", full).Bind(database, "value", sealedValue).ExecuteNonQuery();
            transaction.Commit();
        });
        return Task.CompletedTask;
    }

    public Task DeleteAsync(SecretName name, CancellationToken ct = default)
    {
        var database = state();
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command("trust", connection, "DELETE FROM {table:group_secret} WHERE name = $name;");
            cmd.Bind(database, "name", name.ToString()).ExecuteNonQuery();
        });
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<SecretName> ListAsync(string prefix, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var database = state();
        var names = database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command("trust", connection, "SELECT name FROM {table:group_secret} ORDER BY name;");
            using var reader = cmd.ExecuteReader();
            var found = new List<string>();
            while (reader.Read()) found.Add(reader.GetString(0));
            return found;
        });
        foreach (var name in names.Where(n => n.StartsWith(prefix, StringComparison.Ordinal)))
            if (SecretName.TryParse(name, out var parsed)) yield return parsed;
        await Task.CompletedTask;
    }
}

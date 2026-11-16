using System.Security.Cryptography;
using System.Text;
using DbDataKit.Secrets;

namespace DbDataKit.Connections;

/// <summary>One secret slot of a data source (plan 30 PB02): the slot's name, where the file names the secret, and the reference as it is safe to show (an inline DPAPI blob is elided).</summary>
/// <param name="Slot">The slot: the name of the secret for a <c>secret:</c> reference (<c>dbdatakit:gateway:managed-build-1:sales_conn</c>), the reference itself for <c>env:</c> and <c>file:</c>, and <c>dpapi:</c> with the first 16 hexadecimal digits of the blob's SHA-256 for an inline DPAPI value.</param>
/// <param name="Path">Where the file names it: <c>connection_string_secret</c>, <c>read_login.password_secret</c>, <c>connection_string.Password</c> for a reference inside the connection string.</param>
/// <param name="Reference">The reference for a screen or a refusal (<see cref="SecretRef.ToAuditString"/>).</param>
public sealed record SecretSlot(string Slot, string Path, string Reference)
{
    /// <summary>Whether the slot names a secret of a store (<c>secret:</c>), rather than the environment, a file or an inline value.</summary>
    public bool IsStored => !Slot.StartsWith("env:", StringComparison.Ordinal) && !Slot.StartsWith("file:", StringComparison.Ordinal) && !Slot.StartsWith("dpapi:", StringComparison.Ordinal);
}

/// <summary>
/// The secret slots of a data source (plan 30 PB02, owner decision of 2026-11-16): one per secret reference its file names (<c>connection_string_secret</c>, a login's <c>password_secret</c>, <c>connection_string_secret</c> and <c>token_secret</c>, and a credential
/// key of the connection string whose value is a reference). A host that serves data sources refuses a version while its channel has no binding for a slot; the binding names the secret, never its value. Every data source has them, whoever made it.
/// </summary>
public static class ConnectionSecretSlots
{
    /// <summary>The slots of a data source's file; none when the file does not load as a database connection.</summary>
    public static IReadOnlyList<SecretSlot> OfText(string text, string name = "data-source")
    {
        try { return ConnectionDefinitionLoader.LoadDatabaseConnection(text, name + ".yaml").Definition is { } d ? Of(d) : []; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return []; }
    }

    /// <summary>The slots of a loaded data source, in the order the file names them, each slot once.</summary>
    public static IReadOnlyList<SecretSlot> Of(ConnectionDefinition d)
    {
        var found = new List<(string Path, string? Text)> { ("connection_string_secret", d.ConnectionStringSecret) };
        foreach (var (at, login) in new[] { ("auth", d.Auth), ("read_login", d.ReadLogin), ("write_login", d.WriteLogin) })
            if (login is not null) found.AddRange([(at + ".password_secret", login.PasswordSecret), (at + ".connection_string_secret", login.ConnectionStringSecret), (at + ".token_secret", login.TokenSecret)]);
        var slots = new List<SecretSlot>();
        foreach (var (path, text) in found)
            if (SecretRef.TryParse(text, out var reference)) Add(slots, path, reference);
        foreach (var inside in ConnectionStringReferences.Find(d.ConnectionString)) Add(slots, "connection_string." + inside.Key, inside.Reference);
        return slots;
    }

    /// <summary>The slot a reference fills (see <see cref="SecretSlot.Slot"/>).</summary>
    public static string SlotOf(SecretRef reference) => reference.Kind switch
    {
        SecretRefKind.Secret => reference.Target,
        SecretRefKind.Dpapi => "dpapi:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(reference.Target)))[..16].ToLowerInvariant(),
        _ => reference.ToString(),
    };

    /// <summary>The reference a slot stands for, when the slot alone says it (every kind but an inline DPAPI value, whose blob is in the file only).</summary>
    public static SecretRef? ReferenceOf(string slot) =>
        slot.StartsWith("dpapi:", StringComparison.Ordinal) ? null
        : slot.StartsWith("env:", StringComparison.Ordinal) || slot.StartsWith("file:", StringComparison.Ordinal) ? (SecretRef.TryParse(slot, out var r) ? r : null)
        : new SecretRef(SecretRefKind.Secret, slot);

    private static void Add(List<SecretSlot> slots, string path, SecretRef reference)
    {
        var slot = SlotOf(reference);
        if (slots.All(s => s.Slot != slot)) slots.Add(new SecretSlot(slot, path, reference.ToAuditString()));
    }
}

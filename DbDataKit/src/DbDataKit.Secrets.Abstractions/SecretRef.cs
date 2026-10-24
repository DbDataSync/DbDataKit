using System.Diagnostics.CodeAnalysis;

namespace DbDataKit.Secrets;

public enum SecretRefKind { Secret, Env, File, Dpapi }

/// <summary>
/// A reference to a secret, as written in config. Forms: <c>secret:&lt;name&gt;</c>, <c>env:NAME</c>,
/// <c>file:/path</c>, <c>dpapi:&lt;base64&gt;</c>.
/// </summary>
public sealed record SecretRef(SecretRefKind Kind, string Target)
{
    private static readonly (string Prefix, SecretRefKind Kind)[] Schemes =
    [
        ("secret:", SecretRefKind.Secret),
        ("env:", SecretRefKind.Env),
        ("file:", SecretRefKind.File),
        ("dpapi:", SecretRefKind.Dpapi),
    ];

    /// <summary>True when the text starts with one of the known reference schemes (does not validate the rest).</summary>
    public static bool LooksLikeRef(string? text) =>
        text is not null && Schemes.Any(s => text.StartsWith(s.Prefix, StringComparison.OrdinalIgnoreCase));

    public static SecretRef Parse(string text) =>
        TryParse(text, out var r, out var error) ? r : throw new FormatException(error);

    public static bool TryParse(string? text, [NotNullWhen(true)] out SecretRef? result) =>
        TryParse(text, out result, out _);

    public static bool TryParse(string? text, [NotNullWhen(true)] out SecretRef? result, [NotNullWhen(false)] out string? error)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(text)) { error = "Secret reference is empty."; return false; }
        text = text.Trim();
        foreach (var (prefix, kind) in Schemes)
        {
            if (!text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var target = text[prefix.Length..];
            if (string.IsNullOrWhiteSpace(target))
            {
                error = $"Secret reference '{prefix}' has no target.";
                return false;
            }
            result = new SecretRef(kind, target);
            error = null;
            return true;
        }
        error = "Unknown secret reference scheme; expected secret:, env:, file: or dpapi:.";
        return false;
    }

    /// <summary>The reference as written in config.</summary>
    public override string ToString() => Schemes.First(s => s.Kind == Kind).Prefix + Target;

    /// <summary>Safe for logs: names only; an inline DPAPI blob is elided.</summary>
    public string ToAuditString() => Kind == SecretRefKind.Dpapi ? "dpapi:<blob>" : ToString();
}

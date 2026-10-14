using DbDataSync.Certificates;

namespace DbDataSync.Cli;

/// <summary>
/// What <c>dbdatasync config cert discover</c> decides to write and say, separated from the directory query and
/// the file write so every branch can be tested without a domain.
/// </summary>
/// <param name="WriteCaConfig">Set only when exactly one CA was found and none was configured.</param>
/// <param name="WriteTemplate">Set only when the CA publishes exactly one template and none was configured.</param>
/// <param name="Lines">What to print, in order.</param>
/// <param name="Succeeded">False when nothing could be determined (directory unreachable, no CA published).</param>
internal sealed record DiscoveryOutcome(
    string? WriteCaConfig, string? WriteTemplate, IReadOnlyList<string> Lines, bool Succeeded);

internal static class CertDiscovery
{
    public const string CaKey = "DbDataSync:Certificates:CaConfig";
    public const string TemplateKey = "DbDataSync:Certificates:Template";

    /// <summary>
    /// One CA, or one template, is written without asking. More than one is printed with the command that would
    /// save a chosen one. A value already configured is never overwritten.
    /// </summary>
    public static DiscoveryOutcome Decide(CaDiscoveryResult result, string? configuredCa, string? configuredTemplate)
    {
        if (result.Reason != TemplateListReason.Available)
        {
            return Failed(
                $"Could not discover certificate authorities ({result.Reason}): {result.Detail}",
                "Find the CA name with 'certutil' (the Config: line) on a domain-joined machine, then save it with:",
                SetCommand(CaKey, @"CASERVER\CA Name"));
        }

        if (result.Cas.Count == 0)
        {
            return Failed(
                "No enterprise certificate authority is published in this domain.",
                "If you expect one, ask whoever runs the CA for its 'CASERVER\\CA Name' string and save it with:",
                SetCommand(CaKey, @"CASERVER\CA Name"));
        }

        var lines = new List<string>();
        string? writeCa = null;
        DiscoveredCa chosen;

        if (!string.IsNullOrWhiteSpace(configuredCa))
        {
            var match = result.Cas.FirstOrDefault(c => string.Equals(c.CaConfig, configuredCa, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                lines.Add($"The configured CA '{configuredCa}' is not one this domain publishes, so it was left alone. Published:");
                AddCaChoices(lines, result.Cas);
                return new DiscoveryOutcome(null, null, lines, Succeeded: true);
            }

            lines.Add($"CA already configured: {match.CaConfig}");
            chosen = match;
        }
        else if (result.Cas.Count == 1)
        {
            chosen = result.Cas[0];
            writeCa = chosen.CaConfig;
            lines.Add($"Found one CA and saved it: {chosen.CaConfig}");
        }
        else
        {
            lines.Add("Found several CAs. Save the one to use, then run discover again for its templates:");
            AddCaChoices(lines, result.Cas);
            return new DiscoveryOutcome(null, null, lines, Succeeded: true);
        }

        string? writeTemplate = null;
        string? template = null;
        if (!string.IsNullOrWhiteSpace(configuredTemplate))
        {
            template = configuredTemplate;
            var published = chosen.Templates.Any(t => string.Equals(t, configuredTemplate, StringComparison.OrdinalIgnoreCase));
            lines.Add(published
                ? $"Template already configured: {configuredTemplate}"
                : $"Template already configured: {configuredTemplate} (not published on this CA — it was left alone)");
        }
        else if (chosen.Templates.Count == 1)
        {
            template = writeTemplate = chosen.Templates[0];
            lines.Add($"This CA publishes one template and it was saved: {template}");
        }
        else if (chosen.Templates.Count == 0)
        {
            lines.Add("This CA publishes no templates.");
        }
        else
        {
            lines.Add("This CA publishes several templates. Save the one to use:");
            foreach (var name in chosen.Templates)
                lines.Add($"  {name}    {SetCommand(TemplateKey, name)}");
        }

        if (template is not null)
        {
            lines.Add("");
            lines.Add("Next: dbdatasync config cert enroll --dns <names>");
        }

        return new DiscoveryOutcome(writeCa, writeTemplate, lines, Succeeded: true);
    }

    public static string SetCommand(string key, string value) => $"dbdatasync config set {key} \"{value}\"";

    private static void AddCaChoices(List<string> lines, IReadOnlyList<DiscoveredCa> cas)
    {
        foreach (var ca in cas)
            lines.Add($"  {ca.CaConfig}    {SetCommand(CaKey, ca.CaConfig)}");
    }

    private static DiscoveryOutcome Failed(params string[] lines) => new(null, null, lines, Succeeded: false);
}

using DbDataKit.Config.Yaml;

namespace DbDataKit.Config.Testing;

/// <summary>What a fuzz target made of one damaged input.</summary>
public readonly record struct FuzzOutcome(bool Accepted, IReadOnlyList<ConfigDiagnostic> Diagnostics);

/// <summary>
/// Build's rule as a helper: a damaged config file is answered with diagnostics, never an exception. Seeded mutations (cut, delete a line,
/// duplicate a line, replace a character, insert a token, swap two lines, change indentation) of a valid sample are fed to a target.
/// </summary>
public static class FuzzHelper
{
    public const int DefaultSeed = 20261002;

    private static readonly string[] Tokens =
        ["", " ", "\t", ":", "- ", "[", "]", "{", "}", "\"", "'", "#", "&a", "*a", "!!str", "? ", "|", ">", "---", "...", "null", "~", "true", "-1", "1e999", "0x10", "\u0000", "é", "‮", "\r", "\\", "%", "@", "`", "$$", "{{x}}", "<<: *a"];

    public static IEnumerable<string> Mutations(string text, int seed = DefaultSeed, int count = 500)
    {
        var rng = new Random(seed);
        for (var i = 0; i < count; i++)
        {
            var lines = text.Split('\n').ToList();
            switch (rng.Next(7))
            {
                case 0: yield return text[..rng.Next(text.Length + 1)]; break;
                case 1: lines.RemoveAt(rng.Next(lines.Count)); yield return string.Join('\n', lines); break;
                case 2: { var k = rng.Next(lines.Count); lines.Insert(k, lines[k]); yield return string.Join('\n', lines); break; }
                case 3: { var chars = text.ToCharArray(); if (chars.Length > 0) chars[rng.Next(chars.Length)] = Tokens[rng.Next(Tokens.Length)].FirstOrDefault('?'); yield return new string(chars); break; }
                case 4: yield return text.Insert(rng.Next(text.Length + 1), Tokens[rng.Next(Tokens.Length)]); break;
                case 5: { var a = rng.Next(lines.Count); var b = rng.Next(lines.Count); (lines[a], lines[b]) = (lines[b], lines[a]); yield return string.Join('\n', lines); break; }
                default: { var k = rng.Next(lines.Count); lines[k] = new string(' ', rng.Next(8)) + lines[k].TrimStart(); yield return string.Join('\n', lines); break; }
            }
        }
    }

    /// <summary>
    /// Runs <paramref name="target"/> on every mutation. Fails when it throws, or when it refuses the input without reporting a diagnostic
    /// (<see cref="FuzzOutcome.Accepted"/> false and no diagnostics). Returns how many mutations were accepted, so callers can check the fuzz is not vacuous.
    /// </summary>
    public static int DamagedInputYieldsDiagnostics(string sample, Func<string, FuzzOutcome> target, int seed = DefaultSeed, int count = 500)
    {
        var accepted = 0;
        foreach (var m in Mutations(sample, seed, count))
        {
            FuzzOutcome outcome;
            try { outcome = target(m); }
            catch (Exception ex) { throw new ConfigTestException($"The target threw {ex.GetType().Name} on damaged input (seed {seed}):\n{m}", ex); }
            if (outcome.Accepted) accepted++;
            else if (outcome.Diagnostics.Count == 0) throw new ConfigTestException($"The target refused damaged input without a diagnostic (seed {seed}):\n{m}");
        }
        return accepted;
    }

    /// <summary>Fuzzes <c>ConfigBuilder&lt;T&gt;</c> (strict reader, layering, binder, validators).</summary>
    public static int ForBuilder<T>(string sample, int seed = DefaultSeed, int count = 500, Action<ConfigBuilder<T>>? configure = null) where T : class =>
        DamagedInputYieldsDiagnostics(sample, m =>
        {
            var b = new ConfigBuilder<T>().AddText("fuzz.yml", m);
            configure?.Invoke(b);
            var r = b.Build();
            return new FuzzOutcome(r.Ok, r.Diagnostics);
        }, seed, count);

    /// <summary>Fuzzes the migration engine, and checks that every migrated result is still valid strict YAML.</summary>
    public static int ForMigrations(MigrationEngine engine, string kind, string sample, int seed = DefaultSeed, int count = 500, MigrationOptions? options = null) =>
        DamagedInputYieldsDiagnostics(sample, m =>
        {
            var r = engine.Plan(kind, m, "fuzz.yml", options);
            if (r.Status == MigrationStatus.Migrated)
            {
                var d = new List<ConfigDiagnostic>();
                StrictYamlReader.Read(r.NewText, "fuzz.yml", d);
                if (d.HasErrors()) throw new InvalidOperationException("migration produced invalid YAML: " + d.First().Message);
            }
            return new FuzzOutcome(r.Ok, r.Diagnostics);
        }, seed, count);

    /// <summary>Fuzzes the strict reader itself (YAML or, for a <c>.json</c> file name, JSON).</summary>
    public static int ForReader(string sample, string file = "fuzz.yml", int seed = DefaultSeed, int count = 500) =>
        DamagedInputYieldsDiagnostics(sample, m =>
        {
            var d = new List<ConfigDiagnostic>();
            JsonInput.ReadByExtension(m, file, d);
            return new FuzzOutcome(!d.HasErrors(), d);
        }, seed, count);
}

using DbDataKit.Config.Yaml;

namespace DbDataKit.Config.Yaml.Tests;

/// <summary>
/// Ported from DbDataBuild's FuzzTests: seeded mutations (cut, delete a line, duplicate a line, replace a character, insert a token, swap two lines,
/// change indentation) of valid files. Every damaged input must come back as diagnostics or a node tree, never an exception.
/// </summary>
public class FuzzTests
{
    private const int Seed = 20261002;

    private static readonly string[] Tokens =
        ["", " ", "\t", ":", "- ", "[", "]", "{", "}", "\"", "'", "#", "&a", "*a", "!!str", "? ", "|", ">", "---", "...", "null", "~", "true", "-1", "1e999", "0x10", "\u0000", "é", "‮", "\r", "\\", "%", "@", "`", "$$", "{{x}}", "<<: *a"];

    public static IEnumerable<string> Mutations(string text, int seed, int count)
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

    public const string Sample = """
        # sample
        schemaVersion: 1
        title: Fuzz
        tags: [fuzz, "layer:one"]
        properties: { owner: data, tier: 2, live: true }
        server:
          listen: { address: 0.0.0.0, port: 5432 }
          users:
            - name: alice
              sources: [erp, crm]
            - name: bob
              sources: [erp]
        notes: |
          free text
          more
        """;

    [Fact]
    public void Sample_is_clean()
    {
        var d = new List<ConfigDiagnostic>();
        Assert.NotNull(StrictYamlReader.Read(Sample, "s.yml", d));
        Assert.Empty(d);
    }

    [Fact]
    public void Damaged_input_never_throws_in_the_reader()
    {
        foreach (var m in Mutations(Sample, Seed, 600))
            StrictYamlReader.Read(m, "s.yml", []);
    }

    [Fact]
    public void Damaged_input_never_throws_in_the_editor_and_never_yields_unparseable_text()
    {
        foreach (var m in Mutations(Sample, Seed + 1, 300))
        {
            foreach (var r in new[]
            {
                YamlEditor.Set(m, "server.listen.port", "9"),
                YamlEditor.Set(m, "a.b.c", "x"),
                YamlEditor.Remove(m, "server.users[0]"),
                YamlEditor.Remove(m, "title"),
                YamlEditor.Insert(m, "tags", "new"),
            })
            {
                if (!r.Ok) continue;
                var d = new List<ConfigDiagnostic>();
                StrictYamlReader.Read(r.Text!, "s.yml", d);
                Assert.False(d.HasErrors(), "edit produced a document with errors from: " + m);
            }
        }
    }

    [Fact]
    public void Damaged_input_never_throws_in_merge()
    {
        foreach (var m in Mutations(Sample, Seed + 2, 200))
        {
            var d = new List<ConfigDiagnostic>();
            if (StrictYamlReader.Read(m, "a.yml", d) is YamlMapping root)
                YamlMerge.Merge([new("base.yml", (YamlMapping)StrictYamlReader.Read(Sample, "base.yml", [])!), new("a.yml", root)], new HashSet<string> { "server", "tags", "properties" }, d);
        }
    }
}

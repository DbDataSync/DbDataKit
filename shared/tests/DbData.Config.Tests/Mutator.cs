

namespace DbData.Config.Tests;

/// <summary>
/// Ported from DbDataBuild's FuzzTests: seeded mutations (cut, delete a line, duplicate a line, replace a character, insert a token, swap two lines,
/// change indentation) of valid files. Every damaged input must come back as diagnostics or a node tree, never an exception.
/// </summary>
public static class Mutator
{
    private const int Seed = 20261002;

    private static readonly string[] Tokens =
        ["", " ", "\t", ":", "- ", "[", "]", "{", "}", "\"", "'", "#", "&a", "*a", "!!str", "? ", "|", ">", "---", "...", "null", "~", "true", "-1", "1e999", "0x10", "\u0000", "Ã©", "â€®", "\r", "\\", "%", "@", "`", "$$", "{{x}}", "<<: *a"];

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
}

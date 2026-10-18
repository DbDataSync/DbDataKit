using System.Text;

namespace DbDataBuild.Core;

/// <summary>
/// Text the tool produces ends its lines with `\n` on every platform. Anything that is hashed (a plan), compared (a rendered file, a diff) or pasted (an answers template) must not
/// depend on whether the build ran on Windows, where `AppendLine` would write `\r\n`. Windows terminals show `\n` correctly.
/// </summary>
public static class StringBuilderLf
{
    public static StringBuilder AppendLineLf(this StringBuilder sb, string text) => sb.Append(text).Append('\n');
    public static StringBuilder AppendLineLf(this StringBuilder sb) => sb.Append('\n');
}

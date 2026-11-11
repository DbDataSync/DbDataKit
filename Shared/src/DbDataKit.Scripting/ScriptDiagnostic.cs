namespace DbDataKit.Scripting;

/// <summary>A problem in a script, at the script's own line: a directive line is blanked, never removed, and a <c>#load</c>ed file is named, so the numbers are the ones an editor shows.</summary>
public sealed record ScriptDiagnostic(string? File, int Line, int Column, string Message, bool IsError = true)
{
    public override string ToString() => Line > 0 ? $"{File}({Line},{Column}): {Message}" : Message;
}

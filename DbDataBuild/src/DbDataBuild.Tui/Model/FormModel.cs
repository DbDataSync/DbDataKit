using System.Globalization;

namespace DbDataBuild.Tui.Model;

public sealed class FormField
{
    public required string Label { get; init; }
    public required string Description { get; init; }
    public required OptionKind Kind { get; init; }
    public required bool IsArgument { get; init; }
    public required bool Required { get; init; }
    public required string? Default { get; init; }
    public required IReadOnlyList<string> Choices { get; init; }

    /// <summary>The text of a Text, Integer, Path or Choice field, or the comma-separated items of a List.</summary>
    public string Value { get; set; } = "";
    public bool Checked { get; set; }

    public IReadOnlyList<string> Items => Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
}

/// <summary>
/// A form for one command, built from its option and argument definitions: one field per option and argument, validated, and turned back into the exact argument list the
/// CLI would take (and into the command line a person could type). Nothing here knows about a particular command.
/// </summary>
public sealed class FormModel
{
    public CommandInfo Command { get; }
    public IReadOnlyList<FormField> Fields { get; }

    public FormModel(CommandInfo command, string projectRoot)
    {
        Command = command;
        var fields = new List<FormField>();
        foreach (var a in command.Arguments)
            fields.Add(new FormField
            {
                Label = a.Name, Description = a.Description, Kind = a.Choices.Count > 0 && !a.Repeatable ? OptionKind.Choice : a.Repeatable ? OptionKind.List : OptionKind.Text,
                IsArgument = true, Required = a.Required, Default = null, Choices = a.Choices,
            });
        foreach (var o in command.Options)
        {
            var f = new FormField { Label = o.Name, Description = o.Description, Kind = o.Kind, IsArgument = false, Required = false, Default = o.Default, Choices = o.Choices };
            if (o.Kind == OptionKind.Flag) f.Checked = o.Default == "True";
            else f.Value = o.Name == "--project" ? projectRoot : o.Default ?? "";
            fields.Add(f);
        }
        Fields = fields;
    }

    public FormField Field(string label) => Fields.First(f => f.Label == label);

    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        foreach (var f in Fields)
        {
            if (f.Kind == OptionKind.Flag) continue;
            if (f.Required && f.Items.Count == 0 && f.Kind == OptionKind.List || f.Required && f.Kind != OptionKind.List && f.Value.Trim().Length == 0)
                problems.Add($"{f.Label} is required.");
            if (f.Kind == OptionKind.Integer && f.Value.Trim().Length > 0 && !int.TryParse(f.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                problems.Add($"{f.Label} must be a whole number.");
            if (f.Kind == OptionKind.Choice && f.Value.Trim().Length > 0 && f.Choices.Count > 0 && !f.Choices.Contains(f.Value.Trim()))
                problems.Add($"{f.Label} must be one of: {string.Join(", ", f.Choices)}.");
        }
        return problems;
    }

    /// <summary>The arguments after the command name: positionals first, then every option whose value differs from its default.</summary>
    public IReadOnlyList<string> Arguments()
    {
        var args = new List<string>();
        foreach (var f in Fields.Where(f => f.IsArgument))
            args.AddRange(f.Kind == OptionKind.List ? f.Items : f.Value.Trim().Length > 0 ? [f.Value.Trim()] : []);
        foreach (var f in Fields.Where(f => !f.IsArgument))
        {
            switch (f.Kind)
            {
                case OptionKind.Flag:
                    if (f.Checked != (f.Default == "True")) args.Add(f.Label);
                    break;
                case OptionKind.List:
                    foreach (var item in f.Items) { args.Add(f.Label); args.Add(item); }
                    break;
                default:
                    var v = f.Value.Trim();
                    if (v.Length > 0 && v != (f.Default ?? "")) { args.Add(f.Label); args.Add(v); }
                    break;
            }
        }
        return args;
    }

    /// <summary>`--project` is always passed, even when it is the default, because the TUI works on one project and the command must not guess.</summary>
    public IReadOnlyList<string> FullArguments()
    {
        var args = Arguments().ToList();
        var project = Fields.FirstOrDefault(f => f.Label == "--project");
        if (project != null && !args.Contains("--project") && project.Value.Trim().Length > 0) { args.Add("--project"); args.Add(project.Value.Trim()); }
        return [.. Command.Name.Split(' '), .. args];
    }

    /// <summary>Whether running the form as it stands changes something (files, tracking tables, data, the target). The TUI asks before it does.</summary>
    public bool ChangesSomething()
    {
        return WriteRules.Changes(Command, label =>
        {
            var f = Fields.FirstOrDefault(x => x.Label == label);
            return f != null && (f.Kind == OptionKind.Flag ? f.Checked : f.Value.Trim().Length > 0);
        }, repoFiles: true);
    }

    /// <summary>The command a person could type to do the same thing.</summary>
    public string CommandLine() => "dbdatabuild " + string.Join(" ", FullArguments().Select(Quote));

    public static string Quote(string s) => s.Length > 0 && s.All(c => char.IsLetterOrDigit(c) || "-_./=:,@+".Contains(c)) ? s : "'" + s.Replace("'", "'\\''") + "'";
}

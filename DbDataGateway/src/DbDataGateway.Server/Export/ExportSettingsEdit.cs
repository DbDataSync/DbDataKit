using DbDataKit.Config.Yaml;

namespace DbDataGateway.Server.Export;

/// <summary>The edit that points <c>views.export</c> at an installed component (plan 32 DBR16), on the text of <c>server.yaml</c>: comments, order and every other key stay as they are.</summary>
public static class ExportSettingsEdit
{
    /// <summary>The text with each setting set; <c>changed</c> lists the paths whose value was not already that.</summary>
    public static (string Text, IReadOnlyList<string> Changed) Apply(string text, IReadOnlyDictionary<string, string> settings, string file = "server.yaml")
    {
        var changed = new List<string>();
        foreach (var (path, value) in settings)
        {
            var edit = YamlEditor.Set(text, path, new YamlValue(value), file);
            if (edit.Text is null) throw new ExportInstallException($"{file} could not be edited at {path}: {edit.Problem?.Message ?? "the edit was refused"}. Set views.export by hand (docs/views.md, The export component).");
            if (edit.Text != text) changed.Add(path);
            text = edit.Text;
        }
        return (text, changed);
    }
}

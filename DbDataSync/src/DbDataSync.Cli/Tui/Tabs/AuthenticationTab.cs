using Microsoft.Extensions.Configuration;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace DbDataSync.Cli.Tui.Tabs;

/// <summary>Old step 5: through the console (default; nothing to set, a console's calls carry a signed token), Windows groups, or none — guarded by the same
/// type-the-phrase hard confirm before disabling authentication (<see cref="Tui.Dialogs.HardConfirm"/>,
/// shown from <see cref="Save"/> rather than kept as a persistent field, since it only matters at the
/// moment of saving "none").</summary>
internal sealed class AuthenticationTab : View
{
    private static readonly string[] Choices = ["console", "windows", "none"];

    private readonly OptionSelector _method = new()
    {
        X = 1, Y = 0,
        Labels = ["Console only (recommended)", "Windows groups", "None — trusted network only"],
        Value = 0,
    };

    private readonly Label _adminGroupLabel = new() { X = 1, Y = 4, Text = "Admin group:" };
    private readonly TextField _adminGroup = new() { X = 1, Y = 5, Width = Dim.Fill(1) };
    private readonly Label _viewerGroupLabel = new() { X = 1, Y = 6, Text = "Viewer group (blank for none):" };
    private readonly TextField _viewerGroup = new() { X = 1, Y = 7, Width = Dim.Fill(1) };

    public AuthenticationTab()
    {
        Title = "Auth"; // short — six tabs need to fit an 80-column tab strip
        CanFocus = true;
        Add(_method, _adminGroupLabel, _adminGroup, _viewerGroupLabel, _viewerGroup);
        _method.ValueChanged += (_, _) => UpdateVisibility();
        UpdateVisibility();
    }

    private void UpdateVisibility()
    {
        var choice = Choices[_method.Value ?? 0];
        _adminGroupLabel.Visible = choice == "windows";
        _adminGroup.Visible = choice == "windows";
        _viewerGroupLabel.Visible = choice == "windows";
        _viewerGroup.Visible = choice == "windows";
    }

    public SetupSteps.StepResult Save(IApplication app, string root)
    {
        var choice = Choices[_method.Value ?? 0];

        var noAuthConfirmed = choice == "none"
            && Dialogs.HardConfirm(app, "DbDataSync will accept every request with no sign-in.", "ALLOW");

        return SetupSteps.ApplyAuthentication(root, choice, _adminGroup.Text, _viewerGroup.Text, noAuthConfirmed);
    }

    public void Populate(IConfiguration configuration)
    {
        if (string.Equals(configuration["DbDataSync:Auth:Network:Admin"], "loopback", StringComparison.OrdinalIgnoreCase))
        {
            _method.Value = 2;
        }
        else if (!string.IsNullOrEmpty(configuration["DbDataSync:Auth:Windows:AdminGroup"]))
        {
            _method.Value = 1;
            _adminGroup.Text = configuration["DbDataSync:Auth:Windows:AdminGroup"] ?? "";
            _viewerGroup.Text = configuration["DbDataSync:Auth:Windows:ViewerGroup"] ?? "";
        }
        else
        {
            _method.Value = 0;
        }

        UpdateVisibility();
    }
}

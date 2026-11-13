namespace DbDataKit.Views.Export.Tests;

/// <summary>The embedded pins (plan 32 DBR16): the browser and fonts this program was tested with, used only for a verification line or a warning.</summary>
public sealed class ExportPinsTests
{
    [Fact]
    public void The_embedded_pins_are_the_repository_files()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "chromium.pin"))) root = root.Parent;
        Assert.NotNull(root);
        foreach (var name in new[] { "chromium.pin", "fonts.pin" })
            Assert.Equal(File.ReadAllText(Path.Combine(root.FullName, name)).Replace("\r\n", "\n").Split('\n'), ExportPins.Text(name).Split('\n'));
        Assert.Contains($"VERSION={ExportPins.Chromium}", File.ReadAllText(Path.Combine(root.FullName, "chromium.pin")));
    }

    [Fact]
    public void The_tested_browser_and_fonts_are_verified_and_any_other_is_a_warning_in_words()
    {
        Assert.True(ExportPins.Compare(ExportPins.Chromium, ExportPins.Fonts, ExportPins.Families).Verified);
        Assert.True(ExportPins.Compare("HeadlessChrome/" + ExportPins.Chromium, null, null).Verified);   // a browser's own version string
        var other = ExportPins.Compare("999.0.0.1", ExportPins.Fonts, ExportPins.Families);
        Assert.False(other.Verified);
        Assert.Equal($"this component carries Chromium 999.0.0.1 (this program was tested with {ExportPins.Chromium}): installed as the release pins it.", other.Note);
        var fonts = ExportPins.Compare(ExportPins.Chromium, "0.0.1", ["noto-sans"]);
        Assert.False(fonts.Verified);
        Assert.Contains("fonts 0.0.1", fonts.Note);
        Assert.Contains("font families noto-sans", fonts.Note);
    }
}

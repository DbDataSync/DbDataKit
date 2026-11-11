using DbDataKit.Web.Auth;

namespace DbDataKit.Web.Tests;

/// <summary>13a U22: the <c>session:</c> section of a host's settings file.</summary>
public sealed class SessionSettingsFileTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ddk-session-" + Guid.NewGuid().ToString("N"));

    public SessionSettingsFileTests() => Directory.CreateDirectory(_directory);

    public void Dispose() { try { Directory.Delete(_directory, true); } catch (IOException) { } }

    private string File_(string text)
    {
        var path = Path.Combine(_directory, "server.yaml");
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public void No_file_or_no_section_is_the_default()
    {
        Assert.Equal(DbDataKit.State.SessionSettings.Default, SessionSettingsFile.Load(Path.Combine(_directory, "none.yaml")));
        Assert.Equal(DbDataKit.State.SessionSettings.Default, SessionSettingsFile.Load(File_("instanceId: build-1\n")));
    }

    [Fact]
    public void A_section_sets_the_length_and_the_idle_timeout_beside_what_else_the_file_holds()
    {
        var settings = SessionSettingsFile.Load(File_("instanceId: identity-1\nsession:\n  length_hours: 8\n  idle_minutes: 45\nclients: []\n"));
        Assert.Equal(TimeSpan.FromHours(8), settings.Length);
        Assert.Equal(TimeSpan.FromMinutes(45), settings.IdleTimeout);
        Assert.Null(SessionSettingsFile.Load(File_("session:\n  idle_minutes: 0\n")).IdleTimeout);
    }

    [Theory]
    [InlineData("session:\n  length_hours: 0\n", "session.length_hours")]
    [InlineData("session:\n  length_hours: 25\n", "session.length_hours")]
    [InlineData("session:\n  length_hours: soon\n", "session.length_hours")]
    [InlineData("session:\n  idle_minutes: -5\n", "session.idle_minutes")]
    [InlineData("session:\n  sliding: true\n", "'sliding' is not a session setting")]
    public void A_setting_that_is_wrong_is_refused_with_a_message_naming_the_file_and_the_setting(string yaml, string expected)
    {
        var path = File_(yaml);
        var error = Assert.Throws<InvalidOperationException>(() => SessionSettingsFile.Load(path));
        Assert.Contains(path, error.Message);
        Assert.Contains(expected, error.Message);
    }
}

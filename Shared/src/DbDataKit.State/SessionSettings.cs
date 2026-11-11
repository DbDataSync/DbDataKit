namespace DbDataKit.State;

/// <summary>
/// How long a sign-in session lasts (plan 16, "Sign-in decisions" item 8; plan 13a U22): <see cref="Length"/> from the moment of sign-in, which use does not extend, and an optional <see cref="IdleTimeout"/>, after which a
/// session that was not used is refused. Set per install in the settings file of the host (<c>session:</c>, see <c>SessionSettingsFile</c> in <c>DbDataKit.Web</c>); the defaults are what was built before: 12 hours, no idle timeout.
/// </summary>
/// <param name="Length">How long a session lives from sign-in, 1 to 24 hours.</param>
/// <param name="IdleTimeout">A session unused for longer than this is refused; null: off.</param>
public sealed record SessionSettings(TimeSpan Length, TimeSpan? IdleTimeout = null)
{
    public static readonly TimeSpan MinimumLength = TimeSpan.FromHours(1);
    public static readonly TimeSpan MaximumLength = TimeSpan.FromHours(24);

    public static readonly SessionSettings Default = new(TimeSpan.FromHours(12));

    /// <summary>The first thing wrong with the settings, in words for the person who set them, or null. <paramref name="where"/> names the file and is put in front.</summary>
    public string? Problem(string where)
    {
        if (Length < MinimumLength || Length > MaximumLength) return $"{where}: session.length_hours is a number of hours from 1 to 24, not {Length.TotalHours:0.##}.";
        if (IdleTimeout is { } idle && idle < TimeSpan.Zero) return $"{where}: session.idle_minutes is a number of minutes, 0 or more (0 turns the idle timeout off), not {idle.TotalMinutes:0.##}.";
        return null;
    }
}

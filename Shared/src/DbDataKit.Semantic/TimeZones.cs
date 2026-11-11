using System.Globalization;

namespace DbDataKit.Semantic;

/// <summary>
/// Time zones are IANA names everywhere in the catalog and the request ("America/Chicago", "UTC"). This turns them into the three things the engine needs:
/// the boundaries of a local day as instants, the wall-clock text a source holds, and the name SQL Server's AT TIME ZONE expects (it wants Windows names).
/// </summary>
public static class TimeZones
{
    public static TimeZoneInfo? Find(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException) { return null; }
    }

    public static bool IsValid(string id) => IsSafe(id) && Find(id) is not null;

    /// <summary>Zone names are written into SQL text, so only the characters a zone name has are allowed.</summary>
    private static bool IsSafe(string id) => id.Length is > 0 and <= 64 && id.All(c => char.IsLetterOrDigit(c) || c is '/' or '_' or '-' or '+');

    /// <summary>The Windows zone name SQL Server's AT TIME ZONE uses for an IANA name (UTC is the same in both).</summary>
    public static string WindowsId(string iana)
    {
        if (iana is "UTC" or "Etc/UTC") return "UTC";
        return TimeZoneInfo.TryConvertIanaIdToWindowsId(iana, out var windows) && windows is not null ? windows : iana;
    }

    /// <summary>The instant (UTC) at which a calendar day starts in a zone. A day whose midnight doesn't exist (a DST gap) starts an hour later.</summary>
    public static DateTime StartOfDayUtc(DateOnly day, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(local)) local = local.AddHours(1);
        return TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }

    /// <summary>A UTC instant as the naive wall-clock time a source holding values in <paramref name="source"/> stores.</summary>
    public static DateTime ToWallClock(DateTime utc, TimeZoneInfo source) =>
        DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), source), DateTimeKind.Unspecified);

    /// <summary>A source's naive wall-clock value as a UTC instant.</summary>
    public static DateTime FromWallClock(DateTime wall, TimeZoneInfo source)
    {
        var unspecified = DateTime.SpecifyKind(wall, DateTimeKind.Unspecified);
        while (source.IsInvalidTime(unspecified)) unspecified = unspecified.AddHours(1);
        return TimeZoneInfo.ConvertTimeToUtc(unspecified, source);
    }

    public static string Text(DateTime wall) => wall.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>An instant as ISO 8601 in UTC with a Z: the one form a datetime has in results.</summary>
    public static string Iso(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>A filter value as an instant: a date means the start of that day in the zone; anything else is an ISO 8601 instant (a missing offset means UTC).</summary>
    public static bool TryInstant(string text, TimeZoneInfo zone, out DateTime utc)
    {
        if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        { utc = StartOfDayUtc(day, zone); return true; }
        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var instant))
        { utc = instant.UtcDateTime; return true; }
        utc = default;
        return false;
    }
}

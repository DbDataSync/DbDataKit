using System.Globalization;
using System.Reflection;
using Microsoft.AspNetCore.Builder;

namespace DbDataKit.Web.Trust;

/// <summary>
/// The compatibility generation of the suite (plan 24, section 2; 24a D1 and D2): an integer that a change bumps when it changes a contract between parts (a management API, a protocol, a definition format, a state
/// module marked incompatible). "Release N" in "N and N-1" means a generation, because the suite is released every day and "the previous release" would mean yesterday. Every part works with its own generation and the one
/// before it: a pair whose generations are more than one apart is refused, at pairing and on every call after an upgrade, with a message naming both program versions and generations and the side to upgrade.
/// The management API's own integer (<see cref="ManagementApi.Version"/>) is a separate check and is made first.
/// </summary>
public static class SuiteCompatibility
{
    /// <summary>The generation this code was built with. It is a constant on purpose: a repository check (24a UC02) fails a pull request that changes a frozen contract without raising it.</summary>
    public const int Generation = 1;

    public const string GenerationHeader = "X-DbData-Generation";
    public const string ProgramVersionHeader = "X-DbData-Program-Version";

    /// <summary>The version of the program that is running, as it says itself (the entry assembly; this library's when there is none).</summary>
    public static string ProgramVersion { get; } =
        (Assembly.GetEntryAssembly() ?? typeof(SuiteCompatibility).Assembly).GetName().Version?.ToString() ?? "0.0.0";

    /// <summary>The oldest generation a part of generation <paramref name="own"/> works with.</summary>
    public static int Oldest(int own = Generation) => Math.Max(1, own - 1);

    /// <summary>The newest generation a part of generation <paramref name="own"/> works with: one ahead, since the other side is then the one that is N-1.</summary>
    public static int Newest(int own = Generation) => own + 1;

    public static bool Supports(int peer, int own = Generation) => peer >= Oldest(own) && peer <= Newest(own);

    /// <summary>
    /// Null when the pair is within N and N-1 (or the peer states no generation: it predates the idea and is taken to be in range, which the management API integer still guards); otherwise what to tell the person: both
    /// program versions and generations, how far apart they are, and which side to upgrade (the one with the lower generation).
    /// </summary>
    /// <param name="peer">The generation the other side reported, or null when it reported none.</param>
    /// <param name="peerProgram">The program version the other side reported.</param>
    /// <param name="own">This side's generation.</param>
    /// <param name="ownProgram">This side's program version.</param>
    /// <param name="peerRole">What the other side is called in the message (<c>server</c> when a console asks a server); this side is the other word.</param>
    public static string? Incompatibility(int? peer, string? peerProgram, int own = Generation, string? ownProgram = null, string peerRole = "server")
    {
        if (peer is not { } generation || Supports(generation, own)) return null;
        var ownRole = peerRole == "server" ? "console" : "server";
        var gap = Math.Abs(generation - own);
        var upgrade = generation < own ? $"this {peerRole}" : $"this {ownRole}";
        return $"The {peerRole} (version {peerProgram ?? "unknown"}, compatibility generation {generation.ToString(CultureInfo.InvariantCulture)}) and this {ownRole} (version {ownProgram ?? ProgramVersion}, generation {own.ToString(CultureInfo.InvariantCulture)}) " +
               $"are {gap.ToString(CultureInfo.InvariantCulture)} generations apart; they work together up to 1 apart. Upgrade {upgrade}.";
    }

    /// <summary>Reads a generation header the way it is written; null when there is none or it is not a whole number.</summary>
    public static int? ParseGeneration(string? text) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var generation) && generation >= 1 ? generation : null;

    /// <summary>Makes every response of the server state its compatibility generation and program version, next to the management API version.</summary>
    public static IApplicationBuilder UseSuiteCompatibility(this IApplicationBuilder app, int generation = Generation, string? programVersion = null) =>
        app.Use((context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                context.Response.Headers[GenerationHeader] = generation.ToString(CultureInfo.InvariantCulture);
                context.Response.Headers[ProgramVersionHeader] = programVersion ?? ProgramVersion;
                return Task.CompletedTask;
            });
            return next();
        });
}

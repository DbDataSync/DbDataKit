using System.Diagnostics.CodeAnalysis;

namespace DbDataKit.Runner;

/// <summary>
/// Which task of which package, on which channel (plan 28a R02). The three names follow the manifest's name rule
/// (<see cref="PackageManifestValidator.NameRule"/>), so none can hold the <c>/</c> that separates them in <see cref="ToString"/>:
/// <c>orders-sync/stable/extract</c>. <see cref="Parse"/> reads that form back.
/// </summary>
public sealed record TaskRef
{
    public TaskRef(string package, string channel, string task)
    {
        var problem = Problem(package, channel, task);
        if (problem is not null) throw new ArgumentException(problem);
        Package = package;
        Channel = channel;
        Task = task;
    }

    public string Package { get; }
    public string Channel { get; }
    public string Task { get; }

    /// <summary>What is wrong with the three names, in words, or null.</summary>
    public static string? Problem(string? package, string? channel, string? task)
    {
        foreach (var (what, value) in new[] { ("package", package), ("channel", channel), ("task", task) })
            if (!PackageManifestValidator.NameRule.IsMatch(value ?? ""))
                return $"'{value}' is not a {what} name: use 1 to 64 lowercase letters, digits, '-' or '_', starting with a letter or digit.";
        return null;
    }

    public override string ToString() => $"{Package}/{Channel}/{Task}";

    public static bool TryParse(string? text, [NotNullWhen(true)] out TaskRef? result)
    {
        result = null;
        var parts = (text ?? "").Split('/');
        if (parts.Length != 3 || Problem(parts[0], parts[1], parts[2]) is not null) return false;
        result = new TaskRef(parts[0], parts[1], parts[2]);
        return true;
    }

    /// <summary>Reads <c>package/channel/task</c>; throws a <see cref="FormatException"/> that says what the form is.</summary>
    public static TaskRef Parse(string? text) =>
        TryParse(text, out var result) ? result : throw new FormatException($"'{text}' is not a task reference: write package/channel/task, each a name such as orders-sync/stable/extract.");
}

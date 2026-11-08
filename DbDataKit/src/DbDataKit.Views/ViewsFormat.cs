namespace DbDataKit.Views;

/// <summary>
/// The one format number of <c>dashboard/1</c> and <c>report/1</c> (plan 32 section 3.1, the freeze rule of plan 24a). The shared core (widget types, query forms, layout types, selector
/// conditions) is versioned with both kinds: a change to any of them is the next number of both kinds in one change, and a document is written in the lowest number that can say it.
/// </summary>
public static class ViewsFormat
{
    /// <summary>The number this program writes and reads.</summary>
    public const int Current = 1;

    /// <summary>The oldest number this program still reads (N and N-1, plan 24 section 2.4).</summary>
    public const int Previous = 1;

    /// <summary>The words of a refusal for a document in a number this program does not read, naming the kind, the file and both numbers.</summary>
    public static string Refusal(ViewKind kind, string file, int number)
    {
        var id = kind == ViewKind.Dashboard ? "dashboard" : "report";
        return number > Current
            ? $"the {id} '{file}' is in format {number}, and this program reads format {Current} of {id}. Upgrade this program; the file was written by a newer version."
            : $"the {id} '{file}' is in format {number}, which this program does not read (it reads format {Previous} to {Current} of {id}).";
    }
}

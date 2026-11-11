using DbDataKit.Cli;

namespace DbDataKit.State.Cli;

/// <summary>
/// What <c>state show</c> finds, as values, for the management API (plan 29b, C05): the same read-only inspection (the engine, the connection with every secret masked and every reference as written, the namespaces, each module's
/// version in the registry against the binary, what is pending, where each effective setting came from), so the API and the verb cannot say different things. It opens no <see cref="StateDatabase"/>: nothing is created or migrated.
/// </summary>
public static class StateStatusReader
{
    /// <summary>
    /// Reads the state <paramref name="context"/> describes. <c>Exit</c> is the verb's exit code (0 when the database was read; <see cref="StateExit"/> otherwise, with the sentence in <c>error</c> of the data).
    /// </summary>
    public static async Task<(int Exit, IDictionary<string, object?> Data)> ReadAsync(StateVerbContext context, CancellationToken cancellationToken = default)
    {
        var data = new Dictionary<string, object?>(StringComparer.Ordinal);
        var errors = new StringWriter();
        var output = new CliContext(TextWriter.Null, errors, TextReader.Null, cancellationToken) { Data = data };
        var exit = await StateShow.RunAsync(context, [], output);
        // settings that cannot be used are said on the error stream before any data exists: the sentence is the answer's error
        if (exit != CliExit.Ok && !data.ContainsKey("error") && errors.ToString().Trim() is { Length: > 0 } said) data["error"] = said;
        return (exit, data);
    }
}

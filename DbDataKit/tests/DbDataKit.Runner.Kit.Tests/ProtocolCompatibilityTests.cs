using System.Text;
using DbDataKit.Runner.Protocol.V1;
using Google.Protobuf.Reflection;

namespace DbDataKit.Runner.Kit.Tests;

/// <summary>
/// The compatibility rule of <c>dbdatakit.runner.v1</c> (plan 28a DR4) as a snapshot: every message field (number, name, type), enum value and call of the .proto, written as text. Inside v1 nothing is removed,
/// renumbered, renamed or retyped; a new field takes a new number. The checked-in snapshot is what published packages were built against.
/// </summary>
internal static class ProtocolSnapshot
{
    public const string FileName = "runner.v1.snapshot.txt";

    public const string UpdateVariable = "DBKIT_UPDATE_PROTOCOL_SNAPSHOT";

    /// <summary>One line per message field, enum value and call: <c>key = value</c>.</summary>
    public static IReadOnlyList<(string Key, string Value)> Describe(FileDescriptor file)
    {
        var lines = new List<(string, string)>();
        foreach (var message in file.MessageTypes)
        {
            lines.Add(($"message {message.Name}", "present"));
            foreach (var field in message.Fields.InFieldNumberOrder())
                lines.Add(($"message {message.Name} field {field.FieldNumber}", $"{field.Name} {TypeOf(field)}{(field.ContainingOneof is { } oneof ? $" oneof {oneof.Name}" : "")}"));
        }

        foreach (var enumeration in file.EnumTypes)
        {
            lines.Add(($"enum {enumeration.Name}", "present"));
            foreach (var value in enumeration.Values) lines.Add(($"enum {enumeration.Name} value {value.Number}", value.Name));
        }

        foreach (var service in file.Services)
            foreach (var method in service.Methods)
                lines.Add(($"service {service.Name} rpc {method.Name}", $"({(method.IsClientStreaming ? "stream " : "")}{method.InputType.Name}) returns ({(method.IsServerStreaming ? "stream " : "")}{method.OutputType.Name})"));
        return lines;
    }

    private static string TypeOf(FieldDescriptor field)
    {
        if (field.IsMap) return $"map<{ScalarOrNamed(field.MessageType.FindFieldByNumber(1))},{ScalarOrNamed(field.MessageType.FindFieldByNumber(2))}>";
        return (field.IsRepeated ? "repeated " : "") + ScalarOrNamed(field);
    }

    private static string ScalarOrNamed(FieldDescriptor field) => field.FieldType switch
    {
        FieldType.Message => "message:" + field.MessageType.Name,
        FieldType.Enum => "enum:" + field.EnumType.Name,
        var scalar => scalar.ToString().ToLowerInvariant(),
    };

    public static string Write(IReadOnlyList<(string Key, string Value)> lines)
    {
        var text = new StringBuilder();
        text.AppendLine("# The runner protocol dbdatakit.runner.v1: every message field, enum value and call, as the published packages know them.");
        text.AppendLine("# Inside v1 nothing here is removed, renumbered, renamed or retyped (plan 28a DR4); a new field takes a new number.");
        text.AppendLine($"# Regenerate after an ADDITION (never to hide a removal): {UpdateVariable}=1 dotnet test --project DbDataKit/tests/DbDataKit.Runner.Kit.Tests -- --filter-class '*ProtocolCompatibilityTests'");
        foreach (var (key, value) in lines) text.AppendLine($"{key} = {value}");
        return text.ToString();
    }

    public static Dictionary<string, string> Parse(string text)
    {
        var lines = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0 || line[0] == '#') continue;
            var at = line.IndexOf(" = ", StringComparison.Ordinal);
            if (at < 0) throw new FormatException($"A line of the snapshot is not 'key = value': {line}");
            lines[line[..at]] = line[(at + 3)..];
        }

        return lines;
    }

    /// <summary>What the current .proto lost or changed since the snapshot: a field, enum value or call that is gone (removed or renumbered), or that has another name or type.</summary>
    public static IReadOnlyList<string> Incompatibilities(IReadOnlyDictionary<string, string> snapshot, IReadOnlyList<(string Key, string Value)> current)
    {
        var now = current.ToDictionary(l => l.Key, l => l.Value, StringComparer.Ordinal);
        var problems = new List<string>();
        foreach (var (key, value) in snapshot)
        {
            if (!now.TryGetValue(key, out var found)) problems.Add($"'{key}' ({value}) is gone: it was removed or renumbered.");
            else if (found != value) problems.Add($"'{key}' was '{value}' and is now '{found}': it was renamed or retyped.");
        }

        return problems;
    }

    /// <summary>What the current .proto has that the snapshot does not: new fields, values and calls (compatible, but the snapshot must list them).</summary>
    public static IReadOnlyList<string> Additions(IReadOnlyDictionary<string, string> snapshot, IReadOnlyList<(string Key, string Value)> current) =>
        [.. current.Where(l => !snapshot.ContainsKey(l.Key)).Select(l => $"'{l.Key}' = {l.Value}")];

    public static string? SourcePath()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "DbDataKit.Runner.Kit.Tests.csproj"))) return Path.Combine(directory.FullName, "Protocol", FileName);
        return null;
    }
}

public sealed class ProtocolCompatibilityTests
{
    private static IReadOnlyList<(string Key, string Value)> Current => ProtocolSnapshot.Describe(RunnerV1Reflection.Descriptor);

    private static Dictionary<string, string> Checked() => ProtocolSnapshot.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Protocol", ProtocolSnapshot.FileName)));

    [Fact]
    public void A_field_value_or_call_of_the_checked_in_snapshot_is_never_removed_renumbered_renamed_or_retyped()
    {
        if (Environment.GetEnvironmentVariable(ProtocolSnapshot.UpdateVariable) == "1") return;      // the update test writes the file; a removal is still reviewed in the diff

        var problems = ProtocolSnapshot.Incompatibilities(Checked(), Current);

        Assert.True(problems.Count == 0,
            "The runner protocol dbdatakit.runner.v1 changed incompatibly (published packages speak it, plan 28a DR4). Add a new field with a new number, or serve a v2 beside v1:\n  " + string.Join("\n  ", problems));
    }

    [Fact]
    public void The_snapshot_lists_every_field_value_and_call_of_the_proto()
    {
        if (Environment.GetEnvironmentVariable(ProtocolSnapshot.UpdateVariable) == "1")
        {
            var path = ProtocolSnapshot.SourcePath() ?? throw new InvalidOperationException("The test project's folder was not found from " + AppContext.BaseDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, ProtocolSnapshot.Write(Current));
            return;
        }

        var added = ProtocolSnapshot.Additions(Checked(), Current);

        Assert.True(added.Count == 0,
            $"The .proto has what the snapshot does not (a compatible addition). Regenerate the snapshot with the explicit command {ProtocolSnapshot.UpdateVariable}=1 dotnet test --project DbDataKit/tests/DbDataKit.Runner.Kit.Tests -- --filter-class '*ProtocolCompatibilityTests' and review the diff:\n  "
            + string.Join("\n  ", added));
    }

    [Fact]
    public void The_check_catches_a_removed_a_renumbered_and_a_retyped_field()
    {
        var text = ProtocolSnapshot.Write(Current);

        // removed: the line of a field is gone from the current proto (here: gone from what the proto "is")
        var removed = Current.Where(l => l.Key != "message StartInfo field 5").ToList();
        var problem = Assert.Single(ProtocolSnapshot.Incompatibilities(ProtocolSnapshot.Parse(text), removed));
        Assert.Contains("message StartInfo field 5", problem, StringComparison.Ordinal);
        Assert.Contains("removed or renumbered", problem, StringComparison.Ordinal);

        // renumbered: field 5 of StartInfo (package) becomes 55
        var renumbered = Current.Select(l => l.Key == "message StartInfo field 5" ? ("message StartInfo field 55", l.Value) : l).ToList();
        Assert.Contains("message StartInfo field 5", Assert.Single(ProtocolSnapshot.Incompatibilities(ProtocolSnapshot.Parse(text), renumbered)), StringComparison.Ordinal);
        Assert.Contains("message StartInfo field 55", Assert.Single(ProtocolSnapshot.Additions(ProtocolSnapshot.Parse(text), renumbered)), StringComparison.Ordinal);

        // retyped
        var retyped = Current.Select(l => l.Key == "message StartInfo field 3" ? (l.Key, "attempt string") : l).ToList();
        Assert.Contains("renamed or retyped", Assert.Single(ProtocolSnapshot.Incompatibilities(ProtocolSnapshot.Parse(text), retyped)), StringComparison.Ordinal);

        // added: compatible
        var added = Current.Append(("message StartInfo field 99", "extra string")).ToList();
        Assert.Empty(ProtocolSnapshot.Incompatibilities(ProtocolSnapshot.Parse(text), added));
        Assert.Single(ProtocolSnapshot.Additions(ProtocolSnapshot.Parse(text), added));
    }

    [Fact]
    public void The_proto_names_the_calls_of_plan_28b_section_9()
    {
        var calls = RunnerV1Reflection.Descriptor.Services.Single().Methods.Select(m => m.Name).ToHashSet();

        foreach (var call in new[]
        {
            "Start", "Advance", "BeginStep", "CompleteStep", "StepChunk", "ReadPayload", "BeginUnit", "UnitProgress", "EndUnit", "ClaimFile", "CompleteFile", "TakeInbox", "CompleteInbox", "ReleaseInbox",
            "Reject", "Report", "Heartbeat", "WatchCancel", "End",
        })
            Assert.Contains(call, calls);
        Assert.Equal("dbdatakit.runner.v1", RunnerV1Reflection.Descriptor.Package);
        var chunk = RunnerV1Reflection.Descriptor.Services.Single().FindMethodByName("StepChunk");
        Assert.True(chunk.IsClientStreaming && !chunk.IsServerStreaming);               // the upload is a client stream
        var read = RunnerV1Reflection.Descriptor.Services.Single().FindMethodByName("ReadPayload");
        Assert.True(read.IsServerStreaming && !read.IsClientStreaming);                 // the read-back is a server stream
    }
}

using DbDataKit.Runner.Protocol.V1;
using DbDataKit.State;

namespace DbDataKit.Runner.Kit;

/// <summary>The conversions between the kit's and the stores' types and the messages of <c>dbdatakit.runner.v1</c>, used by the client (<see cref="GrpcRunBackend"/>) and by the server beside the stores.</summary>
internal static class WireConvert
{
    // ---- progress values

    public static ProgressValueMessage ToWire(ProgressValue value) => new() { Type = ToWire(value.Type), Text = value.Canonical() };

    public static ProgressValue FromWire(ProgressValueMessage message) => ProgressValue.Parse(FromWire(message.Type), message.Text);

    public static ProgressValueType ToWire(ProgressType type) => type switch
    {
        ProgressType.Text => ProgressValueType.Text,
        ProgressType.Int => ProgressValueType.Int,
        ProgressType.Decimal => ProgressValueType.Decimal,
        ProgressType.Time => ProgressValueType.Time,
        ProgressType.Date => ProgressValueType.Date,
        ProgressType.Bool => ProgressValueType.Bool,
        ProgressType.Bytes => ProgressValueType.Bytes,
        ProgressType.Json => ProgressValueType.Json,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "A progress type has a wire type."),
    };

    public static ProgressType FromWire(ProgressValueType type) => type switch
    {
        ProgressValueType.Text => ProgressType.Text,
        ProgressValueType.Int => ProgressType.Int,
        ProgressValueType.Decimal => ProgressType.Decimal,
        ProgressValueType.Time => ProgressType.Time,
        ProgressValueType.Date => ProgressType.Date,
        ProgressValueType.Bool => ProgressType.Bool,
        ProgressValueType.Bytes => ProgressType.Bytes,
        ProgressValueType.Json => ProgressType.Json,
        _ => throw new ArgumentException($"The progress value type {(int)type} is not one this protocol version knows."),
    };

    public static IReadOnlyDictionary<string, ProgressValue> FromWire(IEnumerable<KeyValuePair<string, ProgressValueMessage>> parts) =>
        parts.ToDictionary(p => p.Key, p => FromWire(p.Value), StringComparer.Ordinal);

    // ---- counts

    public static IReadOnlyDictionary<string, long>? CountsFromWire(IReadOnlyDictionary<string, long> counts) =>
        counts.Count == 0 ? null : new Dictionary<string, long>(counts, StringComparer.Ordinal);

    public static void AddTo(Google.Protobuf.Collections.MapField<string, long> field, IReadOnlyDictionary<string, long>? counts)
    {
        if (counts is null) return;
        foreach (var (name, n) in counts) field[name] = n;
    }

    // ---- the end message

    public static TaskEndMessage ToWire(TaskEnd end)
    {
        var message = new TaskEndMessage
        {
            State = end.State switch
            {
                TaskEndState.Succeeded => EndState.Succeeded,
                TaskEndState.CompletedWithFailures => EndState.CompletedWithFailures,
                TaskEndState.Failed => EndState.Failed,
                TaskEndState.Stopped => EndState.Stopped,
                _ => EndState.Unspecified,
            },
            Processed = end.Processed,
            FailedItems = end.FailedItems,
            FirstFailedItem = end.FirstFailedItem ?? "",
        };
        if (end.Failure is { } failure)
            message.Failure = new Failure { Kind = ToWire(failure.Kind), Message = failure.Message ?? "", Item = failure.Item ?? "", Code = failure.Code ?? "" };
        foreach (var (name, output) in end.OutputsOrEmpty)
        {
            var wire = new Output();
            if (output.PayloadId is not null) wire.PayloadId = output.PayloadId;
            else if (output.Text is not null) wire.Text = output.Text;
            message.Outputs[name] = wire;
        }

        return message;
    }

    public static TaskEnd FromWire(TaskEndMessage message)
    {
        var state = message.State switch
        {
            EndState.Succeeded => TaskEndState.Succeeded,
            EndState.CompletedWithFailures => TaskEndState.CompletedWithFailures,
            EndState.Failed => TaskEndState.Failed,
            EndState.Stopped => TaskEndState.Stopped,
            _ => throw new ArgumentException($"The end state {(int)message.State} is not one this protocol version knows."),
        };
        TaskFailure? failure = null;
        if (message.Failure is { } wire)
            failure = new TaskFailure(FromWire(wire.Kind), wire.Message, wire.Item.Length == 0 ? null : wire.Item, wire.Code.Length == 0 ? null : wire.Code);
        Dictionary<string, TaskOutput>? outputs = null;
        if (message.Outputs.Count > 0)
        {
            outputs = new Dictionary<string, TaskOutput>(StringComparer.Ordinal);
            foreach (var (name, output) in message.Outputs)
                outputs[name] = output.ValueCase switch
                {
                    Output.ValueOneofCase.PayloadId => TaskOutput.Payload(output.PayloadId),
                    Output.ValueOneofCase.Text => TaskOutput.FromText(output.Text),
                    _ => new TaskOutput(null, null),
                };
        }

        return new TaskEnd(state, message.Processed, message.FailedItems, message.FirstFailedItem.Length == 0 ? null : message.FirstFailedItem, failure, outputs);
    }

    private static FailureKind ToWire(TaskFailureKind kind) => kind switch
    {
        TaskFailureKind.Temporary => FailureKind.Temporary,
        TaskFailureKind.Permanent => FailureKind.Permanent,
        TaskFailureKind.Cancelled => FailureKind.Cancelled,
        TaskFailureKind.TimedOut => FailureKind.TimedOut,
        TaskFailureKind.ProcessExited => FailureKind.ProcessExited,
        TaskFailureKind.ProtocolViolation => FailureKind.ProtocolViolation,
        TaskFailureKind.LimitExceeded => FailureKind.LimitExceeded,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "A failure kind has a wire kind."),
    };

    private static TaskFailureKind FromWire(FailureKind kind) => kind switch
    {
        FailureKind.Temporary => TaskFailureKind.Temporary,
        FailureKind.Permanent => TaskFailureKind.Permanent,
        FailureKind.Cancelled => TaskFailureKind.Cancelled,
        FailureKind.TimedOut => TaskFailureKind.TimedOut,
        FailureKind.ProcessExited => TaskFailureKind.ProcessExited,
        FailureKind.ProtocolViolation => TaskFailureKind.ProtocolViolation,
        FailureKind.LimitExceeded => TaskFailureKind.LimitExceeded,
        _ => throw new ArgumentException($"The failure kind {(int)kind} is not one this protocol version knows."),
    };

    // ---- ids

    public static Guid ParseId(string text, string what) =>
        Guid.TryParse(text, out var id) ? id : throw new ArgumentException($"'{text}' is not a valid {what} (a UUID).");
}

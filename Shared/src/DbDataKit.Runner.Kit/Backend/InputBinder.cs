using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DbDataKit.Runner.Kit;

/// <summary>
/// Binds the protocol's inputs (names and values as strings) to a job's typed input record, and back (plan 28b section 9, slice R03b). An input is named in snake case after the record's property
/// (<c>Day</c> is <c>day</c>, <c>OrdersSince</c> is <c>orders_since</c>, as the generated manifest names it). A value is the text of its JSON form where that is a number, a boolean, an array or an object, and the plain
/// text where the type is a string, a date, a time, a GUID or an enum. A required input that is missing, an input the record does not declare and a value of the wrong form are errors that name the input.
/// </summary>
public static class InputBinder
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    private static IEnumerable<PropertyInfo> PropertiesOf(Type input) =>
        input.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanRead && p.GetIndexParameters().Length == 0 && p.DeclaringType == input && p.Name != "EqualityContract");

    /// <summary>The input record as the strings the protocol carries: only inputs that have a value.</summary>
    public static IReadOnlyDictionary<string, string> ToStrings(object? input)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (input is null or NoInput) return result;
        foreach (var property in PropertiesOf(input.GetType()))
        {
            var value = property.GetValue(input);
            if (value is null) continue;
            if (value is string text)
            {
                result[Naming.Snake(property.Name)] = text;
                continue;
            }

            JsonNode? node;
            try
            {
                node = JsonSerializer.SerializeToNode(value, property.PropertyType, Json);
            }
            catch (Exception ex) when (ex is NotSupportedException or JsonException)
            {
                throw new ArgumentException($"The input '{Naming.Snake(property.Name)}' ({property.PropertyType.Name}) cannot be written as text: {ex.Message}", nameof(input), ex);
            }

            result[Naming.Snake(property.Name)] = node is JsonValue { } scalar && scalar.TryGetValue<string>(out var s) ? s : node?.ToJsonString(Json) ?? "null";
        }

        return result;
    }

    /// <summary>The typed input record for <paramref name="inputs"/>.</summary>
    public static object FromStrings(Type inputType, IReadOnlyDictionary<string, string> inputs)
    {
        ArgumentNullException.ThrowIfNull(inputType);
        ArgumentNullException.ThrowIfNull(inputs);
        if (inputType == typeof(NoInput))
        {
            if (inputs.Count > 0) throw new ArgumentException($"The task declares no inputs, and the run was given {string.Join(", ", inputs.Keys.Select(k => $"'{k}'"))}.");
            return new NoInput();
        }

        var properties = PropertiesOf(inputType).ToDictionary(p => Naming.Snake(p.Name), StringComparer.Ordinal);
        foreach (var name in inputs.Keys)
            if (!properties.ContainsKey(name))
                throw new ArgumentException($"The input '{name}' is not declared by {inputType.Name} (its inputs: {string.Join(", ", properties.Keys.Select(k => $"'{k}'"))}).");

        var constructor = inputType.GetConstructors().OrderByDescending(c => c.GetParameters().Length).FirstOrDefault();
        var nullability = new NullabilityInfoContext();
        var node = new JsonObject();
        foreach (var (name, property) in properties)
        {
            if (!inputs.TryGetValue(name, out var text))
            {
                var parameter = constructor?.GetParameters().FirstOrDefault(p => string.Equals(p.Name, property.Name, StringComparison.Ordinal));
                var optional = Nullable.GetUnderlyingType(property.PropertyType) is not null || nullability.Create(property).ReadState == NullabilityState.Nullable || (parameter?.HasDefaultValue ?? false);
                if (!optional) throw new ArgumentException($"The input '{name}' ({property.PropertyType.Name}) is required by {inputType.Name} and the run gave no value.");
                continue;
            }

            node[property.Name] = Parse(name, property.PropertyType, text);
        }

        try
        {
            return node.Deserialize(inputType, Json) ?? throw new ArgumentException($"The inputs do not make a {inputType.Name}.");
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"The inputs do not make a {inputType.Name}: {ex.Message}", ex);
        }
    }

    private static JsonNode? Parse(string name, Type type, string text)
    {
        var target = Nullable.GetUnderlyingType(type) ?? type;
        if (target == typeof(string)) return JsonValue.Create(text);
        try
        {
            var asJson = JsonNode.Parse(text);
            JsonSerializer.Deserialize(asJson, type, Json);     // does the value fit the type?
            return asJson;
        }
        catch (JsonException)
        {
            // not JSON of that type: the plain text of a date, a GUID, an enum
        }

        try
        {
            var quoted = JsonValue.Create(text);
            JsonSerializer.Deserialize(quoted, type, Json);
            return quoted;
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"The input '{name}' is '{text}', which is not a {target.Name}: {ex.Message}", ex);
        }
    }
}

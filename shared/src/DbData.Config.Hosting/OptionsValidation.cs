using System.Collections;
using System.Globalization;
using System.Text.RegularExpressions;
using DbData.Config.Yaml;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DbData.Config.Hosting;

/// <summary>
/// <c>IValidateOptions</c> that checks a bound options object against its schema attributes (required, range, pattern) and runs every registered
/// <see cref="IConfigValidator{T}"/>, collecting all failures at once. Failures carry file:line when the value came from a DbData YAML provider.
/// </summary>
public sealed class ConfigSchemaValidator<T>(IEnumerable<IConfigValidator<T>> validators, IConfiguration? configuration = null) : IValidateOptions<T> where T : class
{
    public ValidateOptionsResult Validate(string? name, T options)
    {
        var origins = configuration?.GetOrigins() ?? new Dictionary<string, KeyOrigin>();
        var failures = new List<string>();
        var cx = new Context(origins, failures);
        Walk(SchemaModel.For(typeof(T)), options, "", cx);
        foreach (var v in validators) v.Validate(options, cx);
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private sealed class Context(IReadOnlyDictionary<string, KeyOrigin> origins, List<string> failures) : IConfigValidationContext
    {
        public void Error(string path, string message, string? fix = null) => Add("error", path, message, fix);
        public void Warning(string path, string message, string? fix = null) { }   // warnings do not fail startup

        public void Add(string kind, string path, string message, string? fix)
        {
            var key = path.Replace("].", ":").Replace("[", ":").Replace("]", "").Replace('.', ':');
            var where = "";
            for (var k = key; k.Length > 0 && where.Length == 0; k = k.Contains(':') ? k[..k.LastIndexOf(':')] : "")
                if (origins.TryGetValue(k, out var o)) where = $"{o.File}:{o.Line}: ";
            failures.Add($"{where}{(path.Length > 0 ? path + ": " : "")}{message}{(fix != null ? " (" + fix + ")" : "")}");
        }
    }

    private static void Walk(TypeSchema schema, object? value, string path, Context cx)
    {
        if (value == null) return;
        switch (schema)
        {
            case ObjectSchema o:
                foreach (var p in o.Properties)
                {
                    var v = p.Property.GetValue(value);
                    var child = path.Length == 0 ? p.Key : path + "." + p.Key;
                    if (p.Required && (v == null || v is string { Length: 0 })) { cx.Add("error", child, $"`{p.Key}` is required.", null); continue; }
                    if (v is null) continue;
                    if ((p.Min != null || p.Max != null) && v is IConvertible and not string and not bool and not Enum)
                    {
                        var d = Convert.ToDouble(v, CultureInfo.InvariantCulture);
                        if (p.Min != null && d < p.Min) cx.Add("error", child, $"`{p.Key}` is {d}; the minimum is {p.Min}.", null);
                        if (p.Max != null && d > p.Max) cx.Add("error", child, $"`{p.Key}` is {d}; the maximum is {p.Max}.", null);
                    }
                    if (p.Pattern != null && v is string s && !Regex.IsMatch(s, "^(?:" + p.Pattern + ")$"))
                        cx.Add("error", child, $"`{p.Key}` is `{s}`, which does not match `{p.Pattern}`.", null);
                    Walk(p.Type, v, child, cx);
                }
                break;
            case ListSchema l when value is IEnumerable items:
                var i = 0;
                foreach (var item in items) Walk(l.Item, item, $"{path}[{i++}]", cx);
                break;
            case MapSchema m when value is IDictionary dict:
                foreach (DictionaryEntry e in dict) Walk(m.Value, e.Value, $"{path}.{e.Key}", cx);
                break;
            case UnionSchema u:
                if (u.Cases.Values.FirstOrDefault(c => c.ClrType == value.GetType()) is { } cs) Walk(cs, value, path, cx);
                break;
        }
    }
}

public static class DbDataOptionsExtensions
{
    /// <summary>Bind, validate against the schema attributes and registered <see cref="IConfigValidator{T}"/>s, and validate on start so every problem is reported at once.</summary>
    public static OptionsBuilder<T> AddDbDataOptions<T>(this IServiceCollection services, IConfiguration section) where T : class
    {
        services.AddSingleton<IValidateOptions<T>>(sp => new ConfigSchemaValidator<T>(sp.GetServices<IConfigValidator<T>>(), section is IConfigurationRoot ? section : sp.GetService<IConfiguration>()));
        return services.AddOptions<T>().Bind(section).ValidateOnStart();
    }
}

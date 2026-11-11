using System.Globalization;

namespace DbDataKit.Lineage;

/// <summary>The answers of <see cref="DeploymentQueries"/> as plain dictionaries and lists (snake_case keys, times as ISO text): the one document shape the command line and the console both give.</summary>
public static class DeploymentDocuments
{
    private static string Time(DateTimeOffset at) => at.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static Dictionary<string, object?> Compact(Dictionary<string, object?> d) => d.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value);

    public static Dictionary<string, object?> Of(ChannelDeployment c) => Compact(new()
    {
        ["group"] = c.Group, ["kind"] = c.Kind, ["name"] = c.Name, ["channel"] = c.Channel,
        ["active"] = c.Active, ["previous"] = c.Previous, ["staged"] = c.Staged,
        ["last_event"] = c.LastEvent, ["last_by"] = c.LastBy, ["last_at"] = c.LastAt is { } at ? Time(at) : null, ["history"] = c.History,
        ["instances"] = c.Instances.Select(i => Compact(new Dictionary<string, object?>
        {
            ["instance"] = i.InstanceId, ["host"] = i.Host, ["version"] = i.Version, ["state"] = i.State, ["reported_at"] = Time(i.At), ["error"] = i.Error,
            ["runtime"] = i.Runtime, ["live"] = i.Live && !i.Gone, ["on_active"] = c.Active == i.Version && i.State == "active",
        })).ToList(),
    });

    public static Dictionary<string, object?> Of(DeploymentOfInstance r) => Compact(new()
    {
        ["instance"] = r.InstanceId, ["group"] = r.Group, ["host"] = r.Host, ["runtime"] = r.Runtime, ["runner_version"] = r.RunnerVersion, ["live"] = r.Live,
        ["holdings"] = r.Holdings.Select(h => Compact(new Dictionary<string, object?>
        {
            ["kind"] = h.Kind, ["name"] = h.Name, ["channel"] = h.Channel, ["version"] = h.Version, ["state"] = h.State, ["reported_at"] = Time(h.At), ["error"] = h.Error, ["channel_active"] = h.ChannelActive,
            ["behind"] = h.ChannelActive is { } active && (h.Version != active || h.State != "active"),
        })).ToList(),
    });
}

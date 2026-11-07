using DbDataKit.Cli;
using DbDataKit.Identity;
using DbDataKit.Secrets;
using DbDataKit.State;
using DbDataProxy.Server.Configuration;
using DbDataProxy.Server.Secrets;
using DbDataProxy.Server.State;

namespace DbDataProxy.Server.Hosting;

/// <summary>
/// <c>dbdataproxy identity ...</c> (plan 20, O06): the shared administration verbs of the identity home (<see cref="IdentityVerbs"/>) on the home this proxy serves, which is in its state database. They work with the
/// server stopped or running. <c>--repo</c> names the data directory, as for the other verbs.
/// </summary>
public static class IdentityCommand
{
    public static Noun Noun() => new("identity", "The identity home this proxy serves: register consoles and apps, invite people, give them roles",
        [.. IdentityVerbs.Admin(new IdentityAdminLocation("[--repo <data directory>]", Open, parsed => ProxyIdentityModule.PublicUrlOf(OptionsOf(parsed)).AbsoluteUri,
            parsed => new AccessTokenPolicy(OptionsOf(parsed).Identity.AccessTokenMaxDays)))]);

    private static ProxyOptions OptionsOf(CliArgs parsed)
    {
        var directory = parsed.Get("repo") is { Length: > 0 } repo ? Path.GetFullPath(repo) : null;
        var options = ProxyConfiguration.LoadOptions(directory);
        if (directory is not null) options.DataDirectory = directory;
        return options;
    }

    /// <summary>The state database the server uses, opened the same way (the password of a server engine resolved from its reference).</summary>
    internal static StateDatabase Open(CliArgs parsed) => OpenState(OptionsOf(parsed));

    /// <summary>The state database of the server these options describe, opened as the server opens it.</summary>
    internal static StateDatabase OpenState(ProxyOptions options)
    {
        var resolver = new SecretResolverChain(SecretsRuntime.CreateProviders(options, SecretsRuntime.OpenStore(options)));
        return ProxyState.Open(ProxyHost.ResolveDataDirectory(options), options.State.ToSettings(),
            text => ConnectionStringReferences.ResolveAsync(text, resolver, ConnectionStringReferences.QuoteDouble).AsTask().GetAwaiter().GetResult());
    }
}

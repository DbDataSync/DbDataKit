using DbDataKit.Identity;
using DbDataKit.Secrets;
using DbDataKit.State;
using DbDataGateway.Server.Configuration;
using DbDataGateway.Server.Hosting;
using DbDataGateway.Server.Secrets;
using Microsoft.Extensions.Options;

namespace DbDataGateway.Server.Serving;

/// <summary>
/// The identity home's people as this instance recognises them (plan 20 O08, plan 13a P13): one <see cref="StoreCallers"/>, shared by the store module and by the data clients (Flight SQL), so a personal access token, a home access token and the home's
/// groups are read in one place. The home's access tokens are checked against its signing keys when this instance holds their key (the identity module's secret); without it only personal access tokens work. Nothing is made here: a key
/// that appears later (the identity module starts) is looked for again after <see cref="Retry"/>.
/// </summary>
public sealed class HomeCallers(IOptionsMonitor<GatewayOptions> options, StateDatabase state, TimeProvider? time = null)
{
    /// <summary>How long a missing signing key is believed before this instance looks again.</summary>
    public static readonly TimeSpan Retry = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private StoreCallers? _callers;
    private DateTimeOffset _lookAgainAt;

    public async ValueTask<StoreCallers> GetAsync(CancellationToken cancellationToken = default)
    {
        var now = _time.GetUtcNow();
        if (_callers is { } cached && now < _lookAgainAt) return cached;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            now = _time.GetUtcNow();
            if (_callers is { } again && now < _lookAgainAt) return again;
            var current = options.CurrentValue;
            var provider = await ProviderAsync(current, cancellationToken).ConfigureAwait(false);
            _callers = new StoreCallers(state, () => GatewayStoreModule.ServerIdOf(options.CurrentValue, state), provider);
            // a key held is held for good; without one, ask again soon
            _lookAgainAt = provider is null ? now + Retry : DateTimeOffset.MaxValue;
            return _callers;
        }
        finally { _gate.Release(); }
    }

    private async Task<OpenIdProvider?> ProviderAsync(GatewayOptions current, CancellationToken cancellationToken)
    {
        using var held = await SecretsRuntime.OpenStore(current).TryResolveAsync(SecretRef.Parse("secret:" + GatewayIdentityModule.SigningKeySecret), cancellationToken).ConfigureAwait(false);
        if (held is null) return null;
        var users = new IdentityUserStore(state);
        return new OpenIdProvider(state, users, new HomeDirectory(state), new OidcClients(state), new SigningKeys(state, KeySigningKeyProtector.FromBase64(held.RevealString())),
            GatewayIdentityModule.PublicUrlOf(current).AbsoluteUri);
    }
}

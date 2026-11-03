namespace DbDataKit.Web.Trust;

/// <summary>Where a server keeps the clients paired with it: its <c>server.yaml</c> (<see cref="PairedClientsFile"/>), or a group's state (<see cref="StatePairedClients"/>, plan 20 O04).</summary>
public interface IPairedClientStore : IPairedClients
{
    IReadOnlyList<PairedClient> All();

    /// <summary>Adds the client, or replaces the one with the same id (pairing again with a new key).</summary>
    void Upsert(PairedClient client);

    /// <summary>Removes the client with this id; false when there was none.</summary>
    bool Remove(string clientId);

    /// <summary>What was wrong with entries that were skipped.</summary>
    IReadOnlyList<string> Problems { get; }
}

/// <summary>Where a server keeps the pairing codes waiting to be redeemed: a file (<see cref="PairingCodes"/>), or a group's state (<see cref="StatePairingCodes"/>, plan 20 O04).</summary>
public interface IPairingCodeStore
{
    PairingCode Create(ServerRole ceiling, string? label = null, TimeSpan? lifetime = null);

    /// <summary>Spends the code: the grant it carries, or null when it is wrong, expired or used. A wrong code counts against <see cref="PairingCodes.MaxFailures"/>.</summary>
    PairingGrant? Redeem(string? code);

    /// <summary>Spends the code that <paramref name="proves"/> accepts, given each waiting code's key (<see cref="PairingCodes.RedeemProof"/>).</summary>
    PairingGrant? RedeemProof(Func<byte[], bool> proves);

    /// <summary>How many codes are waiting (unexpired, unused).</summary>
    int Pending();
}

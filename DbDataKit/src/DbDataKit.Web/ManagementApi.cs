namespace DbDataKit.Web.Trust;

/// <summary>Where a server's management API starts and which version of it this code speaks (plan 13, D17: it is reported at pairing and on every call).</summary>
public static class ManagementApi
{
    public const int Version = 1;
    public const string PairingPath = "/management/v1/pairing";
}

using DbDataKit.Delivery.Email;

namespace DbDataKit.Delivery;

/// <summary>The kinds of channel the library ships.</summary>
public static class DeliveryKinds
{
    /// <summary>A registry with every kind of the library: <c>email</c> (GP08), and, as they are added, <c>teams</c>, <c>slack</c> and <c>webhook</c> (GP09).</summary>
    public static ChannelKinds Default()
    {
        var kinds = new ChannelKinds();
        kinds.Register(EmailChannel.Describe);
        return kinds;
    }
}

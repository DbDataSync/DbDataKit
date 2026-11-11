using DbDataKit.Delivery.Email;
using DbDataKit.Delivery.Webhooks;

namespace DbDataKit.Delivery;

/// <summary>The kinds of channel the library ships.</summary>
public static class DeliveryKinds
{
    /// <summary>A registry with every kind of the library: <c>email</c> (GP08), <c>teams</c>, <c>slack</c> and the signed <c>webhook</c> (GP09).</summary>
    public static ChannelKinds Default()
    {
        var kinds = new ChannelKinds();
        kinds.Register(EmailChannel.Describe).Register(TeamsChannel.Describe).Register(SlackChannel.Describe).Register(SignedWebhookChannel.Describe);
        return kinds;
    }
}

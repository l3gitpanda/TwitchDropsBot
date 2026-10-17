namespace TwitchDropsBot.Core.Platform.Shared.Exceptions;

/// <summary>Ends a watch because a campaign of a priority game went live.</summary>
[Serializable]
public class PriorityCampaignLive : System.Exception
{
    private const string DefaultMessage = "A priority campaign is live.";

    public PriorityCampaignLive() : base(DefaultMessage) { }
    public PriorityCampaignLive(string message) : base(message) { }
    public PriorityCampaignLive(string message, System.Exception inner) : base(message, inner) { }
}

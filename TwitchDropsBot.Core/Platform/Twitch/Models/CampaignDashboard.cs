using TwitchDropsBot.Core.Platform.Twitch.Models.Abstractions;

namespace TwitchDropsBot.Core.Platform.Twitch.Models;

/// <summary>What the drops dashboard offers: campaigns running now, and drop campaigns that haven't started.</summary>
public record CampaignDashboard(List<AbstractCampaign> Active, List<AbstractCampaign> Upcoming);

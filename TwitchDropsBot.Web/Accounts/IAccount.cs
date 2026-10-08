using TwitchDropsBot.Core.Platform.Shared.Bots;
using TwitchDropsBot.Core.Platform.Shared.Services;
using TwitchDropsBot.Core.Platform.Twitch.Bot;
using TwitchDropsBot.Core.Platform.Twitch.Models;
using TwitchDropsBot.Core.Platform.Twitch.Models.Abstractions;

namespace TwitchDropsBot.Web.Accounts;

/// <summary>
/// One farming account as the web UI sees it: a running bot, or a simulated one in demo mode.
/// </summary>
public interface IAccount
{
    string Id { get; }
    string Login { get; }

    BotStatus Status { get; }
    BotIdleReason IdleReason { get; }
    DateTime? NextCycleAt { get; }
    string? LastError { get; }
    DateTime? LastErrorAt { get; }

    AbstractCampaign? CurrentCampaign { get; }
    TimeBasedDrop? CurrentTimeBasedDrop { get; }
    User? CurrentBroadcaster { get; }
    int? CurrentMinutesWatched { get; }
    int? RequiredMinutesWatched { get; }
    IReadOnlyList<DropProgressInfo>? CurrentDropsProgress { get; }
    AbstractCampaign? CheckingCampaign { get; }

    IReadOnlyList<AbstractCampaign> Campaigns { get; }
    DateTime? CampaignsAt { get; }
    Inventory? Inventory { get; }
    IReadOnlyDictionary<string, CampaignNote> Notes { get; }
    IReadOnlyList<string> FavouriteGames { get; }
    bool OnlyFavouriteGames { get; }
    IReadOnlyList<string> AvoidedCampaigns { get; }

    LogBuffer Logs { get; }

    /// <summary>Box art last seen for a game, for campaigns whose game object no longer carries it.</summary>
    string? KnownBoxArt(string? gameId);

    /// <summary>Fetches the campaign list and inventory now instead of waiting for the next cycle.</summary>
    Task RefreshCampaignsAsync(CancellationToken cancellationToken);

    /// <summary>The campaign with its drops, fetched if this cycle's list doesn't carry them yet.</summary>
    Task<AbstractCampaign?> GetCampaignDetailsAsync(string campaignId, CancellationToken cancellationToken);

    void RequestReselect();
}

public interface IAccountSource
{
    IReadOnlyList<IAccount> Accounts { get; }
    CampaignQueueService Queue { get; }
}

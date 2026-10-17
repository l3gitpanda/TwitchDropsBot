using TwitchDropsBot.Core.Platform.Twitch.Models.Abstractions;

namespace TwitchDropsBot.Core.Platform.Twitch.Bot;

public enum CampaignNoteKind
{
    NoLiveChannel,
    Completed,
    NotEnoughTime,
    NoWatchableDrops,
    NothingLeftOnChannel,
    WatchedNotClaimed,
    CategoryNotFound
}

/// <summary>Why the bot last passed over a campaign, shown in the web UI.</summary>
public record CampaignNote(CampaignNoteKind Kind, DateTime At);

public static class CampaignQueueOrdering
{
    /// <summary>The queued campaigns found among this cycle's campaigns, in queue order.</summary>
    public static List<AbstractCampaign> FindQueued(IEnumerable<AbstractCampaign> campaigns,
        IReadOnlyList<string> queuedCampaignIds)
    {
        var byId = campaigns.GroupBy(x => x.Id).ToDictionary(x => x.Key, x => x.First());

        return queuedCampaignIds
            .Where(byId.ContainsKey)
            .Select(id => byId[id])
            .ToList();
    }

    /// <summary>
    /// This cycle's campaigns of priority games, soonest-ending first. Names compare case-insensitively, as for
    /// favourites; campaigns on AvoidCampaign stay out.
    /// </summary>
    public static List<AbstractCampaign> FindPriority(IEnumerable<AbstractCampaign> campaigns,
        IReadOnlyCollection<string> priorityGames, IReadOnlyCollection<string>? avoidedCampaigns = null)
    {
        if (priorityGames.Count == 0)
        {
            return new List<AbstractCampaign>();
        }

        return campaigns
            .Where(x => IsPriority(x, priorityGames) &&
                        !(avoidedCampaigns?.Contains(x.Name, StringComparer.OrdinalIgnoreCase) ?? false))
            .GroupBy(x => x.Id)
            .Select(x => x.First())
            .OrderBy(x => x.GetEndDate())
            .ToList();
    }

    public static bool IsPriority(AbstractCampaign campaign, IReadOnlyCollection<string> priorityGames)
    {
        var game = campaign.Game?.DisplayName ?? campaign.Game?.Name;
        return game is not null && priorityGames.Contains(game, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether a watch should give way to a live campaign of a priority game: always, unless it is itself watching
    /// one, in which case only for a campaign that ends sooner. The same order a new cycle picks in, so a switch
    /// never bounces back.
    /// </summary>
    public static bool ShouldGiveWay(AbstractCampaign current, AbstractCampaign candidate,
        IReadOnlyCollection<string> priorityGames)
    {
        if (candidate.Id == current.Id || !IsPriority(candidate, priorityGames))
        {
            return false;
        }

        return !IsPriority(current, priorityGames) || candidate.GetEndDate() < current.GetEndDate();
    }

    /// <summary>
    /// Queued campaigns first, in queue order, then the normal order without them.
    /// A queued campaign removed by the favourites or linked-account filters is put back.
    /// </summary>
    public static List<AbstractCampaign> PutFirst(List<AbstractCampaign> ordered, List<AbstractCampaign> queued)
    {
        if (queued.Count == 0)
        {
            return ordered;
        }

        var queuedIds = queued.Select(x => x.Id).ToHashSet();
        return queued.Concat(ordered.Where(x => !queuedIds.Contains(x.Id))).ToList();
    }
}

using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using TwitchDropsBot.Core.Platform.Shared.Bots;
using TwitchDropsBot.Core.Platform.Shared.Services;
using TwitchDropsBot.Core.Platform.Twitch.Bot;
using TwitchDropsBot.Core.Platform.Twitch.Models;
using TwitchDropsBot.Core.Platform.Twitch.Models.Abstractions;
using TwitchDropsBot.Web.Accounts;
using TwitchDropsBot.Web.Api;

namespace TwitchDropsBot.Web;

public record BackendResult(int Status, object? Body = null)
{
    public static BackendResult Ok(object? body = null) => new(StatusCodes.Status200OK, body);
    public static BackendResult NotFound(string error) => new(StatusCodes.Status404NotFound, new ProblemDto(error));
    public static BackendResult Conflict(string error) => new(StatusCodes.Status409Conflict, new ProblemDto(error));
    public static BackendResult BadRequest(string error) => new(StatusCodes.Status400BadRequest, new ProblemDto(error));
}

/// <summary>
/// Turns account state into what the page shows, and applies the page's queue and switch requests.
/// </summary>
public sealed partial class WebBackend
{
    private static readonly TimeSpan EndedQueueGrace = TimeSpan.FromHours(1);
    private static readonly TimeSpan ErrorShownFor = TimeSpan.FromHours(6);

    private readonly IAccountSource _source;
    private readonly ConcurrentDictionary<string, string> _boxArtByGame = new();

    public WebBackend(IAccountSource source, string version, bool demo)
    {
        _source = source;
        Version = version;
        IsDemo = demo;
        _source.Queue.Changed += _ => Changed?.Invoke();
    }

    public string Version { get; }
    public bool IsDemo { get; }

    /// <summary>Raised when the queue changes or a switch is requested, so the state is pushed at once.</summary>
    public event Action? Changed;

    public StateDto GetState()
    {
        return new StateDto(Version, IsDemo, _source.Accounts.Select(BuildAccount).ToList());
    }

    public CampaignListDto? GetCampaigns(string accountId)
    {
        var account = Find(accountId);
        if (account is null)
        {
            return null;
        }

        var context = new AccountContext(this, account);
        var campaigns = account.Campaigns
            .Select(context.BuildCampaign)
            .OrderBy(x => x.EndsAt ?? DateTime.MaxValue)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new CampaignListDto(account.CampaignsAt, campaigns);
    }

    public async Task<BackendResult> RefreshCampaignsAsync(string accountId, CancellationToken cancellationToken)
    {
        var account = Find(accountId);
        if (account is null)
        {
            return BackendResult.NotFound("No such account.");
        }

        try
        {
            await account.RefreshCampaignsAsync(cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return new BackendResult(StatusCodes.Status502BadGateway,
                new ProblemDto("Twitch didn't answer; try again in a minute."));
        }

        return BackendResult.Ok(GetCampaigns(accountId));
    }

    public async Task<BackendResult> GetCampaignAsync(string accountId, string campaignId,
        CancellationToken cancellationToken)
    {
        var account = Find(accountId);
        if (account is null)
        {
            return BackendResult.NotFound("No such account.");
        }

        var listed = account.Campaigns.FirstOrDefault(x => x.Id == campaignId);
        if (listed is null)
        {
            return BackendResult.NotFound("That campaign isn't offered to this account right now.");
        }

        AbstractCampaign? details;
        try
        {
            details = await account.GetCampaignDetailsAsync(campaignId, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            details = null;
        }

        var context = new AccountContext(this, account);
        return BackendResult.Ok(context.BuildDetail(listed, details ?? listed));
    }

    public BackendResult AddToQueue(string accountId, QueueRequest request)
    {
        var account = Find(accountId);
        if (account is null)
        {
            return BackendResult.NotFound("No such account.");
        }

        var campaign = account.Campaigns.FirstOrDefault(x => x.Id == request.CampaignId);
        if (campaign is null)
        {
            return BackendResult.NotFound("That campaign isn't offered to this account right now.");
        }

        if (CannotQueueReason(campaign) is { } reason)
        {
            return BackendResult.Conflict(reason);
        }

        int? position;
        switch (request.Position?.ToLowerInvariant())
        {
            case null or "" or "bottom":
                position = null;
                break;
            case "top":
                position = 0;
                break;
            default:
                if (!int.TryParse(request.Position, out var index) || index < 0)
                {
                    return BackendResult.BadRequest("Position must be top, bottom or a queue index.");
                }

                position = index;
                break;
        }

        _source.Queue.Add(account.Id, new QueuedCampaign(
            campaign.Id,
            campaign.Name,
            GameName(campaign),
            BoxArt(campaign.Game, account),
            EndOf(campaign),
            DateTime.UtcNow), position);

        if (request.SwitchNow || IsWaiting(account))
        {
            account.RequestReselect();
        }

        return BackendResult.Ok(BuildAccount(account));
    }

    public BackendResult RemoveFromQueue(string accountId, string campaignId)
    {
        var account = Find(accountId);
        if (account is null)
        {
            return BackendResult.NotFound("No such account.");
        }

        _source.Queue.Remove(account.Id, campaignId);
        return BackendResult.Ok(BuildAccount(account));
    }

    public BackendResult ReorderQueue(string accountId, ReorderRequest request)
    {
        var account = Find(accountId);
        if (account is null)
        {
            return BackendResult.NotFound("No such account.");
        }

        try
        {
            _source.Queue.Reorder(account.Id, request.CampaignIds ?? Array.Empty<string>());
        }
        catch (ArgumentException e)
        {
            return BackendResult.BadRequest(e.Message);
        }

        if (IsWaiting(account))
        {
            account.RequestReselect();
        }

        return BackendResult.Ok(BuildAccount(account));
    }

    public BackendResult SwitchNow(string accountId)
    {
        var account = Find(accountId);
        if (account is null)
        {
            return BackendResult.NotFound("No such account.");
        }

        account.RequestReselect();
        Changed?.Invoke();
        return BackendResult.Ok(BuildAccount(account));
    }

    public LogPageDto? GetLogs(string accountId, long after)
    {
        return Find(accountId)?.Logs.After(after);
    }

    /// <summary>Drops queue entries whose campaign ended over an hour ago.</summary>
    public void Maintain()
    {
        var cutoff = DateTime.UtcNow - EndedQueueGrace;
        foreach (var account in _source.Accounts)
        {
            _source.Queue.RemoveWhere(account.Id, x => x.EndAt is { } end && Utc(end) < cutoff);
        }
    }

    private IAccount? Find(string accountId) =>
        _source.Accounts.FirstOrDefault(x => x.Id == accountId || string.Equals(x.Login, accountId,
            StringComparison.OrdinalIgnoreCase));

    private static bool IsWaiting(IAccount account) =>
        account.Status == BotStatus.Idle && account.NextCycleAt is { } next && next > DateTime.UtcNow;

    private static string? CannotQueueReason(AbstractCampaign campaign) => campaign switch
    {
        RewardCampaign => "Reward campaigns can't be prioritized yet; the bot handles them on its own.",
        _ => null
    };

    private AccountDto BuildAccount(IAccount account)
    {
        var context = new AccountContext(this, account);

        // Idle without a next check means a cycle is running its first fetches; before the first cycle ends it is starting
        var status = account.Status switch
        {
            BotStatus.Watching => "watching",
            BotStatus.Seeking => "seeking",
            _ when account.NextCycleAt is not null => "waiting",
            _ when account.IdleReason != BotIdleReason.None => "seeking",
            _ => "starting"
        };

        var watching = account.Status == BotStatus.Watching && account.CurrentCampaign is not null
            ? context.BuildWatching()
            : null;

        var checking = account.Status == BotStatus.Seeking && account.CheckingCampaign is { } checkingCampaign
            ? new CheckingDto(checkingCampaign.Name, GameName(checkingCampaign))
            : null;

        var lastError = account.LastError is { } message && account.LastErrorAt is { } errorAt &&
                        DateTime.UtcNow - Utc(errorAt) < ErrorShownFor
            ? new ErrorDto(Redactor.Clean(message), Utc(errorAt))
            : null;

        return new AccountDto(
            account.Id,
            account.Login,
            status,
            account.IdleReason == BotIdleReason.None ? null : CamelCase(account.IdleReason.ToString()),
            status == "waiting" ? Utc(account.NextCycleAt) : null,
            watching,
            checking,
            lastError,
            context.BuildQueue(),
            Utc(account.CampaignsAt),
            account.Campaigns.Count,
            account.OnlyFavouriteGames);
    }

    // Box art goes missing on campaign objects the bot has re-fetched, so it is remembered per game
    private string? BoxArt(Game? game, IAccount? account = null)
    {
        if (game is null)
        {
            return null;
        }

        var source = !string.IsNullOrEmpty(game.BoxArtUrl) ? game.BoxArtUrl : account?.KnownBoxArt(game.Id);
        if (!string.IsNullOrEmpty(source))
        {
            var url = SizeRegex().Replace(source, "144x192");
            if (game.Id is not null)
            {
                _boxArtByGame[game.Id] = url;
            }

            return url;
        }

        return game.Id is not null && _boxArtByGame.TryGetValue(game.Id, out var known) ? known : null;
    }

    private static string? GameName(AbstractCampaign campaign) => campaign.Game?.DisplayName ?? campaign.Game?.Name;

    private static DateTime? EndOf(AbstractCampaign campaign) => Utc(campaign.EndAt ?? campaign.EndsAt);

    internal static DateTime? Utc(DateTime? value) => value is null ? null : Utc(value.Value);

    internal static DateTime Utc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };

    private static string CamelCase(string value) =>
        string.IsNullOrEmpty(value) ? value : char.ToLowerInvariant(value[0]) + value[1..];

    [GeneratedRegex(@"(\{width\}x\{height\}|\d+x\d+)(?=\.(jpg|jpeg|png|webp)$)", RegexOptions.IgnoreCase)]
    private static partial Regex SizeRegex();

    /// <summary>Per-request view of one account: its queue, progress and notes looked up once.</summary>
    private sealed class AccountContext
    {
        private readonly WebBackend _backend;
        private readonly IAccount _account;
        private readonly IReadOnlyList<QueuedCampaign> _queue;
        private readonly Dictionary<string, AbstractCampaign> _campaigns;
        private readonly Dictionary<string, DropCampaign> _inventory;
        private readonly HashSet<string> _favourites;
        private readonly HashSet<string> _avoided;

        public AccountContext(WebBackend backend, IAccount account)
        {
            _backend = backend;
            _account = account;
            _queue = backend._source.Queue.Get(account.Id);
            _campaigns = account.Campaigns.GroupBy(x => x.Id).ToDictionary(x => x.Key, x => x.First());
            _inventory = (account.Inventory?.DropCampaignsInProgress ?? new List<DropCampaign>())
                .GroupBy(x => x.Id).ToDictionary(x => x.Key, x => x.First());
            _favourites = account.FavouriteGames.ToHashSet(StringComparer.OrdinalIgnoreCase);
            _avoided = account.AvoidedCampaigns.ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var campaign in _campaigns.Values)
            {
                backend.BoxArt(campaign.Game, account);
            }
        }

        private bool IsWatching(string campaignId) =>
            _account.Status == BotStatus.Watching && _account.CurrentCampaign?.Id == campaignId;

        public WatchingDto BuildWatching()
        {
            var campaign = _account.CurrentCampaign!;
            var listed = _campaigns.GetValueOrDefault(campaign.Id);
            var broadcaster = _account.CurrentBroadcaster;
            var drop = _account.CurrentTimeBasedDrop;
            var benefit = drop?.BenefitEdges.FirstOrDefault()?.Benefit;

            var tiers = _account.CurrentDropsProgress is { Count: > 0 } progress
                ? progress.Select(x => new TierDto(x.Name, x.ImageUrl, (int)x.CurrentProgress,
                    (int)x.RequiredProgress, x.IsClaimed, x.IsActive)).ToList()
                : new List<TierDto>
                {
                    new(benefit?.Name ?? drop?.Name ?? "Current drop", benefit?.ImageAssetURL,
                        _account.CurrentMinutesWatched ?? 0, _account.RequiredMinutesWatched ?? 0, false, true)
                };

            var activeTier = tiers.FirstOrDefault(x => x.Active);

            return new WatchingDto(
                campaign.Id,
                campaign.Name,
                GameName(campaign) ?? (listed is null ? null : GameName(listed)),
                _backend.BoxArt(campaign.Game, _account) ?? _backend.BoxArt(listed?.Game, _account),
                broadcaster?.Login,
                broadcaster?.DisplayName ?? broadcaster?.Login,
                broadcaster?.ProfileImageURL,
                benefit?.Name ?? drop?.Name ?? activeTier?.Name,
                benefit?.ImageAssetURL ?? activeTier?.ImageUrl,
                _account.CurrentMinutesWatched,
                _account.RequiredMinutesWatched,
                EndOf(listed ?? campaign),
                tiers);
        }

        public List<QueueItemDto> BuildQueue()
        {
            var now = DateTime.UtcNow;
            return _queue.Select(item =>
            {
                var campaign = _campaigns.GetValueOrDefault(item.CampaignId);
                var progress = Progress(item.CampaignId);
                var note = Note(item.CampaignId);

                string state;
                if (IsWatching(item.CampaignId))
                {
                    state = "watching";
                }
                else if (campaign is null)
                {
                    state = item.EndAt is { } end && Utc(end) < now ? "ended" : "unavailable";
                }
                else if (progress is { Total: > 0 } && progress.Claimed >= progress.Total ||
                         note?.Kind == nameof(CampaignNoteKind.Completed))
                {
                    state = "done";
                }
                else
                {
                    state = "queued";
                }

                return new QueueItemDto(
                    item.CampaignId,
                    campaign?.Name ?? item.CampaignName,
                    campaign is null ? item.GameName : GameName(campaign) ?? item.GameName,
                    _backend.BoxArt(campaign?.Game, _account) ?? item.BoxArtUrl,
                    campaign is null ? Utc(item.EndAt) : EndOf(campaign),
                    state,
                    note,
                    progress);
            }).ToList();
        }

        public CampaignDto BuildCampaign(AbstractCampaign campaign)
        {
            var queueIndex = -1;
            for (var i = 0; i < _queue.Count; i++)
            {
                if (_queue[i].CampaignId == campaign.Id)
                {
                    queueIndex = i;
                    break;
                }
            }

            var (link, linkUrl) = LinkState(campaign);
            var gameName = GameName(campaign);

            return new CampaignDto(
                campaign.Id,
                campaign.Name,
                gameName,
                _backend.BoxArt(campaign.Game, _account),
                campaign.StartAt == default ? null : Utc(campaign.StartAt),
                EndOf(campaign),
                campaign is RewardCampaign ? "reward" : "drop",
                gameName is not null && _favourites.Contains(gameName),
                link,
                linkUrl,
                _avoided.Contains(campaign.Name ?? ""),
                IsWatching(campaign.Id),
                queueIndex >= 0 ? queueIndex + 1 : null,
                TotalMinutes(campaign),
                Progress(campaign.Id),
                Note(campaign.Id));
        }

        public CampaignDetailDto BuildDetail(AbstractCampaign listed, AbstractCampaign details)
        {
            var summary = BuildCampaign(listed);
            if (summary.TotalMinutes is null && TotalMinutes(details) is { } total)
            {
                summary = summary with { TotalMinutes = total };
            }

            var tiers = new List<TierDto>();
            if (details is RewardCampaign reward)
            {
                var goal = reward.UnlockRequirements?.MinuteWatchedGoal ?? 0;
                tiers.AddRange(reward.Rewards.Select(x => new TierDto(x.Name ?? "Reward",
                    x.ThumbnailImage?.Image1xURL, 0, goal, false, false)));
            }
            else
            {
                var inventory = _inventory.GetValueOrDefault(listed.Id);
                var drops = details.TimeBasedDrops
                    .Where(x => x.RequiredMinutesWatched > 0)
                    .OrderBy(x => x.RequiredMinutesWatched)
                    .ToList();

                var activeSet = false;
                foreach (var drop in drops)
                {
                    var tracked = inventory?.TimeBasedDrops.FirstOrDefault(x => x.Id == drop.Id);
                    var self = tracked?.Self ?? drop.Self;
                    var current = self?.CurrentMinutesWatched ?? 0;
                    var claimed = self?.IsClaimed ?? false;

                    // The bot's live minutes are fresher than the inventory for the drop it is watching
                    if (IsWatching(listed.Id) && _account.RequiredMinutesWatched == drop.RequiredMinutesWatched &&
                        _account.CurrentMinutesWatched is { } live)
                    {
                        current = Math.Max(current, live);
                    }

                    var active = !activeSet && !claimed && current < drop.RequiredMinutesWatched;
                    activeSet |= active;

                    var benefit = drop.BenefitEdges.FirstOrDefault()?.Benefit;
                    tiers.Add(new TierDto(benefit?.Name ?? drop.Name ?? "Drop", benefit?.ImageAssetURL,
                        Math.Min(current, drop.RequiredMinutesWatched), drop.RequiredMinutesWatched, claimed,
                        active));
                }
            }

            var channels = details.Allow?.Channels;
            var restricted = channels is { Count: > 0 and < 250 };

            return new CampaignDetailDto(
                summary,
                tiers,
                restricted
                    ? channels!.Select(x => x.DisplayName ?? x.Name ?? "?").Take(24).ToList()
                    : null,
                restricted ? channels!.Count : 0,
                (details as DropCampaign)?.DetailsURL ?? (listed as DropCampaign)?.DetailsURL,
                CannotQueueReason(listed) is null,
                CannotQueueReason(listed));
        }

        private ProgressDto? Progress(string campaignId)
        {
            if (IsWatching(campaignId) && _account.CurrentDropsProgress is { Count: > 0 } live)
            {
                var timed = live.Where(x => x.RequiredProgress > 0).ToList();
                if (timed.Count > 0)
                {
                    var next = timed.FirstOrDefault(x => x.IsActive) ??
                               timed.FirstOrDefault(x => !x.IsClaimed) ?? timed.Last();
                    return new ProgressDto((int)Math.Min(next.CurrentProgress, next.RequiredProgress),
                        (int)next.RequiredProgress, timed.Count(x => x.IsClaimed), timed.Count);
                }
            }

            if (!_inventory.TryGetValue(campaignId, out var inventory))
            {
                return null;
            }

            var drops = inventory.TimeBasedDrops
                .Where(x => x.RequiredMinutesWatched > 0)
                .OrderBy(x => x.RequiredMinutesWatched)
                .ToList();
            if (drops.Count == 0)
            {
                return null;
            }

            var target = drops.FirstOrDefault(x => x.Self?.IsClaimed != true &&
                                                   (x.Self?.CurrentMinutesWatched ?? 0) < x.RequiredMinutesWatched)
                         ?? drops.Last();
            var watched = Math.Min(target.Self?.CurrentMinutesWatched ?? 0, target.RequiredMinutesWatched);

            return new ProgressDto(watched, target.RequiredMinutesWatched,
                drops.Count(x => x.Self?.IsClaimed == true), drops.Count);
        }

        private NoteDto? Note(string campaignId) =>
            _account.Notes.TryGetValue(campaignId, out var note)
                ? new NoteDto(note.Kind.ToString(), Utc(note.At))
                : null;

        private static int? TotalMinutes(AbstractCampaign campaign)
        {
            if (campaign is RewardCampaign reward)
            {
                return reward.UnlockRequirements?.MinuteWatchedGoal is > 0 and var goal ? goal : null;
            }

            var timed = campaign.TimeBasedDrops.Where(x => x.RequiredMinutesWatched > 0).ToList();
            return timed.Count > 0 ? timed.Max(x => x.RequiredMinutesWatched) : null;
        }

        // Twitch reports isAccountConnected false for campaigns that need no link, whose link URL is twitch.tv itself
        private static (string Link, string? Url) LinkState(AbstractCampaign campaign)
        {
            if (campaign is not DropCampaign drop)
            {
                return ("none", null);
            }

            if (drop.Self?.IsAccountConnected == true)
            {
                return ("linked", null);
            }

            var url = drop.AccountLinkURL;
            if (string.IsNullOrWhiteSpace(url) ||
                Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                uri.Host is "twitch.tv" or "www.twitch.tv" && uri.AbsolutePath.Trim('/').Length == 0)
            {
                return ("none", null);
            }

            return ("unlinked", url);
        }
    }
}

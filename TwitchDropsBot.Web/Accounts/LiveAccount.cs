using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using TwitchDropsBot.Core.Platform.Shared.Bots;
using TwitchDropsBot.Core.Platform.Shared.Services;
using TwitchDropsBot.Core.Platform.Shared.Settings;
using TwitchDropsBot.Core.Platform.Twitch.Bot;
using TwitchDropsBot.Core.Platform.Twitch.Models;
using TwitchDropsBot.Core.Platform.Twitch.Models.Abstractions;

namespace TwitchDropsBot.Web.Accounts;

/// <summary>A running bot account. Reads its state; never touches its credentials.</summary>
public sealed class LiveAccount : IAccount
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DetailsLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan TwitchTimeout = TimeSpan.FromSeconds(25);

    private readonly TwitchUser _user;
    private readonly IOptionsMonitor<BotSettings> _settings;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly ConcurrentDictionary<string, (DateTime At, DropCampaign Campaign)> _details = new();
    private readonly ConcurrentDictionary<string, string> _boxArt = new();

    private List<AbstractCampaign>? _refreshedCampaigns;
    private Inventory? _refreshedInventory;
    private DateTime? _refreshedAt;

    public LiveAccount(TwitchUser user, IOptionsMonitor<BotSettings> settings)
    {
        _user = user;
        _settings = settings;

        if (user.UISink is not null)
        {
            user.UISink.OnLogEvent += Logs.Add;
        }

        // Raised on the bot's thread as it publishes a new list, before it re-fetches each campaign it checks
        // and replaces the campaign's game with one that may lack box art
        user.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TwitchUser.AvailableCampaigns))
            {
                RememberBoxArt(user.AvailableCampaigns);
            }
        };
        RememberBoxArt(user.AvailableCampaigns);
    }

    public string? KnownBoxArt(string? gameId) =>
        gameId is not null && _boxArt.TryGetValue(gameId, out var url) ? url : null;

    private void RememberBoxArt(IEnumerable<AbstractCampaign> campaigns)
    {
        foreach (var campaign in campaigns)
        {
            if (campaign.Game is { Id: { } id, BoxArtUrl: { Length: > 0 } url })
            {
                _boxArt[id] = url;
            }
        }
    }

    public LogBuffer Logs { get; } = new();

    public string Id => _user.Id;
    public string Login => _user.Login;

    public BotStatus Status => _user.Status;
    public BotIdleReason IdleReason => _user.IdleReason;
    public DateTime? NextCycleAt => _user.NextCycleAt;
    public string? LastError => _user.LastError;
    public DateTime? LastErrorAt => _user.LastErrorAt;

    public AbstractCampaign? CurrentCampaign => _user.CurrentCampaign;
    public TimeBasedDrop? CurrentTimeBasedDrop => _user.CurrentTimeBasedDrop;
    public User? CurrentBroadcaster => _user.CurrentBroadcaster;
    public int? CurrentMinutesWatched => _user.CurrentMinutesWatched;
    public int? RequiredMinutesWatched => _user.RequiredMinutesWatched;
    public IReadOnlyList<DropProgressInfo>? CurrentDropsProgress => _user.CurrentDropsProgress;
    public AbstractCampaign? CheckingCampaign => _user.CheckingCampaign;

    // The bot's own list from its last cycle, unless a refresh from the UI is newer
    private bool UseRefreshed => _refreshedAt is not null &&
                                 (_user.AvailableCampaignsAt is null || _refreshedAt > _user.AvailableCampaignsAt);

    public IReadOnlyList<AbstractCampaign> Campaigns =>
        UseRefreshed ? _refreshedCampaigns! : _user.AvailableCampaigns;

    public DateTime? CampaignsAt => UseRefreshed ? _refreshedAt : _user.AvailableCampaignsAt;

    public Inventory? Inventory => UseRefreshed && _refreshedInventory is not null ? _refreshedInventory : _user.Inventory;

    public IReadOnlyDictionary<string, CampaignNote> Notes => _user.CampaignNotes;

    public IReadOnlyList<string> FavouriteGames => _user.FavouriteGames;

    public bool OnlyFavouriteGames => _settings.CurrentValue.TwitchSettings.OnlyFavouriteGames;

    public IReadOnlyList<string> AvoidedCampaigns => _settings.CurrentValue.TwitchSettings.AvoidCampaign;

    public async Task RefreshCampaignsAsync(CancellationToken cancellationToken)
    {
        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            if (CampaignsAt is { } at && DateTime.UtcNow - at < RefreshInterval)
            {
                return;
            }

            var campaigns = await _user.TwitchRepository.FetchDropsAsync()
                .WaitAsync(TwitchTimeout, cancellationToken);
            var inventory = await _user.TwitchRepository.FetchInventoryDropsAsync()
                .WaitAsync(TwitchTimeout, cancellationToken);

            RememberBoxArt(campaigns);
            if (inventory is not null)
            {
                RememberBoxArt(inventory.DropCampaignsInProgress);
            }

            _refreshedCampaigns = campaigns;
            _refreshedInventory = inventory;
            _refreshedAt = DateTime.UtcNow;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public async Task<AbstractCampaign?> GetCampaignDetailsAsync(string campaignId, CancellationToken cancellationToken)
    {
        var campaign = Campaigns.FirstOrDefault(x => x.Id == campaignId);
        if (campaign is not DropCampaign || campaign.TimeBasedDrops.Count > 0)
        {
            return campaign;
        }

        if (_details.TryGetValue(campaignId, out var cached) && DateTime.UtcNow - cached.At < DetailsLifetime)
        {
            return cached.Campaign;
        }

        var details = await _user.TwitchRepository.FetchTimeBasedDropsAsync(campaignId)
            .WaitAsync(TwitchTimeout, cancellationToken);
        if (details is null)
        {
            return campaign;
        }

        _details[campaignId] = (DateTime.UtcNow, details);
        return details;
    }

    public void RequestReselect() => _user.RequestReselect();
}

public sealed class LiveAccountSource : IAccountSource
{
    private readonly BotRegistry _registry;
    private readonly IOptionsMonitor<BotSettings> _settings;
    private readonly ConcurrentDictionary<TwitchUser, LiveAccount> _accounts = new();

    public LiveAccountSource(BotRegistry registry, CampaignQueueService queue, IOptionsMonitor<BotSettings> settings)
    {
        _registry = registry;
        _settings = settings;
        Queue = queue;

        // Subscribe before reading the list, so no account's early log lines are missed
        _registry.UserRegistered += user =>
        {
            if (user is TwitchUser twitchUser)
            {
                Get(twitchUser);
            }
        };

        foreach (var user in _registry.Users.OfType<TwitchUser>())
        {
            Get(user);
        }
    }

    public CampaignQueueService Queue { get; }

    public IReadOnlyList<IAccount> Accounts => _registry.Users.OfType<TwitchUser>().Select(Get).ToList();

    private LiveAccount Get(TwitchUser user) => _accounts.GetOrAdd(user, x => new LiveAccount(x, _settings));
}

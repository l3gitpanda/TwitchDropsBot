using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TwitchDropsBot.Core.Platform.Kick.Bot;
using TwitchDropsBot.Core.Platform.Shared.Services;
using TwitchDropsBot.Core.Platform.Shared.Settings;
using TwitchDropsBot.Core.Platform.Twitch.Bot;

namespace TwitchDropsBot.Core.Platform.Shared.Factories.Bot;

public class BotFactory
{

    private readonly NotificationService _notificationService;
    private readonly IOptionsMonitor<BotSettings> _botSettings;
    private readonly CampaignQueueService _campaignQueue;
    private readonly PriorityGamesService _priorityGames;

    public BotFactory(NotificationService notificationService, IOptionsMonitor<BotSettings> botSettings,
        CampaignQueueService campaignQueue, PriorityGamesService priorityGames)
    {
        _notificationService = notificationService;
        _botSettings = botSettings;
        _campaignQueue = campaignQueue;
        _priorityGames = priorityGames;
    }

    public TwitchBot CreateTwitchBot(TwitchUser user, ILogger logger)
    {
        return new TwitchBot(user, logger, _notificationService, _botSettings, _campaignQueue, _priorityGames);
    }

    public KickBot CreateKickBot(KickUser user, ILogger logger)
    {
        return new KickBot(user, logger, _notificationService, _botSettings); 
    }
}
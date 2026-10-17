using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TwitchDropsBot.Core.Platform.Shared.Settings;

namespace TwitchDropsBot.Core.Platform.Shared.Services;

/// <summary>
/// Games whose campaigns are watched before the queue and everything else, and which a watch switches to as soon as
/// one goes live: PriorityGames in config.json plus the games added from the web UI. Those are kept in priority.json
/// next to queue.json; config.json is never written here. Names compare case-insensitively, like favourites.
/// </summary>
public partial class PriorityGamesService
{
    private const string FileName = "priority.json";

    public const int MaxGames = 50;
    public const int MaxNameLength = 100;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly object _lock = new();
    private readonly ILogger<PriorityGamesService> _logger;
    private readonly IOptionsMonitor<BotSettings>? _settings;
    private readonly string _filePath;
    private readonly List<string> _added;

    /// <summary>Raised when a game is added or removed from the web UI (not when config.json changes).</summary>
    public event Action? Changed;

    public PriorityGamesService(ILogger<PriorityGamesService> logger, IOptionsMonitor<BotSettings>? settings = null,
        string? filePath = null)
    {
        _logger = logger;
        _settings = settings;
        _filePath = filePath ?? Path.Combine(CampaignQueueService.DataDirectory(), FileName);
        _added = Load();
    }

    /// <summary>The games set in config.json; only an edit of that file removes them.</summary>
    public IReadOnlyList<string> FromConfig => Distinct(_settings?.CurrentValue.PriorityGames ?? new List<string>());

    /// <summary>The games added from the web UI.</summary>
    public IReadOnlyList<string> Added
    {
        get
        {
            lock (_lock)
            {
                return _added.ToList();
            }
        }
    }

    /// <summary>Every priority game, config.json's first.</summary>
    public IReadOnlyList<string> All => Distinct(FromConfig.Concat(Added));

    public bool Contains(string? game) =>
        Normalize(game) is { } name && All.Contains(name, StringComparer.OrdinalIgnoreCase);

    public bool IsFromConfig(string? game) =>
        Normalize(game) is { } name && FromConfig.Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>Adds a game; false when it already is one. Throws ArgumentException for a name it can't take.</summary>
    public bool Add(string? game)
    {
        var name = Normalize(game) ?? throw new ArgumentException("Give the game's name as Twitch shows it.");
        if (name.Length > MaxNameLength)
        {
            throw new ArgumentException($"A game name is at most {MaxNameLength} characters.");
        }

        lock (_lock)
        {
            if (Contains(name))
            {
                return false;
            }

            if (_added.Count >= MaxGames)
            {
                throw new ArgumentException($"There can be at most {MaxGames} priority games.");
            }

            _added.Add(name);
            Save();
        }

        Changed?.Invoke();
        return true;
    }

    /// <summary>Removes a game added from the web UI; false when it wasn't one.</summary>
    public bool Remove(string? game)
    {
        if (Normalize(game) is not { } name)
        {
            return false;
        }

        lock (_lock)
        {
            if (_added.RemoveAll(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase)) == 0)
            {
                return false;
            }

            Save();
        }

        Changed?.Invoke();
        return true;
    }

    private static string? Normalize(string? game)
    {
        if (string.IsNullOrWhiteSpace(game))
        {
            return null;
        }

        return Whitespace().Replace(game.Trim(), " ");
    }

    private static List<string> Distinct(IEnumerable<string?> games) =>
        games.Select(Normalize).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private List<string> Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return new List<string>();
            }

            var file = JsonSerializer.Deserialize<PriorityFile>(File.ReadAllText(_filePath), JsonOptions);
            return Distinct(file?.Games ?? new List<string>()).Where(x => x.Length <= MaxNameLength)
                .Take(MaxGames).ToList();
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Can't read {PriorityFile}, starting without priority games from the web UI",
                _filePath);
            return new List<string>();
        }
    }

    // Caller holds the lock
    private void Save()
    {
        try
        {
            var tempPath = _filePath + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(new PriorityFile { Games = _added }, JsonOptions));
            File.Move(tempPath, _filePath, overwrite: true);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Can't save {PriorityFile}; the list is kept in memory until the next change",
                _filePath);
        }
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    private class PriorityFile
    {
        public int Version { get; set; } = 1;
        public List<string> Games { get; set; } = new();
    }
}

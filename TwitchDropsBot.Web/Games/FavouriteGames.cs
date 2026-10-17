using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TwitchDropsBot.Core.Platform.Shared.Settings;

namespace TwitchDropsBot.Web.Games;

/// <summary>The favourite games every account farms, unless an account has a list of its own.</summary>
public interface IFavouriteGames
{
    IReadOnlyList<string> Games { get; }

    /// <summary>Logins whose own list in config.json replaces this one for them.</summary>
    IReadOnlyList<string> AccountsWithOwnList { get; }

    /// <summary>The list after adding the game; unchanged when it already is one.</summary>
    IReadOnlyList<string> Add(string game);

    /// <summary>The list after removing the game. Throws KeyNotFoundException when it isn't one.</summary>
    IReadOnlyList<string> Remove(string game);
}

/// <summary>
/// FavouriteGames in config.json, edited in place: only that list's text is replaced, the file is swapped in
/// atomically with its mode kept, and a copy of the previous file is kept once a day. The bot re-reads the file
/// when it changes and picks favourites again each cycle.
/// </summary>
public sealed class ConfigFavouriteGames : IFavouriteGames
{
    private const string Property = "FavouriteGames";

    // The settings reload a moment after the file changes; until then the list just written is the truth
    private static readonly TimeSpan ReloadGrace = TimeSpan.FromSeconds(5);

    private readonly object _lock = new();
    private readonly IOptionsMonitor<BotSettings> _settings;
    private readonly string _path;
    private readonly ILogger _logger;
    private List<string>? _written;
    private DateTime _writtenAt;

    public ConfigFavouriteGames(IOptionsMonitor<BotSettings> settings, string configPath, ILogger logger)
    {
        _settings = settings;
        _path = configPath;
        _logger = logger;
    }

    public IReadOnlyList<string> Games
    {
        get
        {
            lock (_lock)
            {
                if (_written is not null && DateTime.UtcNow - _writtenAt < ReloadGrace)
                {
                    return _written.ToList();
                }
            }

            return _settings.CurrentValue.FavouriteGames.ToList();
        }
    }

    public IReadOnlyList<string> AccountsWithOwnList =>
        _settings.CurrentValue.TwitchSettings.TwitchUsers
            .Where(x => x.FavouriteGames.Count > 0)
            .Select(x => x.Login)
            .ToList();

    public IReadOnlyList<string> Add(string game)
    {
        lock (_lock)
        {
            var file = File.ReadAllBytes(_path);
            var games = ConfigJsonArray.Read(file, Property);
            if (games.Contains(game, StringComparer.OrdinalIgnoreCase))
            {
                return games;
            }

            games.Add(game);
            Write(file, games);
            _logger.LogInformation("Web UI added {Game} to the favourite games", game);
            return games;
        }
    }

    public IReadOnlyList<string> Remove(string game)
    {
        lock (_lock)
        {
            var file = File.ReadAllBytes(_path);
            var games = ConfigJsonArray.Read(file, Property);
            if (games.RemoveAll(x => string.Equals(x, game, StringComparison.OrdinalIgnoreCase)) == 0)
            {
                throw new KeyNotFoundException();
            }

            Write(file, games);
            _logger.LogInformation("Web UI removed {Game} from the favourite games", game);
            return games;
        }
    }

    // Caller holds the lock
    private void Write(byte[] file, List<string> games)
    {
        var updated = ConfigJsonArray.Replace(file, Property, games);
        var directory = Path.GetDirectoryName(_path)!;
        var mode = OperatingSystem.IsWindows() ? default : File.GetUnixFileMode(_path);

        // One copy of the file as it was before the web UI's first change of the day
        var backup = $"{_path}.bak-webui-{DateTime.Now:yyyy-MM-dd}";
        if (!File.Exists(backup))
        {
            WriteNew(backup, file, mode);
        }

        var temp = Path.Combine(directory, $".{Path.GetFileName(_path)}.webui-{Guid.NewGuid():N}.tmp");
        try
        {
            WriteNew(temp, updated, mode);
            File.Move(temp, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }

        _written = games.ToList();
        _writtenAt = DateTime.UtcNow;
    }

    private static void WriteNew(string path, byte[] content, UnixFileMode mode)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = mode;
        }

        using var stream = new FileStream(path, options);
        stream.Write(content);
        stream.Flush(flushToDisk: true);
    }
}

/// <summary>Demo mode: the made-up favourites, kept in memory.</summary>
public sealed class DemoFavouriteGames : IFavouriteGames
{
    private readonly object _lock = new();
    private List<string> _games;

    public DemoFavouriteGames(IEnumerable<string> games)
    {
        _games = games.ToList();
    }

    public IReadOnlyList<string> Games
    {
        get
        {
            lock (_lock)
            {
                return _games;
            }
        }
    }

    public IReadOnlyList<string> AccountsWithOwnList => Array.Empty<string>();

    public IReadOnlyList<string> Add(string game)
    {
        lock (_lock)
        {
            if (!_games.Contains(game, StringComparer.OrdinalIgnoreCase))
            {
                // Copy on write, so a bot reading the old list never sees it change
                _games = _games.Append(game).ToList();
            }

            return _games;
        }
    }

    public IReadOnlyList<string> Remove(string game)
    {
        lock (_lock)
        {
            if (!_games.Contains(game, StringComparer.OrdinalIgnoreCase))
            {
                throw new KeyNotFoundException();
            }

            _games = _games.Where(x => !string.Equals(x, game, StringComparison.OrdinalIgnoreCase)).ToList();
            return _games;
        }
    }
}

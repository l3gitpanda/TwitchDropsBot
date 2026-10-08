using System.Net;

namespace TwitchDropsBot.Web;

/// <summary>
/// Web UI settings, read from environment variables so config.json stays untouched:
/// WEBUI_PORT (enables the UI), WEBUI_PASSWORD or WEBUI_PASSWORD_FILE, WEBUI_AUTH=none,
/// WEBUI_TRUSTED_PROXIES (comma-separated addresses), WEBUI_DATA_DIR and WEBUI_DEMO=true.
/// </summary>
public sealed class WebUiOptions
{
    public int Port { get; init; }
    public string? Password { get; init; }
    public bool AuthDisabled { get; init; }
    public bool Demo { get; init; }
    public IReadOnlyList<IPAddress> TrustedProxies { get; init; } = Array.Empty<IPAddress>();
    public string DataDirectory { get; init; } = AppContext.BaseDirectory;

    public bool RequiresLogin => !AuthDisabled;

    public static WebUiOptions? FromEnvironment(string dataDirectory, out string? error)
    {
        error = null;

        var portText = Environment.GetEnvironmentVariable("WEBUI_PORT");
        if (string.IsNullOrWhiteSpace(portText))
        {
            return null;
        }

        if (!int.TryParse(portText, out var port) || port is < 1 or > 65535)
        {
            error = $"WEBUI_PORT '{portText}' is not a valid port.";
            return null;
        }

        var password = Environment.GetEnvironmentVariable("WEBUI_PASSWORD");
        var passwordFile = Environment.GetEnvironmentVariable("WEBUI_PASSWORD_FILE");
        if (string.IsNullOrEmpty(password) && !string.IsNullOrWhiteSpace(passwordFile))
        {
            try
            {
                password = File.ReadAllText(passwordFile).TrimEnd('\r', '\n');
            }
            catch (Exception e)
            {
                error = $"Can't read WEBUI_PASSWORD_FILE: {e.Message}";
                return null;
            }
        }

        var authDisabled = string.Equals(Environment.GetEnvironmentVariable("WEBUI_AUTH"), "none",
            StringComparison.OrdinalIgnoreCase);

        if (string.IsNullOrEmpty(password) && !authDisabled)
        {
            error = "The web UI needs WEBUI_PASSWORD (or WEBUI_PASSWORD_FILE); set WEBUI_AUTH=none to run it without a login.";
            return null;
        }

        var proxies = new List<IPAddress>();
        foreach (var entry in (Environment.GetEnvironmentVariable("WEBUI_TRUSTED_PROXIES") ?? "")
                     .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!IPAddress.TryParse(entry, out var address))
            {
                error = $"WEBUI_TRUSTED_PROXIES entry '{entry}' is not an IP address.";
                return null;
            }

            proxies.Add(address);
        }

        return new WebUiOptions
        {
            Port = port,
            Password = string.IsNullOrEmpty(password) ? null : password,
            AuthDisabled = authDisabled,
            Demo = string.Equals(Environment.GetEnvironmentVariable("WEBUI_DEMO"), "true",
                StringComparison.OrdinalIgnoreCase),
            TrustedProxies = proxies,
            DataDirectory = dataDirectory
        };
    }
}

using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TwitchDropsBot.Web.Games;

/// <summary>What Twitch calls a game. Checked is false when Twitch couldn't be asked.</summary>
public sealed record GameLookupResult(bool Checked, string? Name);

/// <summary>Finds a game's category name as Twitch spells it, so a typed name can't silently match nothing.</summary>
public interface IGameLookup
{
    Task<GameLookupResult> FindAsync(string name, CancellationToken cancellationToken);
}

/// <summary>
/// Asks Twitch's public GraphQL API without signing in, as its website does for anyone. It resolves names
/// case-insensitively and follows renames (a lookup of "Overwatch 2" answers "Overwatch").
/// </summary>
public sealed class TwitchGameLookup : IGameLookup
{
    // Twitch's own website client, used without an account; no token is ever sent
    private const string WebClientId = "kimne78kx3ncx6brgo4mv6wki5h1ko";
    private const string Query = "query($name: String!) { game(name: $name) { id displayName } }";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public async Task<GameLookupResult> FindAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://gql.twitch.tv/gql")
            {
                Content = JsonContent.Create(new { query = Query, variables = new { name } })
            };
            request.Headers.Add("Client-ID", WebClientId);

            using var response = await Http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new GameLookupResult(false, null);
            }

            var answer = await response.Content.ReadFromJsonAsync<Answer>(cancellationToken: cancellationToken);
            if (answer?.Errors is { Count: > 0 } || answer?.Data is null)
            {
                return new GameLookupResult(false, null);
            }

            return new GameLookupResult(true, answer.Data.Game?.DisplayName);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException
                                      or NotSupportedException && !cancellationToken.IsCancellationRequested)
        {
            return new GameLookupResult(false, null);
        }
    }

    private sealed record Answer(
        [property: JsonPropertyName("data")] AnswerData? Data,
        [property: JsonPropertyName("errors")] List<JsonElement>? Errors);

    private sealed record AnswerData([property: JsonPropertyName("game")] AnswerGame? Game);

    private sealed record AnswerGame(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("displayName")] string? DisplayName);
}

/// <summary>Demo mode talks to nobody: names are taken as typed and reported unchecked.</summary>
public sealed class OfflineGameLookup : IGameLookup
{
    public Task<GameLookupResult> FindAsync(string name, CancellationToken cancellationToken) =>
        Task.FromResult(new GameLookupResult(false, null));
}

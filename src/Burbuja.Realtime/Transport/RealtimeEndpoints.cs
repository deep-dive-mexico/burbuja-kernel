using System.Net.WebSockets;
using System.Text.Json;
using Burbuja.Realtime.Abstractions;
using Burbuja.Realtime.Lobbies;
using Burbuja.Realtime.Matchmaking;
using Burbuja.Realtime.Players;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Burbuja.Realtime.Transport;

/// <summary>
/// <c>/realtime/v1</c>: the player API (guests, queue, lobbies), the operator API (fleet, drain, close) and the
/// WebSocket door players connect through. Mapped only when <c>Realtime:Enabled</c>.
///
/// <para><b>Two credentials, two jobs.</b> HTTP calls authenticate a player through the
/// <see cref="IPlayerAuthenticator"/> chain. The WebSocket carries no player credential at all: its
/// <c>token</c> query value is the single-use, short-lived seat reservation the API issued, which names the
/// lobby, the seat and the player. Browsers cannot set WebSocket headers, and a credential that only opens one
/// seat once is the right thing to put in a URL. Because no cookie is involved, a cross-site page cannot ride a
/// player's session into a lobby.</para>
/// </summary>
public static class RealtimeEndpoints
{
    private const long MaxBodyBytes = 16 * 1024;
    private const int MaxSettings = 16;
    private const int MaxSettingValueLength = 128;

    public static void Map(WebApplication app)
    {
        var options = app.Services.GetRequiredService<IOptions<RealtimeOptions>>().Value;
        if (!options.Enabled)
            return;

        app.UseWhen(
            context => context.Request.Path.StartsWithSegments(RealtimeProtocol.BasePath),
            branch => branch.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15) }));

        // Every handler takes only the HttpContext and is typed Func<HttpContext, Task<IResult>> on purpose: a
        // bare lambda of that shape binds as a RequestDelegate, and ASP.NET discards its IResult.
        var api = app.MapGroup(RealtimeProtocol.BasePath).AllowAnonymous();
        api.MapGet("/games", H(ListGamesAsync));
        api.MapPost("/guests", H(CreateGuestAsync));
        api.MapGet("/me", H(MeAsync));
        api.MapPost("/queue", H(EnqueueAsync));
        api.MapGet("/queue/{ticketId}", H(GetTicketAsync));
        api.MapDelete("/queue/{ticketId}", H(CancelTicketAsync));
        api.MapGet("/lobbies", H(ListLobbiesAsync));
        api.MapPost("/lobbies", H(CreateLobbyAsync));
        api.MapPost("/lobbies/join", H(JoinByCodeAsync));
        api.MapGet("/lobbies/{lobbyId}", H(GetLobbyAsync));
        api.MapPost("/lobbies/{lobbyId}/join", H(JoinLobbyAsync));
        api.MapPost("/lobbies/{lobbyId}/leave", H(LeaveLobbyAsync));
        api.MapPost("/lobbies/{lobbyId}/close", H(CloseLobbyAsync));
        api.MapGet("/servers", H(ListServersAsync));
        api.MapPost("/servers/{serverId}/drain", H(http => SetServerStateAsync(http, drain: true)));
        api.MapPost("/servers/{serverId}/activate", H(http => SetServerStateAsync(http, drain: false)));
        api.MapGet("/connect", H(ConnectAsync));
    }

    private static Func<HttpContext, Task<IResult>> H(Func<HttpContext, Task<IResult>> handler) => handler;

    // ─── players ─────────────────────────────────────────────────────────────────────────────

    private static Task<IResult> ListGamesAsync(HttpContext http)
    {
        var games = Service<LobbyManager>(http).Games.All.Select(g => new
        {
            id = g.Id,
            displayName = g.DisplayName,
            rules = new
            {
                minPlayers = g.Rules.MinPlayers,
                maxPlayers = g.Rules.MaxPlayers,
                tickRate = g.Rules.TickRate,
                startCountdownSeconds = g.Rules.StartCountdown.TotalSeconds,
                maxMatchSeconds = g.Rules.MaxMatchDuration.TotalSeconds,
                allowJoinInProgress = g.Rules.AllowJoinInProgress,
                reconnectGraceSeconds = g.Rules.ReconnectGrace.TotalSeconds,
            },
        });
        return Task.FromResult(Results.Json(new { value = games }, Frames.Json));
    }

    private static async Task<IResult> CreateGuestAsync(HttpContext http)
    {
        if (!Service<IOptions<RealtimeOptions>>(http).Value.Guests.Enabled)
            return Error(StatusCodes.Status404NotFound, "guests_disabled", "Guest players are not enabled on this host.");
        var (body, problem) = await ReadAsync<GuestBody>(http);
        if (problem is not null)
            return problem;
        if (GuestTokens.CleanDisplayName(body?.DisplayName) is not { } name)
            return Error(StatusCodes.Status400BadRequest, "invalid_display_name",
                $"displayName must be 1-{GuestTokens.MaxDisplayNameLength} printable characters.");
        var (identity, token, expires) = Service<GuestTokens>(http).Issue(name);
        return Results.Json(new { playerId = identity.PlayerId, displayName = identity.DisplayName, token, expiresAt = expires },
            Frames.Json, statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> MeAsync(HttpContext http)
    {
        if (await PlayerAsync(http) is not { } player)
            return Unauthenticated();
        var lobby = Service<LobbyManager>(http).LobbyOf(player.PlayerId);
        return Results.Json(new
        {
            playerId = player.PlayerId,
            displayName = player.DisplayName,
            isGuest = player.IsGuest,
            lobbyId = lobby?.Id,
        }, Frames.Json);
    }

    private static async Task<IResult> EnqueueAsync(HttpContext http)
    {
        if (await PlayerAsync(http) is not { } player)
            return Unauthenticated();
        var (body, problem) = await ReadAsync<QueueBody>(http);
        if (problem is not null)
            return problem;
        if (string.IsNullOrWhiteSpace(body?.Game))
            return Error(StatusCodes.Status400BadRequest, "game_required", "Name the game to queue for.");
        if (body.Region is { Length: > 32 })
            return Error(StatusCodes.Status400BadRequest, "invalid_region", "region is at most 32 characters.");

        var result = Service<MatchmakingQueue>(http).Enqueue(player, body.Game, body.Region);
        return result.Error switch
        {
            null => Results.Json(TicketPayload(result.Ticket!), Frames.Json, statusCode: StatusCodes.Status202Accepted),
            "already_queued" => Results.Json(new
            {
                error = new { code = "already_queued", message = "This player already has a ticket." },
                ticket = TicketPayload(result.Ticket!),
            }, Frames.Json, statusCode: StatusCodes.Status409Conflict),
            _ => Failure(result.Error),
        };
    }

    private static async Task<IResult> GetTicketAsync(HttpContext http)
    {
        if (await PlayerAsync(http) is not { } player)
            return Unauthenticated();
        var id = Route(http, "ticketId");
        var queue = Service<MatchmakingQueue>(http);
        if (queue.Get(id) is not { } ticket || ticket.PlayerId != player.PlayerId)
            return Error(StatusCodes.Status404NotFound, "ticket_not_found", "No such ticket for this player.");

        // Long-poll: ?waitSeconds=N (at most 30) answers as soon as the ticket leaves the queue.
        var wait = int.TryParse(http.Request.Query["waitSeconds"], out var seconds) ? Math.Clamp(seconds, 0, 30) : 0;
        var latest = await queue.WaitAsync(id, TimeSpan.FromSeconds(wait), http.RequestAborted) ?? ticket;
        return Results.Json(TicketPayload(latest), Frames.Json);
    }

    private static async Task<IResult> CancelTicketAsync(HttpContext http)
    {
        if (await PlayerAsync(http) is not { } player)
            return Unauthenticated();
        return Service<MatchmakingQueue>(http).Cancel(Route(http, "ticketId"), player.PlayerId)
            ? Results.NoContent()
            : Error(StatusCodes.Status404NotFound, "ticket_not_found", "No queued ticket with that id for this player.");
    }

    private static Task<IResult> ListLobbiesAsync(HttpContext http)
    {
        var game = http.Request.Query["game"].ToString();
        var lobbies = Service<LobbyManager>(http).Lobbies
            .Where(l => l.Visibility == LobbyVisibility.Public && (game.Length == 0 || l.GameId == game))
            .Select(l => l.Snapshot())
            .Where(s => s.State < LobbyState.Ended)
            .OrderBy(s => s.CreatedAt)
            .Select(s => LobbyPayload(s, includeCode: false));
        return Task.FromResult(Results.Json(new { value = lobbies }, Frames.Json));
    }

    private static async Task<IResult> CreateLobbyAsync(HttpContext http)
    {
        if (await PlayerAsync(http) is not { } player)
            return Unauthenticated();
        var (body, problem) = await ReadAsync<CreateLobbyBody>(http);
        if (problem is not null)
            return problem;
        if (string.IsNullOrWhiteSpace(body?.Game))
            return Error(StatusCodes.Status400BadRequest, "game_required", "Name the game the lobby is for.");
        var visibility = body.Visibility?.ToLowerInvariant() switch
        {
            null or "" or "private" => LobbyVisibility.Private,
            "public" => LobbyVisibility.Public,
            _ => (LobbyVisibility?)null,
        };
        if (visibility is null)
            return Error(StatusCodes.Status400BadRequest, "invalid_visibility", "visibility is 'public' or 'private'.");
        if (ValidateSettings(body.Settings) is { } invalid)
            return Error(StatusCodes.Status400BadRequest, "invalid_settings", invalid);

        var manager = Service<LobbyManager>(http);
        if (manager.LobbyOf(player.PlayerId) is not null)
            return Failure("already_in_lobby");
        var created = manager.Create(new CreateLobbyRequest(body.Game, visibility.Value, LobbyOrigin.Hosted, body.Region,
            body.Settings ?? new Dictionary<string, string>()));
        if (created.Lobby is not { } lobby)
            return Failure(created.Error!);
        var reserved = manager.Reserve(lobby, player);
        if (reserved.Reservation is null)
        {
            lobby.RequestClose("creator_unseated");
            return Failure(reserved.Error!);
        }

        return Results.Json(new { lobby = LobbyPayload(lobby.Snapshot(), includeCode: true), reservation = reserved.Reservation },
            Frames.Json, statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> GetLobbyAsync(HttpContext http)
    {
        if (await PlayerAsync(http) is not { } player)
            return Unauthenticated();
        if (Service<LobbyManager>(http).Find(Route(http, "lobbyId")) is not { } lobby)
            return LobbyNotFound();
        var snapshot = lobby.Snapshot();
        var member = snapshot.Members.Any(m => m.PlayerId == player.PlayerId) || lobby.HasSeat(player.PlayerId);
        return Results.Json(LobbyPayload(snapshot, includeCode: member || IsOperator(http)), Frames.Json);
    }

    private static async Task<IResult> JoinLobbyAsync(HttpContext http)
    {
        if (await PlayerAsync(http) is not { } player)
            return Unauthenticated();
        var manager = Service<LobbyManager>(http);
        return manager.Find(Route(http, "lobbyId")) is { } lobby ? Reservation(manager.Reserve(lobby, player)) : LobbyNotFound();
    }

    private static async Task<IResult> JoinByCodeAsync(HttpContext http)
    {
        if (await PlayerAsync(http) is not { } player)
            return Unauthenticated();
        var (body, problem) = await ReadAsync<JoinBody>(http);
        if (problem is not null)
            return problem;
        var manager = Service<LobbyManager>(http);
        return manager.FindByCode(body?.Code) is { } lobby ? Reservation(manager.Reserve(lobby, player)) : LobbyNotFound();
    }

    private static async Task<IResult> LeaveLobbyAsync(HttpContext http)
    {
        if (await PlayerAsync(http) is not { } player)
            return Unauthenticated();
        var manager = Service<LobbyManager>(http);
        return manager.Find(Route(http, "lobbyId")) is { } lobby && manager.Leave(lobby, player.PlayerId)
            ? Results.NoContent()
            : LobbyNotFound();
    }

    // ─── operators ───────────────────────────────────────────────────────────────────────────

    private static Task<IResult> CloseLobbyAsync(HttpContext http)
    {
        if (OperatorProblem(http) is { } denied)
            return Task.FromResult(denied);
        if (Service<LobbyManager>(http).Find(Route(http, "lobbyId")) is not { } lobby)
            return Task.FromResult(LobbyNotFound());
        lobby.RequestClose("closed_by_operator");
        return Task.FromResult(Results.Accepted());
    }

    private static Task<IResult> ListServersAsync(HttpContext http)
    {
        if (OperatorProblem(http) is { } denied)
            return Task.FromResult(denied);
        var manager = Service<LobbyManager>(http);
        var servers = manager.Balancer.Servers.Select(server =>
        {
            var status = server.Status();
            return new
            {
                serverId = status.ServerId,
                region = status.Region,
                state = status.State.ToString().ToLowerInvariant(),
                lobbies = status.Lobbies,
                maxLobbies = status.MaxLobbies,
                players = status.Players,
                utilization = status.Utilization,
                publicUrl = status.PublicUrl,
                hosted = server.Lobbies.Select(l => l.Snapshot()).Select(s => new
                {
                    lobbyId = s.LobbyId,
                    gameId = s.GameId,
                    state = s.State.ToString().ToLowerInvariant(),
                    seats = s.Seats,
                    maxPlayers = s.MaxPlayers,
                }),
            };
        });
        return Task.FromResult(Results.Json(new { placement = manager.Balancer.Strategy.Name, value = servers }, Frames.Json));
    }

    private static Task<IResult> SetServerStateAsync(HttpContext http, bool drain)
    {
        if (OperatorProblem(http) is { } denied)
            return Task.FromResult(denied);
        if (Service<LobbyManager>(http).Server(Route(http, "serverId")) is not { } server)
            return Task.FromResult(Error(StatusCodes.Status404NotFound, "server_not_found", "No such lobby server."));
        if (drain)
            server.Drain();
        else
            server.Activate();
        return Task.FromResult(Results.Json(new { serverId = server.Id, state = server.State.ToString().ToLowerInvariant() }, Frames.Json));
    }

    // ─── the WebSocket door ──────────────────────────────────────────────────────────────────

    private static async Task<IResult> ConnectAsync(HttpContext http)
    {
        if (!http.WebSockets.IsWebSocketRequest)
            return Error(StatusCodes.Status400BadRequest, "websocket_required", "Connect with a WebSocket upgrade.");
        var token = http.Request.Query["token"].ToString();
        var options = Service<IOptions<RealtimeOptions>>(http).Value;
        var stopping = Service<IHostApplicationLifetime>(http).ApplicationStopping;
        using var aborted = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted, stopping);

        // Accept first and refuse with a close code: a browser cannot read the status of a failed upgrade,
        // but it can read 4001 and the reason.
        using var socket = await http.WebSockets.AcceptWebSocketAsync();
        var connection = new WebSocketPlayerConnection(socket, Math.Max(8, options.SendBufferFrames));
        var admission = Service<IRealtimeGateway>(http).Admit(token, connection);
        if (!admission.Accepted)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(aborted.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                await socket.CloseAsync((WebSocketCloseStatus)RealtimeCloseCode.InvalidReservation,
                    admission.Error ?? "invalid_reservation", timeout.Token);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
            {
            }

            return Results.Empty;
        }

        await connection.RunAsync(admission.Inbox!, admission.MaxMessageBytes, aborted.Token);
        return Results.Empty;
    }

    // ─── helpers ─────────────────────────────────────────────────────────────────────────────

    private static T Service<T>(HttpContext http) where T : notnull => http.RequestServices.GetRequiredService<T>();

    private static string Route(HttpContext http, string name) => http.Request.RouteValues[name]?.ToString() ?? "";

    private static ValueTask<PlayerIdentity?> PlayerAsync(HttpContext http) =>
        Service<PlayerAuthentication>(http).AuthenticateAsync(http);

    private static bool IsOperator(HttpContext http)
    {
        var request = new PlayerAuthRequest(
            http.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase), null);
        var roles = Service<IOptions<RealtimeOptions>>(http).Value.OperatorRoles;
        return Service<ApiKeyPlayerAuthenticator>(http).Match(request) is { } entry
            && entry.Roles.Any(r => roles.Contains(r, StringComparer.OrdinalIgnoreCase));
    }

    private static IResult? OperatorProblem(HttpContext http)
    {
        var request = new PlayerAuthRequest(
            http.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase), null);
        if (Service<ApiKeyPlayerAuthenticator>(http).Match(request) is null)
            return Unauthenticated();
        return IsOperator(http)
            ? null
            : Error(StatusCodes.Status403Forbidden, "operator_required", "This key has no realtime operator role.");
    }

    private static async Task<(T? Body, IResult? Problem)> ReadAsync<T>(HttpContext http) where T : class
    {
        if (http.Request.ContentLength is > MaxBodyBytes)
            return (null, Error(StatusCodes.Status413PayloadTooLarge, "body_too_large", "Request bodies are at most 16 KiB."));
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            limit.MaxRequestBodySize = MaxBodyBytes;
        if (http.Request.ContentLength == 0 || !http.Request.HasJsonContentType())
            return (null, null);
        try
        {
            return (await http.Request.ReadFromJsonAsync<T>(Frames.Json, http.RequestAborted), null);
        }
        catch (JsonException)
        {
            return (null, Error(StatusCodes.Status400BadRequest, "invalid_json", "The body is not valid JSON for this call."));
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return (null, Error(StatusCodes.Status413PayloadTooLarge, "body_too_large", "Request bodies are at most 16 KiB."));
        }
    }

    private static string? ValidateSettings(Dictionary<string, string>? settings)
    {
        if (settings is null)
            return null;
        if (settings.Count > MaxSettings)
            return $"At most {MaxSettings} settings.";
        foreach (var (key, value) in settings)
        {
            if (!RealtimeIds.IsValidGameId(key))
                return $"Setting '{key}' must be one lowercase segment.";
            if (value is null || value.Length > MaxSettingValueLength || value.Any(char.IsControl))
                return $"Setting '{key}' must be a printable value of at most {MaxSettingValueLength} characters.";
        }

        return null;
    }

    private static object TicketPayload(TicketView ticket) => new
    {
        ticketId = ticket.TicketId,
        gameId = ticket.GameId,
        region = ticket.Region,
        state = ticket.State.ToString().ToLowerInvariant(),
        enqueuedAt = ticket.EnqueuedAt,
        updatedAt = ticket.UpdatedAt,
        detail = ticket.Detail,
        reservation = ticket.Reservation,
    };

    private static object LobbyPayload(LobbySnapshot lobby, bool includeCode) => new
    {
        lobbyId = lobby.LobbyId,
        gameId = lobby.GameId,
        serverId = lobby.ServerId,
        region = lobby.Region,
        state = lobby.State.ToString().ToLowerInvariant(),
        visibility = lobby.Visibility.ToString().ToLowerInvariant(),
        origin = lobby.Origin.ToString().ToLowerInvariant(),
        joinCode = includeCode ? lobby.JoinCode : null,
        createdAt = lobby.CreatedAt,
        countdownEndsAt = lobby.CountdownEndsAt,
        startedAt = lobby.StartedAt,
        seats = lobby.Seats,
        openSeats = lobby.OpenSeats,
        maxPlayers = lobby.MaxPlayers,
        members = lobby.Members.Select(m => new { playerId = m.PlayerId, displayName = m.DisplayName, slot = m.Slot, connected = m.Connected }),
    };

    private static IResult Reservation(ReservationResult result) =>
        result.Reservation is { } reservation ? Results.Json(reservation, Frames.Json) : Failure(result.Error!);

    private static IResult Failure(string code) => code switch
    {
        "unknown_game" => Error(StatusCodes.Status404NotFound, code, "No game with that id is registered."),
        "no_capacity" => Error(StatusCodes.Status503ServiceUnavailable, code, "No lobby server has room right now."),
        "already_in_lobby" => Error(StatusCodes.Status409Conflict, code, "This player is seated in a lobby that is still playing."),
        "lobby_full" => Error(StatusCodes.Status409Conflict, code, "The lobby has no free seat."),
        "match_in_progress" => Error(StatusCodes.Status409Conflict, code, "The match started and this game does not take players mid-match."),
        "lobby_closed" => Error(StatusCodes.Status410Gone, code, "The lobby's match is over."),
        _ => Error(StatusCodes.Status500InternalServerError, code, "The lobby could not be created."),
    };

    private static IResult LobbyNotFound() =>
        Error(StatusCodes.Status404NotFound, "lobby_not_found", "No such lobby, or this player has no seat in it.");

    private static IResult Unauthenticated() =>
        Error(StatusCodes.Status401Unauthorized, "unauthenticated", "Sign in as a player (x-player-token) or present an API key.");

    private static IResult Error(int status, string code, string message) =>
        Results.Json(new { error = new { code, message } }, Frames.Json, statusCode: status);

    private sealed record GuestBody(string? DisplayName);

    private sealed record QueueBody(string? Game, string? Region);

    private sealed record CreateLobbyBody(string? Game, string? Visibility, string? Region, Dictionary<string, string>? Settings);

    private sealed record JoinBody(string? Code);
}

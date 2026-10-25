using System.Collections.Concurrent;
using Burbuja.Realtime.Abstractions;
using Burbuja.Realtime.Lobbies;
using Microsoft.Extensions.Logging;

namespace Burbuja.Realtime.Fleet;

/// <summary>
/// One dedicated-server capacity unit hosted by this node. Each lobby placed here runs its own step loop;
/// the server owns those loops, counts its allocations and refuses new ones while draining.
/// </summary>
public sealed class LobbyServer
{
    private readonly ConcurrentDictionary<string, (Lobby Lobby, Task Loop)> _lobbies = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stopping = new();
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private int _allocated;
    private volatile LobbyServerState _state = LobbyServerState.Active;
    private readonly IReadOnlyList<string>? _allowedGames;

    public LobbyServer(string id, string region, int maxLobbies, string? publicUrl, TimeProvider time, ILogger log,
        IReadOnlyList<string>? allowedGames = null)
    {
        Id = id;
        Region = region;
        MaxLobbies = maxLobbies;
        PublicUrl = string.IsNullOrWhiteSpace(publicUrl) ? null : publicUrl;
        _time = time;
        _log = log;
        _allowedGames = allowedGames is null ? null : Array.AsReadOnly(allowedGames.ToArray());
    }

    public string Id { get; }
    public string Region { get; }
    public int MaxLobbies { get; }
    public string? PublicUrl { get; }
    public LobbyServerState State => _state;
    public IEnumerable<Lobby> Lobbies => _lobbies.Values.Select(v => v.Lobby);

    /// <summary>Lock-free: it reads each lobby's published counts, never waiting on a tick.</summary>
    public LobbyServerStatus Status() => new(
        Id, Region, _state, Volatile.Read(ref _allocated), MaxLobbies,
        _lobbies.Values.Sum(v => v.Lobby.SeatCount), PublicUrl,
        _lobbies.Values.Sum(v => v.Lobby.Performance.Busy)) { AllowedGames = _allowedGames };

    public void Drain() => _state = LobbyServerState.Draining;

    public void Activate() => _state = LobbyServerState.Active;

    /// <summary>Claims one lobby slot atomically, so two placements racing for the last slot cannot both win.</summary>
    internal bool TryAllocate(string? gameId = null)
    {
        if (_allowedGames is not null && (gameId is null || !_allowedGames.Contains(gameId, StringComparer.Ordinal)))
            return false;
        while (true)
        {
            var current = Volatile.Read(ref _allocated);
            if (_state != LobbyServerState.Active || current >= MaxLobbies)
                return false;
            if (Interlocked.CompareExchange(ref _allocated, current + 1, current) == current)
                return true;
        }
    }

    internal void Release() => Interlocked.Decrement(ref _allocated);

    /// <summary>Starts the lobby's loop. With <paramref name="run"/> false (tests) the caller steps it.</summary>
    internal void Host(Lobby lobby, bool run)
    {
        var loop = run ? Task.Run(() => RunAsync(lobby, _stopping.Token)) : Task.CompletedTask;
        _lobbies[lobby.Id] = (lobby, loop);
    }

    internal void Forget(Lobby lobby) => _lobbies.TryRemove(lobby.Id, out _);

    private async Task RunAsync(Lobby lobby, CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(lobby.StepInterval, _time);
            while (await timer.WaitForNextTickAsync(ct))
            {
                if (!lobby.Step(_time.GetUtcNow()))
                    return;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            // Step already contains game faults; reaching here is a kernel bug, and the lobby must still close.
            _log.LogError(ex, "Lobby loop {LobbyId} on {ServerId} stopped unexpectedly.", lobby.Id, Id);
            lobby.Shutdown("server_error", _time.GetUtcNow());
        }
    }

    internal async Task StopAsync()
    {
        foreach (var (lobby, _) in _lobbies.Values)
            lobby.Shutdown("server_shutdown", _time.GetUtcNow());
        await _stopping.CancelAsync();
        await Task.WhenAll(_lobbies.Values.Select(v => v.Loop));
    }
}

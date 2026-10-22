using SimLock.Core.Api;
using SimLock.Core.Display;
using SimLock.Core.Logging;
using SimLock.Core.Storage;
using SimLock.Core.Time;

namespace SimLock.Core.Runtime;

/// <summary>Un cuadro listo para dibujar + lo que haya que anunciar una sola vez.</summary>
public sealed record AgentFrame(DisplayModel Display, Announcement? Announcement, bool Paired);

/// <summary>Último estado guardado en disco, para seguir mostrando algo si la PC reinicia sin internet.</summary>
public sealed class SavedState
{
    public StateResponse? State { get; set; }
    public DateTimeOffset SavedAtLocalClock { get; set; }
}

/// <summary>
/// El corazón del agente, sin nada de interfaz: consulta /state, mantiene el reloj del servidor,
/// detecta conexión/desconexión, encola y envía eventos, y produce los cuadros que dibuja la UI.
/// </summary>
public sealed class AgentRuntime : IDisposable
{
    public static readonly TimeSpan DefaultPoll = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan HeartbeatEvery = TimeSpan.FromMinutes(10);
    /// <summary>Un estado guardado más viejo que esto no se usa al arrancar sin conexión.</summary>
    public static readonly TimeSpan MaxSavedStateAge = TimeSpan.FromHours(6);
    private static readonly TimeSpan[] Backoff =
        [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(60)];

    private readonly ISimLockApi _api;
    private readonly ISecretStore _secrets;
    private readonly EventQueue _queue;
    private readonly DisplayTracker _tracker;
    private readonly IMonotonicClock _mono;
    private readonly ServerClock _clock;
    private readonly ILog _log;
    private readonly string _appVersion;
    private readonly string? _savedStatePath;
    private readonly Func<DateTimeOffset> _wallClock;

    private readonly object _gate = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private StateResponse? _state;
    private int _consecutiveFailures;
    private bool _offline;
    private bool _everOnline;
    private long? _lastHeartbeatTimestamp;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    /// <summary>El servidor rechazó el token (equipo revocado o vinculado en otra PC). La UI debe mostrar la vinculación.</summary>
    public event Action? Unpaired;

    public AgentRuntime(
        ISimLockApi api,
        ISecretStore secrets,
        EventQueue queue,
        ISeenStore seen,
        ILog log,
        string appVersion,
        string? savedStatePath = null,
        IMonotonicClock? monotonic = null,
        Func<DateTimeOffset>? wallClock = null)
    {
        _api = api;
        _secrets = secrets;
        _queue = queue;
        _tracker = new DisplayTracker(seen);
        _log = log;
        _appVersion = appVersion;
        _savedStatePath = savedStatePath;
        _mono = monotonic ?? StopwatchClock.Instance;
        _clock = new ServerClock(_mono);
        _wallClock = wallClock ?? (() => DateTimeOffset.UtcNow);
        RestoreSavedState();
    }

    public bool IsPaired => !string.IsNullOrEmpty(_secrets.ReadToken());

    public bool IsOffline { get { lock (_gate) return _offline; } }

    public TimeSpan PollInterval
    {
        get
        {
            lock (_gate)
            {
                var s = _state?.PollSeconds ?? 0;
                return s <= 0 ? DefaultPoll : TimeSpan.FromSeconds(Math.Clamp(s, 5, 120));
            }
        }
    }

    // ───────────────────────── ciclo de vida ─────────────────────────

    public void Start()
    {
        if (_loop is not null) return;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    public async Task StopAsync()
    {
        if (_cts is null || _loop is null) return;
        _cts.Cancel();
        try { await _loop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        _loop = null;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        _log.Info($"Agente iniciado (versión {_appVersion}).");
        while (!ct.IsCancellationRequested)
        {
            TimeSpan wait;
            try
            {
                wait = await PollOnceAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Nunca dejar morir el ciclo por un error inesperado.
                _log.Error("Error inesperado en el ciclo de consulta", ex);
                wait = Backoff[^1];
            }

            if (wait == Timeout.InfiniteTimeSpan)
            {
                // Sin vincular: esperar a que la UI vincule (PairAsync despierta el ciclo).
                await _wake.WaitAsync(ct).ConfigureAwait(false);
                continue;
            }
            // Esperar el intervalo, o menos si alguien pide consultar ya.
            await _wake.WaitAsync(wait, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Una vuelta del ciclo: consultar estado, enviar eventos y heartbeat.
    /// Devuelve cuánto esperar hasta la próxima (InfiniteTimeSpan = no hay token, esperar vinculación).
    /// </summary>
    public async Task<TimeSpan> PollOnceAsync(CancellationToken ct)
    {
        var token = _secrets.ReadToken();
        if (string.IsNullOrEmpty(token)) return Timeout.InfiniteTimeSpan;

        StateFetch fetch;
        try
        {
            fetch = await _api.GetStateAsync(token, ct).ConfigureAwait(false);
        }
        catch (UnauthorizedException)
        {
            HandleUnauthorized();
            return Timeout.InfiniteTimeSpan;
        }
        catch (ApiException ex)
        {
            int failures;
            lock (_gate) failures = ++_consecutiveFailures;
            if (failures == 1 || failures % 20 == 0) _log.Warn($"Fallo al consultar estado (#{failures})", ex);
            if (failures >= 2) SetOffline(true);
            return Backoff[Math.Min(failures - 1, Backoff.Length - 1)];
        }

        _clock.Sync(fetch.State.ServerTime, fetch.ReceivedAtTimestamp, fetch.RoundTrip);
        lock (_gate)
        {
            var previous = _state?.State;
            _state = fetch.State;
            _consecutiveFailures = 0;
            if (previous != fetch.State.State) _log.Info($"Estado del servidor: {fetch.State.State}");
        }
        SetOffline(false);
        SaveState(fetch.State);

        await FlushEventsAsync(token, ct).ConfigureAwait(false);
        await MaybeHeartbeatAsync(token, ct).ConfigureAwait(false);
        return PollInterval;
    }

    /// <summary>Pide al ciclo que consulte ahora (por ejemplo, después de vincular).</summary>
    public void PollNow()
    {
        try { _wake.Release(); } catch (SemaphoreFullException) { }
    }

    // ───────────────────────── vinculación ─────────────────────────

    public async Task<PairResponse> PairAsync(string code, string deviceName, CancellationToken ct)
    {
        var res = await _api.PairAsync(code, deviceName, _appVersion, ct).ConfigureAwait(false);
        _secrets.WriteToken(res.DeviceToken);
        lock (_gate)
        {
            _state = null;
            _consecutiveFailures = 0;
            _offline = false;
            _lastHeartbeatTimestamp = null;
        }
        DeleteSavedState();
        _log.Info($"Equipo vinculado a la bay '{res.Bay?.Name}' de '{res.Venue?.Name}'.");
        PollNow();
        return res;
    }

    /// <summary>Desvinculación local (desde la configuración con PIN).</summary>
    public void Unpair()
    {
        _secrets.DeleteToken();
        lock (_gate) _state = null;
        DeleteSavedState();
        _log.Info("Equipo desvinculado localmente.");
    }

    private void HandleUnauthorized()
    {
        _log.Warn("El servidor rechazó el token (401): equipo revocado o vinculado en otra PC. Se pide vincular de nuevo.");
        _secrets.DeleteToken();
        lock (_gate)
        {
            _state = null;
            _offline = false;
        }
        DeleteSavedState();
        Unpaired?.Invoke();
    }

    // ───────────────────────── cuadros para la UI ─────────────────────────

    /// <summary>Lo llama la UI periódicamente (≈4 veces por segundo). Seguro desde cualquier hilo.</summary>
    public AgentFrame NextFrame()
    {
        StateResponse? state;
        bool offline;
        lock (_gate)
        {
            state = _state;
            offline = _offline;
        }
        var paired = IsPaired;

        if (state is null || !_clock.IsSynced)
            return new AgentFrame(DisplayModel.Starting(_wallClock()) with { Offline = offline }, null, paired);

        var display = StateProjector.Project(state, _clock.Now, offline);
        var (announcement, events) = _tracker.Observe(display);
        if (events.Count > 0) _queue.EnqueueRange(events);
        return new AgentFrame(display, announcement, paired);
    }

    // ───────────────────────── internos ─────────────────────────

    private void SetOffline(bool offline)
    {
        bool changed;
        bool wasEverOnline;
        lock (_gate)
        {
            changed = _offline != offline;
            _offline = offline;
            wasEverOnline = _everOnline;
            if (!offline) _everOnline = true;
        }
        if (!changed) return;

        _log.Info(offline ? "Sin conexión con Fore Booking." : "Conexión recuperada.");
        var at = _clock.IsSynced ? _clock.Now : _wallClock();
        if (offline || wasEverOnline)
            _queue.Enqueue(new AgentEvent { Kind = offline ? EventKinds.Offline : EventKinds.Online, At = at });
    }

    private async Task FlushEventsAsync(string token, CancellationToken ct)
    {
        // Como mucho unos pocos lotes por vuelta, para no demorar la próxima consulta.
        for (var i = 0; i < 5 && _queue.Count > 0; i++)
        {
            var batch = _queue.PeekBatch();
            try
            {
                await _api.PostEventsAsync(token, batch, ct).ConfigureAwait(false);
                _queue.Acknowledge(batch);
            }
            catch (UnauthorizedException)
            {
                HandleUnauthorized();
                return;
            }
            catch (ApiException ex)
            {
                _log.Warn("No se pudieron enviar eventos; quedan en cola", ex);
                return;
            }
        }
    }

    private async Task MaybeHeartbeatAsync(string token, CancellationToken ct)
    {
        // /state ya marca "conectado", pero no informa la versión instalada: el heartbeat sí.
        long? last;
        lock (_gate) last = _lastHeartbeatTimestamp;
        if (last is long ts && _mono.GetElapsed(ts) < HeartbeatEvery) return;
        try
        {
            await _api.HeartbeatAsync(token, _appVersion, ct).ConfigureAwait(false);
            lock (_gate) _lastHeartbeatTimestamp = _mono.GetTimestamp();
        }
        catch (UnauthorizedException)
        {
            HandleUnauthorized();
        }
        catch (ApiException ex)
        {
            _log.Warn("Heartbeat fallido", ex);
        }
    }

    private void SaveState(StateResponse state)
    {
        if (_savedStatePath is null) return;
        try { JsonFile.Write(_savedStatePath, new SavedState { State = state, SavedAtLocalClock = _wallClock() }); }
        catch (IOException ex) { _log.Warn("No se pudo guardar el último estado", ex); }
    }

    private void DeleteSavedState()
    {
        if (_savedStatePath is null) return;
        try { if (File.Exists(_savedStatePath)) File.Delete(_savedStatePath); } catch (IOException) { }
    }

    /// <summary>
    /// Arranque sin conexión: se retoma el último estado guardado. Como no hay respuesta del servidor,
    /// la hora se estima con el reloj de Windows (serverTime guardado + lo que pasó desde que se guardó).
    /// Es la única vez que se usa el reloj de Windows, y solo hasta la primera respuesta del servidor.
    /// </summary>
    private void RestoreSavedState()
    {
        if (_savedStatePath is null || !IsPaired) return;
        var saved = JsonFile.Read<SavedState>(_savedStatePath);
        if (saved?.State is null) return;
        var age = _wallClock() - saved.SavedAtLocalClock;
        if (age < TimeSpan.Zero || age > MaxSavedStateAge) return;

        _clock.Sync(saved.State.ServerTime + age, _mono.GetTimestamp(), TimeSpan.Zero);
        lock (_gate)
        {
            _state = saved.State;
            _offline = true; // hasta que el servidor conteste
        }
        _log.Info($"Retomado el último estado guardado ({saved.State.State}, hace {age.TotalMinutes:0} min).");
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _wake.Dispose();
    }
}

using SimLock.Core.Api;
using SimLock.Core.Display;
using SimLock.Core.Logging;
using SimLock.Core.Runtime;
using SimLock.Core.Storage;
using SimLock.Core.Time;

namespace SimLock.Core.Tests;

public sealed class FakeApi : ISimLockApi
{
    public Func<StateResponse>? NextState;
    public Exception? StateError;
    public Exception? EventsError;
    public readonly List<AgentEvent> PostedEvents = [];
    public readonly List<string> Heartbeats = [];
    public readonly List<int> EventBatchSizes = [];
    public FakeMonotonic Mono = new();
    public int StateCalls;

    public Task<PairResponse> PairAsync(string code, string deviceName, string appVersion, CancellationToken ct) =>
        code == "482913"
            ? Task.FromResult(new PairResponse { DeviceToken = new string('a', 64), Bay = new NamedRef { Id = "b", Name = "Bay 3" }, Venue = new VenueRef { Id = "v", Name = "Stick" } })
            : Task.FromException<PairResponse>(new UnauthorizedException("invalid_code"));

    public Task<StateFetch> GetStateAsync(string deviceToken, CancellationToken ct)
    {
        StateCalls++;
        if (StateError is not null) return Task.FromException<StateFetch>(StateError);
        return Task.FromResult(new StateFetch(NextState!(), Mono.GetTimestamp(), TimeSpan.Zero));
    }

    public Task HeartbeatAsync(string deviceToken, string appVersion, CancellationToken ct)
    {
        Heartbeats.Add(appVersion);
        return Task.CompletedTask;
    }

    public Task PostEventsAsync(string deviceToken, IReadOnlyList<AgentEvent> events, CancellationToken ct)
    {
        if (EventsError is not null) return Task.FromException(EventsError);
        if (events.Count > SimLockApiClient.MaxEventsPerRequest) throw new Exception("lote demasiado grande");
        EventBatchSizes.Add(events.Count);
        PostedEvents.AddRange(events);
        return Task.CompletedTask;
    }
}

public sealed class MemorySecrets(string? token = null) : ISecretStore
{
    public string? Token = token;
    public string? ReadToken() => Token;
    public void WriteToken(string token) => Token = token;
    public void DeleteToken() => Token = null;
}

public static class RuntimeTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 22, 21, 0, 0, TimeSpan.Zero);

    private static (AgentRuntime rt, FakeApi api, MemorySecrets secrets, EventQueue queue) Make(string? token = "tok", string? savedState = null, Func<DateTimeOffset>? wall = null)
    {
        var api = new FakeApi();
        var secrets = new MemorySecrets(token);
        var queue = new EventQueue(null);
        var rt = new AgentRuntime(api, secrets, queue, new SeenStore(null), NullLog.Instance, "1.0.0", savedState, api.Mono, wall);
        return (rt, api, secrets, queue);
    }

    [Test]
    public static async Task Sin_token_espera_vinculacion()
    {
        var (rt, api, _, _) = Make(token: null);
        Assert.Equal(Timeout.InfiniteTimeSpan, await rt.PollOnceAsync(default));
        Assert.Equal(0, api.StateCalls, "no consulta sin token");
        Assert.True(!rt.NextFrame().Paired, "no vinculado");
    }

    [Test]
    public static async Task Vincular_guarda_token()
    {
        var (rt, _, secrets, _) = Make(token: null);
        await Assert.Throws<UnauthorizedException>(() => rt.PairAsync("000000", "PC", default));
        Assert.True(secrets.Token is null, "código inválido no guarda nada");
        var res = await rt.PairAsync("482913", "PC", default);
        Assert.Equal("Bay 3", res.Bay?.Name);
        Assert.Equal(new string('a', 64), secrets.Token);
    }

    [Test]
    public static async Task Cuenta_con_hora_del_servidor_no_de_windows()
    {
        // El reloj de Windows está 3 horas mal: no debe importar.
        var (rt, api, _, _) = Make(wall: () => T0.AddHours(3));
        api.NextState = () => ProjectorTests.S("in_progress", T0.AddMinutes(-30), T0.AddMinutes(30)) ;
        await rt.PollOnceAsync(default);
        api.Mono.Advance(TimeSpan.FromSeconds(90));
        var f = rt.NextFrame();
        Assert.Equal(TimeSpan.FromSeconds(30 * 60 - 90), f.Display.Remaining, "remaining");
    }

    [Test]
    public static async Task Intervalo_respeta_pollSeconds_con_limites()
    {
        var (rt, api, _, _) = Make();
        api.NextState = () => ProjectorTests.S("idle");
        Assert.Equal(TimeSpan.FromSeconds(15), await rt.PollOnceAsync(default));
        api.NextState = () => new StateResponse { ServerTime = T0, State = "idle", PollSeconds = 1 };
        Assert.Equal(TimeSpan.FromSeconds(5), await rt.PollOnceAsync(default), "mínimo 5 s");
    }

    [Test]
    public static async Task Sin_conexion_backoff_e_indicador_y_eventos()
    {
        var (rt, api, _, queue) = Make();
        api.NextState = () => ProjectorTests.S("in_progress", T0.AddMinutes(-30), T0.AddMinutes(30));
        await rt.PollOnceAsync(default);

        api.StateError = new ApiException("caída");
        Assert.Equal(TimeSpan.FromSeconds(5), await rt.PollOnceAsync(default));
        Assert.True(!rt.IsOffline, "un solo fallo no marca sin conexión");
        Assert.Equal(TimeSpan.FromSeconds(10), await rt.PollOnceAsync(default));
        Assert.True(rt.IsOffline, "dos fallos = sin conexión");
        for (var i = 0; i < 10; i++) await rt.PollOnceAsync(default);
        Assert.Equal(TimeSpan.FromSeconds(60), await rt.PollOnceAsync(default), "backoff máximo 60 s");

        // Sigue contando con el último estado.
        api.Mono.Advance(TimeSpan.FromMinutes(31));
        var f = rt.NextFrame();
        Assert.True(f.Display.Offline, "indicador sin conexión");
        Assert.Equal(AgentState.Ended, f.Display.State, "termina en el endsAt conocido aun sin red");

        api.StateError = null;
        await rt.PollOnceAsync(default);
        Assert.True(!rt.IsOffline, "recuperado");
        var kinds = api.PostedEvents.Select(e => e.Kind).ToList();
        Assert.True(kinds.Contains(EventKinds.Offline) && kinds.Contains(EventKinds.Online), "eventos offline/online enviados al volver");
        Assert.Equal(0, queue.Count, "cola vacía");
    }

    [Test]
    public static async Task Token_revocado_pide_vincular()
    {
        var (rt, api, secrets, _) = Make();
        var fired = false;
        rt.Unpaired += () => fired = true;
        api.StateError = new UnauthorizedException("revoked");
        Assert.Equal(Timeout.InfiniteTimeSpan, await rt.PollOnceAsync(default));
        Assert.True(fired, "evento Unpaired");
        Assert.True(secrets.Token is null, "token borrado");
    }

    [Test]
    public static async Task Eventos_en_lotes_de_50()
    {
        var (rt, api, _, queue) = Make();
        for (var i = 0; i < 120; i++) queue.Enqueue(new AgentEvent { Kind = EventKinds.Error, At = T0, Detail = i.ToString() });
        api.NextState = () => ProjectorTests.S("idle");
        await rt.PollOnceAsync(default);
        Assert.Equal("50,50,20", string.Join(",", api.EventBatchSizes));
        Assert.Equal(0, queue.Count, "todo enviado");
    }

    [Test]
    public static async Task Eventos_quedan_en_cola_si_falla_el_envio()
    {
        var (rt, api, _, queue) = Make();
        queue.Enqueue(new AgentEvent { Kind = EventKinds.Error, At = T0 });
        api.NextState = () => ProjectorTests.S("idle");
        api.EventsError = new ApiException("500");
        await rt.PollOnceAsync(default);
        Assert.Equal(1, queue.Count, "sigue en cola");
        Assert.True(!rt.IsOffline, "fallar eventos no marca sin conexión");
    }

    [Test]
    public static async Task Heartbeat_al_arrancar_y_cada_10_min_con_version()
    {
        var (rt, api, _, _) = Make();
        api.NextState = () => ProjectorTests.S("idle");
        await rt.PollOnceAsync(default);
        await rt.PollOnceAsync(default);
        Assert.Equal(1, api.Heartbeats.Count, "uno al arrancar");
        api.Mono.Advance(TimeSpan.FromMinutes(10));
        await rt.PollOnceAsync(default);
        Assert.Equal(2, api.Heartbeats.Count, "otro a los 10 min");
        Assert.Equal("1.0.0", api.Heartbeats[0]);
    }

    [Test]
    public static async Task NextFrame_genera_eventos_de_auditoria()
    {
        var (rt, api, _, _) = Make();
        api.NextState = () => ProjectorTests.S("last_minutes", T0.AddMinutes(-50), T0.AddMinutes(9));
        await rt.PollOnceAsync(default);
        var f = rt.NextFrame();
        Assert.Equal(10, f.Announcement?.WarningMinutes, "anuncio");
        await rt.PollOnceAsync(default);
        var kinds = api.PostedEvents.Select(e => e.Kind).ToHashSet();
        Assert.True(kinds.SetEquals([EventKinds.Started, EventKinds.WarningShown, EventKinds.QrShown]), string.Join(",", kinds));
        Assert.True(api.PostedEvents.All(e => e.BookingId == "bk1"), "con bookingId");
    }

    [Test]
    public static async Task Arranque_sin_red_retoma_ultimo_estado()
    {
        using var dir = new TempDir();
        var path = dir.File("last-state.json");
        var wall = T0;
        var (rt1, api1, _, _) = Make(savedState: path, wall: () => wall);
        api1.NextState = () => ProjectorTests.S("in_progress", T0.AddMinutes(-30), T0.AddMinutes(30));
        await rt1.PollOnceAsync(default);

        // La PC reinicia 10 minutos después, sin internet.
        wall = T0.AddMinutes(10);
        var (rt2, api2, _, _) = Make(savedState: path, wall: () => wall);
        api2.StateError = new ApiException("sin red");
        var f = rt2.NextFrame();
        Assert.Equal(AgentState.InProgress, f.Display.State);
        Assert.True(f.Display.Offline, "marca sin conexión");
        Assert.Equal(TimeSpan.FromMinutes(20), f.Display.Remaining, "remaining estimado");
    }

    [Test]
    public static async Task Estado_guardado_viejo_se_ignora()
    {
        using var dir = new TempDir();
        var path = dir.File("last-state.json");
        var wall = T0;
        var (rt1, api1, _, _) = Make(savedState: path, wall: () => wall);
        api1.NextState = () => ProjectorTests.S("in_progress", T0.AddMinutes(-30), T0.AddMinutes(30));
        await rt1.PollOnceAsync(default);
        wall = T0.AddHours(7);
        var (rt2, _, _, _) = Make(savedState: path, wall: () => wall);
        Assert.Equal(AgentState.Unknown, rt2.NextFrame().Display.State);
    }

    [Test]
    public static void Reloj_servidor_compensa_medio_viaje()
    {
        var mono = new FakeMonotonic();
        var c = new ServerClock(mono);
        c.Sync(T0, mono.GetTimestamp(), TimeSpan.FromMilliseconds(400));
        Assert.Equal(T0.AddMilliseconds(200), c.Now);
        mono.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(T0.AddMilliseconds(5200), c.Now);
    }
}

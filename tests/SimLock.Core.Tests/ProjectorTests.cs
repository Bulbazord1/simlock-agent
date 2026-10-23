using SimLock.Core.Api;
using SimLock.Core.Display;
using SimLock.Core.Storage;

namespace SimLock.Core.Tests;

public static class ProjectorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 22, 21, 0, 0, TimeSpan.Zero);

    public static StateResponse S(string state, DateTimeOffset? starts = null, DateTimeOffset? ends = null,
        bool requireCheckin = false, bool checkedIn = true, bool canExtend = true, string? qr = "https://x/qr",
        int[]? warnings = null, MessageInfo? message = null, long pending = 0) => new()
    {
        ServerTime = T0,
        PollSeconds = 15,
        State = state,
        WarningMinutes = warnings ?? [15, 10, 5],
        RequireCheckin = requireCheckin,
        CanExtend = canExtend,
        QrUrl = qr,
        Message = message,
        Venue = new VenueRef { Id = "v", Name = "Stick", Timezone = "America/Argentina/Buenos_Aires" },
        Bay = new NamedRef { Id = "b", Name = "Bay 3" },
        Booking = starts is null ? null : new BookingInfo
        {
            Id = "bk1",
            StartsAt = starts.Value,
            EndsAt = ends!.Value,
            CustomerFirstName = "Juan",
            CheckedIn = checkedIn,
            PendingPaymentCents = pending,
            Currency = "ARS",
        },
    };

    [Test]
    public static void Disabled_oculta_todo()
    {
        var m = StateProjector.Project(S("disabled", qr: "https://x"), T0, false);
        Assert.Equal(AgentState.Disabled, m.State);
        Assert.True(!m.ShowQr && m.MessageText is null, "sin QR ni mensaje");
    }

    [Test]
    public static void InProgress_cuenta_desde_endsAt()
    {
        var m = StateProjector.Project(S("in_progress", T0.AddMinutes(-30), T0.AddMinutes(30)), T0, false);
        Assert.Equal(AgentState.InProgress, m.State);
        Assert.Equal(TimeSpan.FromMinutes(30), m.Remaining, "remaining");
        Assert.True(!m.ShowQr, "en juego no se muestra QR");
    }

    [Test]
    public static void Avanza_solo_a_ultimos_minutos_entre_polls()
    {
        var s = S("in_progress", T0.AddMinutes(-50), T0.AddMinutes(16));
        Assert.Equal(AgentState.InProgress, StateProjector.Project(s, T0, false).State);
        var later = StateProjector.Project(s, T0.AddMinutes(1).AddSeconds(1), false);
        Assert.Equal(AgentState.LastMinutes, later.State);
        Assert.Equal(15, later.ActiveWarningMinutes, "umbral activo");
        Assert.True(later.ShowQr, "QR de extensión visible");
    }

    [Test]
    public static void Umbral_activo_es_el_menor_cruzado()
    {
        Assert.Equal(10, StateProjector.ActiveWarning(TimeSpan.FromSeconds(540), [15, 10, 5]));
        Assert.Equal(5, StateProjector.ActiveWarning(TimeSpan.FromSeconds(10), [15, 10, 5]));
        Assert.Equal<int?>(null, StateProjector.ActiveWarning(TimeSpan.FromMinutes(20), [15, 10, 5]));
        Assert.Equal<int?>(null, StateProjector.ActiveWarning(TimeSpan.FromMinutes(1), null));
    }

    [Test]
    public static void Termina_en_endsAt_y_no_antes()
    {
        var s = S("last_minutes", T0.AddMinutes(-55), T0.AddMinutes(5));
        Assert.Equal(AgentState.LastMinutes, StateProjector.Project(s, T0.AddMinutes(5).AddMilliseconds(-1), true).State);
        var ended = StateProjector.Project(s, T0.AddMinutes(5), true);
        Assert.Equal(AgentState.Ended, ended.State);
        Assert.True(ended.ShowQr, "QR en terminado si canExtend");
        Assert.Equal<TimeSpan?>(null, ended.Remaining, "sin cuenta en terminado");
    }

    [Test]
    public static void Terminado_sin_extension_no_muestra_QR()
    {
        var s = S("ended", T0.AddMinutes(-60), T0.AddMinutes(-1), canExtend: false);
        var m = StateProjector.Project(s, T0, false);
        Assert.Equal(AgentState.Ended, m.State);
        Assert.True(!m.ShowQr, "sin QR");
    }

    [Test]
    public static void Terminado_vuelve_a_espera_a_los_15_min()
    {
        var s = S("ended", T0.AddMinutes(-60), T0);
        Assert.Equal(AgentState.Ended, StateProjector.Project(s, T0.AddMinutes(14), true).State);
        Assert.Equal(AgentState.Idle, StateProjector.Project(s, T0.AddMinutes(15), true).State);
    }

    [Test]
    public static void Upcoming_pasa_a_juego_al_empezar()
    {
        var s = S("upcoming", T0.AddMinutes(1), T0.AddMinutes(61));
        var before = StateProjector.Project(s, T0, false);
        Assert.Equal(AgentState.Upcoming, before.State);
        Assert.True(before.ShowQr, "QR de check-in en upcoming");
        Assert.Equal(AgentState.InProgress, StateProjector.Project(s, T0.AddMinutes(1), false).State);
    }

    [Test]
    public static void Upcoming_con_checkin_obligatorio_espera_checkin()
    {
        var s = S("upcoming", T0.AddMinutes(1), T0.AddMinutes(61), requireCheckin: true, checkedIn: false);
        var m = StateProjector.Project(s, T0.AddMinutes(2), false);
        Assert.Equal(AgentState.AwaitingCheckin, m.State);
        Assert.Equal<TimeSpan?>(null, m.Remaining, "sin cuenta regresiva esperando check-in");
        Assert.True(m.ShowQr, "QR de check-in");
    }

    [Test]
    public static void AwaitingCheckin_no_arranca_solo_pero_si_termina()
    {
        var s = S("awaiting_checkin", T0.AddMinutes(-10), T0.AddMinutes(50), requireCheckin: true, checkedIn: false);
        Assert.Equal(AgentState.AwaitingCheckin, StateProjector.Project(s, T0.AddMinutes(10), false).State);
        Assert.Equal(AgentState.Ended, StateProjector.Project(s, T0.AddMinutes(50), false).State);
    }

    [Test]
    public static void No_retrocede_estado_del_servidor()
    {
        // El staff terminó el turno antes: el servidor manda ended aunque falte para endsAt.
        var s = S("ended", T0.AddMinutes(-10), T0.AddMinutes(50));
        Assert.Equal(AgentState.Ended, StateProjector.Project(s, T0, false).State);
    }

    [Test]
    public static void Mensaje_vence()
    {
        var s = S("in_progress", T0.AddMinutes(-10), T0.AddMinutes(50),
            message: new MessageInfo { Text = "Pasá por recepción", ExpiresAt = T0.AddMinutes(1) });
        Assert.Equal("Pasá por recepción", StateProjector.Project(s, T0, false).MessageText);
        Assert.Equal<string?>(null, StateProjector.Project(s, T0.AddMinutes(1), false).MessageText);
    }

    [Test]
    public static void Estado_desconocido_se_trata_como_espera()
    {
        var m = StateProjector.Project(S("algo_nuevo"), T0, false);
        Assert.Equal(AgentState.Idle, m.State);
    }

    [Test]
    public static void Pago_pendiente_se_propaga()
    {
        var m = StateProjector.Project(S("in_progress", T0.AddMinutes(-10), T0.AddMinutes(50), pending: 1500000), T0, false);
        Assert.Equal(1500000L, m.PendingPaymentCents);
    }

    [Test]
    public static void Tracker_avisa_una_vez_por_umbral_y_de_nuevo_tras_extender()
    {
        var tracker = new DisplayTracker(new SeenStore(null));
        var s = S("in_progress", T0.AddMinutes(-50), T0.AddMinutes(11));

        var first = tracker.Observe(StateProjector.Project(s, T0, false));
        Assert.Equal(15, first.Announcement?.WarningMinutes, "a los 11 min ya cruzó el de 15");
        Assert.True(first.Events.Any(e => e.Kind == EventKinds.Started), "evento started");

        var a10 = tracker.Observe(StateProjector.Project(s, T0.AddMinutes(1), false));
        Assert.Equal(10, a10.Announcement?.WarningMinutes, "aviso de 10");
        var again = tracker.Observe(StateProjector.Project(s, T0.AddMinutes(1).AddSeconds(1), false));
        Assert.True(again.Announcement is null, "no se repite");
        Assert.True(!again.Events.Any(e => e.Kind == EventKinds.Started), "started una sola vez");

        // Se extendió 30 min: nuevo endsAt → los avisos vuelven a aplicar.
        var extended = S("in_progress", T0.AddMinutes(-50), T0.AddMinutes(41));
        var after = tracker.Observe(StateProjector.Project(extended, T0.AddMinutes(31), false));
        Assert.Equal(10, after.Announcement?.WarningMinutes, "aviso de 10 tras extender");
    }

    [Test]
    public static void Tracker_persiste_entre_reinicios()
    {
        using var dir = new TempDir();
        var s = S("last_minutes", T0.AddMinutes(-50), T0.AddMinutes(9));
        var m = StateProjector.Project(s, T0, false);
        Assert.Equal(10, new DisplayTracker(new SeenStore(dir.File("seen.json"))).Observe(m).Announcement?.WarningMinutes);
        Assert.True(new DisplayTracker(new SeenStore(dir.File("seen.json"))).Observe(m).Announcement is null, "no repite tras reiniciar");
    }

    [Test]
    public static void Tracker_al_arrancar_tarde_solo_anuncia_el_umbral_actual()
    {
        var tracker = new DisplayTracker(new SeenStore(null));
        var m = StateProjector.Project(S("last_minutes", T0.AddMinutes(-58), T0.AddMinutes(2)), T0, false);
        var r = tracker.Observe(m);
        Assert.Equal(5, r.Announcement?.WarningMinutes);
        Assert.Equal(1, r.Events.Count(e => e.Kind == EventKinds.WarningShown), "un solo warning_shown");
    }

    [Test]
    public static void Reposo_sin_reserva_y_a_los_3_min_de_terminar()
    {
        Assert.True(StateProjector.Project(S("idle"), T0, false).Resting, "idle = reposo");
        var s = S("ended", T0.AddMinutes(-60), T0);
        var recien = StateProjector.Project(s, T0.AddMinutes(2), false);
        Assert.True(!recien.Resting && recien.State == AgentState.Ended, "los primeros minutos muestra 'terminó'");
        var luego = StateProjector.Project(s, T0.AddMinutes(3), false);
        Assert.True(luego.Resting && luego.State == AgentState.Ended, "después reposo, sigue siendo ended");
        Assert.True(luego.ShowQr, "en reposo conserva el QR para extender");
        Assert.True(!StateProjector.Project(S("in_progress", T0.AddMinutes(-5), T0.AddMinutes(30)), T0, false).Resting, "jugando no es reposo");
    }

    [Test]
    public static void Reposo_muestra_QR_de_reserva_si_el_servidor_lo_manda()
    {
        var con = StateProjector.Project(S("idle", qr: "https://app.forebooking.com/o/stick/stick-nordelta/jugar"), T0, false);
        Assert.True(con.Resting && con.ShowQr, "idle con qrUrl muestra QR");
        var sin = StateProjector.Project(S("idle", qr: null), T0, false);
        Assert.True(sin.Resting && !sin.ShowQr, "idle sin qrUrl no muestra QR");
    }

    [Test]
    public static void ScreenUrl_solo_https_y_nunca_en_disabled()
    {
        StateResponse With(string state, string? url) => new()
        {
            ServerTime = T0, State = state, ScreenUrl = url,
        };
        Assert.Equal("https://panel.forebooking.com/simlock/screen/abc",
            StateProjector.Project(With("idle", "https://panel.forebooking.com/simlock/screen/abc"), T0, false).ScreenUrl);
        Assert.Equal<string?>(null, StateProjector.Project(With("idle", "http://evil.example/x"), T0, false).ScreenUrl, "http no");
        Assert.Equal<string?>(null, StateProjector.Project(With("idle", "javascript:alert(1)"), T0, false).ScreenUrl, "esquema raro no");
        Assert.Equal<string?>(null, StateProjector.Project(With("disabled", "https://x.example/s"), T0, false).ScreenUrl, "disabled no");
        Assert.True(AgentState.Ended.UsesWebScreen() && !AgentState.InProgress.UsesWebScreen(), "qué estados usan la web");
    }
}

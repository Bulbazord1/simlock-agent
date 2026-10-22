using System.Net;
using System.Text;
using System.Text.Json;
using SimLock.Core.Api;
using SimLock.Core.Qr;
using SimLock.Core.Storage;
using SimLock.Core.Time;

namespace SimLock.Core.Tests;

public static class MiscTests
{
    [Test]
    public static void Pin_verifica_y_rechaza()
    {
        var c = new AgentConfig();
        Assert.True(!Pin.Verify(c, "1234"), "sin PIN configurado no valida");
        Pin.Set(c, "1234");
        Assert.True(Pin.Verify(c, "1234"), "PIN correcto");
        Assert.True(!Pin.Verify(c, "1235"), "PIN incorrecto");
        Assert.True(!Pin.IsValidFormat("12a4") && !Pin.IsValidFormat("123"), "formato");
    }

    [Test]
    public static void Config_persiste()
    {
        using var dir = new TempDir();
        var store = new ConfigStore(dir.File("config.json"));
        var c = store.Load();
        Assert.Equal(AgentConfig.DefaultBaseUrl, c.BaseUrl);
        c.MonitorDevice = @"\\.\DISPLAY2";
        Pin.Set(c, "4321");
        store.Save(c);
        var again = store.Load();
        Assert.Equal(@"\\.\DISPLAY2", again.MonitorDevice);
        Assert.True(Pin.Verify(again, "4321"), "PIN tras recargar");
    }

    [Test]
    public static void Cola_de_eventos_persiste_y_tiene_tope()
    {
        using var dir = new TempDir();
        var q = new EventQueue(dir.File("q.json"));
        q.Enqueue(new AgentEvent { Kind = "offline", At = DateTimeOffset.UnixEpoch });
        Assert.Equal(1, new EventQueue(dir.File("q.json")).Count, "sobrevive reinicio");
        for (var i = 0; i < EventQueue.MaxQueued + 10; i++) q.Enqueue(new AgentEvent { Kind = "error", At = DateTimeOffset.UnixEpoch });
        Assert.Equal(EventQueue.MaxQueued, q.Count, "tope");
    }

    [Test]
    public static void Formatos_de_hora()
    {
        Assert.Equal("9:00", VenueTime.FormatCountdown(TimeSpan.FromSeconds(540)));
        Assert.Equal("0:01", VenueTime.FormatCountdown(TimeSpan.FromMilliseconds(200)), "redondea hacia arriba");
        Assert.Equal("1:05:03", VenueTime.FormatCountdown(TimeSpan.FromSeconds(3903)));
        Assert.Equal("0:00", VenueTime.FormatCountdown(TimeSpan.FromSeconds(-5)));
        var instant = new DateTimeOffset(2026, 10, 22, 21, 30, 0, TimeSpan.Zero);
        Assert.Equal("18:30", VenueTime.FormatHour(instant, "America/Argentina/Buenos_Aires"));
    }

    [Test]
    public static void Json_del_contrato_se_lee_bien()
    {
        // Ejemplo literal de la sección 2 del contrato.
        const string json = """
        {
          "serverTime": "2026-10-22T21:30:00.000Z",
          "pollSeconds": 15,
          "bay": { "id": "uuid", "name": "Bay 3" },
          "venue": { "id": "uuid", "name": "Stick Palermo", "timezone": "America/Argentina/Buenos_Aires" },
          "state": "last_minutes",
          "remainingSeconds": 540,
          "activeWarningMinutes": 10,
          "warningMinutes": [15, 10, 5],
          "requireCheckin": false,
          "booking": {
            "id": "uuid",
            "startsAt": "2026-10-22T20:40:00.000Z",
            "endsAt": "2026-10-22T21:39:00.000Z",
            "customerFirstName": "Juan",
            "checkedIn": true,
            "pendingPaymentCents": 0,
            "currency": "ARS"
          },
          "qrUrl": "https://panel.forebooking.com/x/abc",
          "canExtend": true,
          "message": { "text": "Pasá por recepción", "expiresAt": "2026-10-22T21:35:00.000Z" },
          "branding": { "logoUrl": "https://example.com/logo.png", "primaryColor": "#0B3D2E" },
          "campoNuevoQueNoConozco": 1
        }
        """;
        var s = JsonSerializer.Deserialize<StateResponse>(json, Json.Options)!;
        Assert.Equal("last_minutes", s.State);
        Assert.Equal(new DateTimeOffset(2026, 10, 22, 21, 39, 0, TimeSpan.Zero), s.Booking!.EndsAt);
        Assert.Equal("#0B3D2E", s.Branding!.PrimaryColor);
        var m = Display.StateProjector.Project(s, s.ServerTime, false);
        Assert.Equal(TimeSpan.FromSeconds(540), m.Remaining, "coincide con remainingSeconds del ejemplo");
        Assert.Equal(10, m.ActiveWarningMinutes);

        var disabled = JsonSerializer.Deserialize<StateResponse>("""{"serverTime":"2026-10-22T21:30:00Z","state":"disabled"}""", Json.Options)!;
        Assert.Equal(Display.AgentState.Disabled, Display.StateProjector.Project(disabled, disabled.ServerTime, false).State);
    }

    [Test]
    public static void Qr_tamanos_validos()
    {
        var q = QrCode.EncodeText("https://panel.forebooking.com/x/" + new string('k', 43));
        Assert.Equal(q.Version * 4 + 17, q.Size);
        Assert.True(q.Version <= 6, $"una URL típica entra en versión chica (fue {q.Version})");
    }

    // ─────────────── cliente HTTP real contra un servidor falso local ───────────────

    [Test]
    public static async Task Cliente_http_habla_el_contrato()
    {
        using var server = new FakeServer();
        using var client = new SimLockApiClient(new Uri(server.BaseUrl), "SimLock/1.0.0");

        var pair = await client.PairAsync(" 482913 ", "PC Bay 3", "1.0.0", default);
        Assert.Equal(new string('f', 64), pair.DeviceToken);
        Assert.Equal("Stick Palermo", pair.Venue?.Name);
        Assert.Equal("""{"code":"482913","deviceName":"PC Bay 3","appVersion":"1.0.0"}""", server.LastBody["/api/public/simlock/pair"]);

        var ex = await Assert.Throws<UnauthorizedException>(() => client.PairAsync("000000", "PC", "1.0.0", default));
        Assert.True(ex.Message.Contains("invalid_code"), "detalle del 401");

        var st = await client.GetStateAsync(pair.DeviceToken, default);
        Assert.Equal("in_progress", st.State.State);
        Assert.Equal("Bearer " + pair.DeviceToken, server.LastAuth["/api/public/simlock/state"]);
        Assert.Equal("GET", server.LastMethod["/api/public/simlock/state"]);

        await Assert.Throws<UnauthorizedException>(() => client.GetStateAsync("revocado", default));

        await client.HeartbeatAsync(pair.DeviceToken, "1.0.1", default);
        Assert.Equal("""{"appVersion":"1.0.1"}""", server.LastBody["/api/public/simlock/heartbeat"]);

        await client.PostEventsAsync(pair.DeviceToken,
            [new AgentEvent { Kind = "warning_shown", BookingId = "bk", At = new DateTimeOffset(2026, 10, 22, 21, 30, 0, TimeSpan.Zero), Detail = "10" }], default);
        Assert.Equal("""{"events":[{"kind":"warning_shown","bookingId":"bk","at":"2026-10-22T21:30:00+00:00","detail":"10"}]}""",
            server.LastBody["/api/public/simlock/events"]);

        server.FailWith = HttpStatusCode.InternalServerError;
        await Assert.Throws<ApiException>(() => client.GetStateAsync(pair.DeviceToken, default));
    }

    [Test]
    public static async Task Cliente_http_sin_servidor_es_sin_conexion()
    {
        using var client = new SimLockApiClient(new Uri("http://127.0.0.1:9/"), "SimLock/test");
        await Assert.Throws<ApiException>(() => client.GetStateAsync("x", default));
    }
}

/// <summary>Servidor HTTP mínimo que imita a Fore Booking según el contrato.</summary>
public sealed class FakeServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    public string BaseUrl { get; }
    public Dictionary<string, string> LastBody { get; } = [];
    public Dictionary<string, string?> LastAuth { get; } = [];
    public Dictionary<string, string> LastMethod { get; } = [];
    public HttpStatusCode? FailWith;

    public FakeServer()
    {
        var port = Random.Shared.Next(20000, 60000);
        BaseUrl = $"http://127.0.0.1:{port}/";
        _listener.Prefixes.Add(BaseUrl);
        _listener.Start();
        _ = Task.Run(LoopAsync);
    }

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); } catch { return; }
            var path = ctx.Request.Url!.AbsolutePath;
            using (var r = new StreamReader(ctx.Request.InputStream)) LastBody[path] = await r.ReadToEndAsync();
            LastAuth[path] = ctx.Request.Headers["Authorization"];
            LastMethod[path] = ctx.Request.HttpMethod;

            var (status, body) = FailWith is { } f ? ((int)f, "{\"error\":\"boom\"}") : Route(path, ctx.Request.Headers["Authorization"]);
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json";
            var bytes = Encoding.UTF8.GetBytes(body);
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }
    }

    private (int, string) Route(string path, string? auth)
    {
        var token = new string('f', 64);
        if (path == "/api/public/simlock/pair")
            return LastBody[path].Contains("\"482913\"")
                ? (200, $$$"""{"deviceToken":"{{{token}}}","bay":{"id":"b","name":"Bay 3"},"venue":{"id":"v","name":"Stick Palermo"}}""")
                : (401, """{"error":"invalid_code"}""");
        if (auth != "Bearer " + token) return (401, """{"error":"unauthorized"}""");
        return path switch
        {
            "/api/public/simlock/state" => (200, """{"serverTime":"2026-10-22T21:30:00.000Z","pollSeconds":15,"state":"in_progress","booking":{"id":"bk","startsAt":"2026-10-22T20:40:00Z","endsAt":"2026-10-22T21:39:00Z","checkedIn":true}}"""),
            "/api/public/simlock/heartbeat" or "/api/public/simlock/events" => (200, "{\"ok\":true}"),
            _ => (404, "{}"),
        };
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Close();
    }
}

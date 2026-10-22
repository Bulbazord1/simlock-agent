using System.Text;
using SimLock.Core.Api;
using SimLock.Core.Display;
using SimLock.Core.Logging;
using SimLock.Core.Qr;
using SimLock.Core.Runtime;
using SimLock.Core.Storage;
using SimLock.Core.Time;

// Herramienta de consola para probar el backend de SimLock desde cualquier PC (Windows, Mac, Linux)
// sin instalar el overlay. Usa exactamente el mismo núcleo que el agente.
//
//   simlock-cli pair 482913 [--url https://panel.forebooking.com] [--name "PC prueba"]
//   simlock-cli watch      → muestra lo que vería el overlay, actualizado cada segundo
//   simlock-cli state      → un pedido a /state y muestra el JSON interpretado
//   simlock-cli unpair     → borra el token local

Console.OutputEncoding = Encoding.UTF8;
var argv = args.ToList();
string? Opt(string name)
{
    var i = argv.IndexOf(name);
    if (i < 0 || i + 1 >= argv.Count) return null;
    var v = argv[i + 1];
    argv.RemoveRange(i, 2);
    return v;
}

var dataDir = Opt("--data") ?? Path.Combine(Environment.CurrentDirectory, ".simlock-cli");
var paths = new AgentPaths(dataDir);
paths.EnsureCreated();
var configStore = new ConfigStore(paths.Config);
var config = configStore.Load();
if (Opt("--url") is { } url) { config.BaseUrl = url.TrimEnd('/'); configStore.Save(config); }
if (Opt("--name") is { } name) { config.DeviceName = name; configStore.Save(config); }

var command = argv.FirstOrDefault() ?? "help";
var secrets = new PlainFileSecretStore(paths.Token);
var log = new FileLog(paths.Logs, alsoConsole: command != "watch");
using var api = new SimLockApiClient(new Uri(config.BaseUrl.TrimEnd('/') + "/"), "SimLock-CLI/1.0.0");
using var runtime = new AgentRuntime(api, secrets, new EventQueue(paths.Events), new SeenStore(paths.Seen), log, "cli-1.0.0", paths.LastState);

switch (command)
{
    case "pair":
    {
        var code = argv.ElementAtOrDefault(1);
        if (string.IsNullOrWhiteSpace(code)) { Console.Error.WriteLine("Falta el código: simlock-cli pair 482913"); return 2; }
        try
        {
            var res = await runtime.PairAsync(code, config.DeviceName, default);
            config.BayId = res.Bay?.Id; config.BayName = res.Bay?.Name;
            config.VenueId = res.Venue?.Id; config.VenueName = res.Venue?.Name;
            config.PairedAt = DateTimeOffset.Now;
            configStore.Save(config);
            Console.WriteLine($"Vinculado: {res.Bay?.Name} — {res.Venue?.Name}");
            return 0;
        }
        catch (UnauthorizedException) { Console.Error.WriteLine("Código inválido, usado o vencido."); return 1; }
        catch (ApiException ex) { Console.Error.WriteLine(ex.Message); return 1; }
    }

    case "unpair":
        runtime.Unpair();
        Console.WriteLine("Token local borrado.");
        return 0;

    case "state":
    {
        var token = secrets.ReadToken();
        if (token is null) { Console.Error.WriteLine("No vinculado. Usá: simlock-cli pair <código>"); return 1; }
        try
        {
            var fetch = await api.GetStateAsync(token, default);
            var s = fetch.State;
            Console.WriteLine($"HTTP OK en {fetch.RoundTrip.TotalMilliseconds:0} ms");
            Console.WriteLine($"serverTime={s.ServerTime:O} pollSeconds={s.PollSeconds} state={s.State} remainingSeconds={s.RemainingSeconds}");
            Console.WriteLine($"bay={s.Bay?.Name} venue={s.Venue?.Name} tz={s.Venue?.Timezone} requireCheckin={s.RequireCheckin} canExtend={s.CanExtend}");
            if (s.Booking is { } b)
                Console.WriteLine($"booking={b.Id} {b.StartsAt:O} → {b.EndsAt:O} checkedIn={b.CheckedIn} pending={b.PendingPaymentCents} {b.Currency}");
            Console.WriteLine($"qrUrl={s.QrUrl} message={s.Message?.Text}");
            var m = StateProjector.Project(s, s.ServerTime, false);
            Console.WriteLine($"→ el agente mostraría: {Describe(m)}");
            if (s.RemainingSeconds is int rs && m.Remaining is TimeSpan r && Math.Abs(rs - r.TotalSeconds) > 2)
                Console.WriteLine($"⚠ remainingSeconds del servidor ({rs}) no coincide con endsAt - serverTime ({r.TotalSeconds:0})");
            return 0;
        }
        catch (UnauthorizedException) { Console.Error.WriteLine("401: token inválido o equipo revocado."); return 1; }
        catch (ApiException ex) { Console.Error.WriteLine(ex.Message); return 1; }
    }

    case "watch":
    {
        if (secrets.ReadToken() is null) { Console.Error.WriteLine("No vinculado. Usá: simlock-cli pair <código>"); return 1; }
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        runtime.Unpaired += () => { Console.WriteLine("\nEl servidor revocó este equipo (401)."); cts.Cancel(); };
        runtime.Start();
        string? lastQr = null;
        while (!cts.IsCancellationRequested)
        {
            var f = runtime.NextFrame();
            if (f.Announcement is { } a) Console.WriteLine($"\n🔔 AVISO: quedan {a.WarningMinutes} minutos");
            var line = $"[{DateTime.Now:HH:mm:ss}] {Describe(f.Display)}";
            Console.Write("\r" + line.PadRight(Math.Max(line.Length, 110)));
            if (f.Display.ShowQr && f.Display.QrUrl != lastQr)
            {
                lastQr = f.Display.QrUrl;
                Console.WriteLine("\n" + RenderQr(lastQr!) + lastQr);
            }
            if (!f.Display.ShowQr) lastQr = null;
            try { await Task.Delay(1000, cts.Token); } catch (OperationCanceledException) { }
        }
        await runtime.StopAsync();
        Console.WriteLine();
        return 0;
    }

    default:
        Console.WriteLine("""
            simlock-cli — prueba el backend de SimLock sin instalar el overlay

              pair <código> [--url URL] [--name NOMBRE]   vincula este equipo a una bay
              watch                                      muestra en vivo lo que vería el overlay
              state                                      un pedido a /state, interpretado
              unpair                                     borra el token local

            Opciones: --data <carpeta> (por defecto ./.simlock-cli)
            """);
        return command == "help" ? 0 : 2;
}

static string Describe(DisplayModel m)
{
    var sb = new StringBuilder(m.State.ToWire());
    if (m.Offline) sb.Append(" [SIN CONEXIÓN]");
    if (m.Remaining is TimeSpan r) sb.Append($" quedan {VenueTime.FormatCountdown(r)}");
    if (m.ActiveWarningMinutes is int w) sb.Append($" (aviso {w} min)");
    if (m.State == AgentState.Upcoming && m.StartsAt is { } st) sb.Append($" empieza {VenueTime.FormatHour(st, m.VenueTimezone)}");
    if (m.CustomerFirstName is { } n) sb.Append($" — {n}");
    if (m.ShowQr) sb.Append(" [QR]");
    if (m.PendingPaymentCents > 0) sb.Append($" [pago pendiente {m.PendingPaymentCents / 100m:0.##} {m.Currency}]");
    if (m.MessageText is { } msg) sb.Append($" «{msg}»");
    return sb.ToString();
}

static string RenderQr(string text)
{
    var q = QrCode.EncodeText(text);
    var sb = new StringBuilder();
    // Medio bloque por módulo vertical: dos filas por línea de texto, con zona silenciosa de 2.
    for (var y = -2; y < q.Size + 2; y += 2)
    {
        for (var x = -2; x < q.Size + 2; x++)
        {
            bool top = q[x, y], bottom = q[x, y + 1];
            sb.Append((top, bottom) switch { (true, true) => ' ', (true, false) => '▄', (false, true) => '▀', _ => '█' });
        }
        sb.Append('\n');
    }
    return sb.ToString();
}

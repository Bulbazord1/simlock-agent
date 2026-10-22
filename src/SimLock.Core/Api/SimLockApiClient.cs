using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace SimLock.Core.Api;

/// <summary>El servidor respondió 401: código inválido (pair) o equipo revocado / token inválido (resto).</summary>
public sealed class UnauthorizedException(string message) : Exception(message);

/// <summary>Cualquier otra falla de red o respuesta inesperada. Se trata como "sin conexión".</summary>
public sealed class ApiException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Resultado de /state con lo necesario para sincronizar el reloj.</summary>
public sealed record StateFetch(StateResponse State, long ReceivedAtTimestamp, TimeSpan RoundTrip);

public interface ISimLockApi
{
    Task<PairResponse> PairAsync(string code, string deviceName, string appVersion, CancellationToken ct);
    Task<StateFetch> GetStateAsync(string deviceToken, CancellationToken ct);
    Task HeartbeatAsync(string deviceToken, string appVersion, CancellationToken ct);
    Task PostEventsAsync(string deviceToken, IReadOnlyList<AgentEvent> events, CancellationToken ct);
}

public sealed class SimLockApiClient : ISimLockApi, IDisposable
{
    public const int MaxEventsPerRequest = 50;

    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    public SimLockApiClient(Uri baseUrl, string userAgent, HttpMessageHandler? handler = null)
    {
        _ownsClient = true;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.BaseAddress = baseUrl;
        _http.Timeout = TimeSpan.FromSeconds(10);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<PairResponse> PairAsync(string code, string deviceName, string appVersion, CancellationToken ct)
    {
        var body = new PairRequest { Code = code.Trim(), DeviceName = deviceName, AppVersion = appVersion };
        using var req = new HttpRequestMessage(HttpMethod.Post, "api/public/simlock/pair")
        {
            Content = JsonBody(body),
        };
        using var res = await SendAsync(req, ct).ConfigureAwait(false);
        var pair = await ReadJsonAsync<PairResponse>(res, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(pair.DeviceToken))
            throw new ApiException("La vinculación no devolvió deviceToken.");
        return pair;
    }

    public async Task<StateFetch> GetStateAsync(string deviceToken, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "api/public/simlock/state");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", deviceToken);
        var sw = Stopwatch.StartNew();
        using var res = await SendAsync(req, ct).ConfigureAwait(false);
        var state = await ReadJsonAsync<StateResponse>(res, ct).ConfigureAwait(false);
        // Se toma el instante después de leer el cuerpo: serverTime se generó antes de enviarlo.
        var received = Stopwatch.GetTimestamp();
        sw.Stop();
        if (state.ServerTime == default)
            throw new ApiException("La respuesta de /state no trae serverTime.");
        return new StateFetch(state, received, sw.Elapsed);
    }

    public async Task HeartbeatAsync(string deviceToken, string appVersion, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "api/public/simlock/heartbeat")
        {
            Content = JsonBody(new HeartbeatRequest { AppVersion = appVersion }),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", deviceToken);
        using var _ = await SendAsync(req, ct).ConfigureAwait(false);
    }

    public async Task PostEventsAsync(string deviceToken, IReadOnlyList<AgentEvent> events, CancellationToken ct)
    {
        if (events.Count == 0) return;
        if (events.Count > MaxEventsPerRequest)
            throw new ArgumentException($"Máximo {MaxEventsPerRequest} eventos por pedido.", nameof(events));
        using var req = new HttpRequestMessage(HttpMethod.Post, "api/public/simlock/events")
        {
            Content = JsonBody(new EventsRequest { Events = events }),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", deviceToken);
        using var _ = await SendAsync(req, ct).ConfigureAwait(false);
    }

    /// <summary>JSON con Content-Length fijo (sin "chunked"), que acepta cualquier servidor o proxy.</summary>
    private static StringContent JsonBody<T>(T value) =>
        new(JsonSerializer.Serialize(value, Json.Options), Encoding.UTF8, "application/json");

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        HttpResponseMessage res;
        try
        {
            res = await _http.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            throw new ApiException($"Sin conexión con Fore Booking ({ex.GetType().Name}: {ex.Message})", ex);
        }

        if (res.StatusCode == HttpStatusCode.Unauthorized)
        {
            var detail = await SafeReadAsync(res, ct).ConfigureAwait(false);
            res.Dispose();
            throw new UnauthorizedException(string.IsNullOrEmpty(detail) ? "401" : detail);
        }
        if (!res.IsSuccessStatusCode)
        {
            var detail = await SafeReadAsync(res, ct).ConfigureAwait(false);
            var code = (int)res.StatusCode;
            res.Dispose();
            throw new ApiException($"HTTP {code}: {Truncate(detail, 300)}");
        }
        return res;
    }

    private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage res, CancellationToken ct)
    {
        try
        {
            var value = await res.Content.ReadFromJsonAsync<T>(Json.Options, ct).ConfigureAwait(false);
            return value ?? throw new ApiException("Respuesta vacía.");
        }
        catch (JsonException ex)
        {
            throw new ApiException($"Respuesta JSON inválida: {ex.Message}", ex);
        }
    }

    private static async Task<string> SafeReadAsync(HttpResponseMessage res, CancellationToken ct)
    {
        try { return await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false); }
        catch { return ""; }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    public void Dispose()
    {
        if (_ownsClient) _http.Dispose();
    }
}

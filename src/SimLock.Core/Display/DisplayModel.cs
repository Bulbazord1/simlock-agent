using SimLock.Core.Api;

namespace SimLock.Core.Display;

public enum AgentState
{
    /// <summary>SimLock apagado para la org o la bay: overlay oculto.</summary>
    Disabled,
    /// <summary>Sin reserva: pantalla de espera con branding.</summary>
    Idle,
    /// <summary>Turno por empezar (30 min antes): hora, nombre y QR de check-in.</summary>
    Upcoming,
    /// <summary>Turno empezado sin check-in (solo si la org lo exige). Sin cuenta regresiva.</summary>
    AwaitingCheckin,
    /// <summary>Jugando: reloj discreto, no tapar el juego.</summary>
    InProgress,
    /// <summary>Quedan pocos minutos: aviso visible + QR de extensión si se puede.</summary>
    LastMinutes,
    /// <summary>Turno terminado: pantalla completa + QR de extensión si se puede.</summary>
    Ended,
    /// <summary>Todavía no hay estado del servidor (arranque).</summary>
    Unknown,
}

public static class AgentStates
{
    public static AgentState Parse(string? s) => s switch
    {
        "disabled" => AgentState.Disabled,
        "idle" => AgentState.Idle,
        "upcoming" => AgentState.Upcoming,
        "awaiting_checkin" => AgentState.AwaitingCheckin,
        "in_progress" => AgentState.InProgress,
        "last_minutes" => AgentState.LastMinutes,
        "ended" => AgentState.Ended,
        _ => AgentState.Unknown,
    };

    public static string ToWire(this AgentState s) => s switch
    {
        AgentState.Disabled => "disabled",
        AgentState.Idle => "idle",
        AgentState.Upcoming => "upcoming",
        AgentState.AwaitingCheckin => "awaiting_checkin",
        AgentState.InProgress => "in_progress",
        AgentState.LastMinutes => "last_minutes",
        AgentState.Ended => "ended",
        _ => "unknown",
    };

    /// <summary>Estados que tapan toda la pantalla (no transparentes a clics).</summary>
    public static bool IsFullScreen(this AgentState s) =>
        s is AgentState.Idle or AgentState.Upcoming or AgentState.AwaitingCheckin or AgentState.Ended;
}

/// <summary>Todo lo que la interfaz necesita para dibujar un cuadro. Inmutable.</summary>
public sealed record DisplayModel
{
    public required AgentState State { get; init; }
    public required DateTimeOffset Now { get; init; }
    public string? BookingId { get; init; }
    public string? CustomerFirstName { get; init; }
    public DateTimeOffset? StartsAt { get; init; }
    public DateTimeOffset? EndsAt { get; init; }
    /// <summary>Tiempo que falta para endsAt. Null cuando no corresponde mostrar cuenta regresiva.</summary>
    public TimeSpan? Remaining { get; init; }
    public int? ActiveWarningMinutes { get; init; }
    public string? QrUrl { get; init; }
    public bool ShowQr { get; init; }
    /// <summary>
    /// Pantalla de reposo: oscura, con poco contenido que se mueve de lugar (evita marcas en la pantalla).
    /// Se usa sin reserva, y también cuando el turno terminó hace más de unos minutos.
    /// </summary>
    public bool Resting { get; init; }
    public bool CanExtend { get; init; }
    public string? MessageText { get; init; }
    public long PendingPaymentCents { get; init; }
    public string? Currency { get; init; }
    public bool Offline { get; init; }
    public string? BayName { get; init; }
    public string? VenueName { get; init; }
    public string? VenueTimezone { get; init; }
    public BrandingInfo? Branding { get; init; }

    public static DisplayModel Starting(DateTimeOffset now) => new() { State = AgentState.Unknown, Now = now };
}

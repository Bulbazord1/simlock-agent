using System.Text.Json;
using System.Text.Json.Serialization;

namespace SimLock.Core.Api;

// Modelos del contrato "SimLock Agent — contrato de integración" (simlock-agent-api.md).
// Todos los campos que el servidor puede omitir son anulables: el agente no debe romperse
// si Fore Booking agrega o deja de mandar algo.

public sealed class PairRequest
{
    public required string Code { get; init; }
    public required string DeviceName { get; init; }
    public required string AppVersion { get; init; }
}

public sealed class PairResponse
{
    public string DeviceToken { get; init; } = "";
    public NamedRef? Bay { get; init; }
    public VenueRef? Venue { get; init; }
}

public sealed class NamedRef
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
}

public sealed class VenueRef
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Timezone { get; init; }
    public string? Locale { get; init; }
}

public sealed class StateResponse
{
    public DateTimeOffset ServerTime { get; init; }
    public int PollSeconds { get; init; }
    public NamedRef? Bay { get; init; }
    public VenueRef? Venue { get; init; }
    public string State { get; init; } = "";
    public int? RemainingSeconds { get; init; }
    public int? ActiveWarningMinutes { get; init; }
    public int[]? WarningMinutes { get; init; }
    public bool RequireCheckin { get; init; }
    public BookingInfo? Booking { get; init; }
    public string? QrUrl { get; init; }
    /// <summary>
    /// Página del panel que dibuja las pantallas completas (reposo, por empezar, check-in, terminó).
    /// Si falta o no carga, el agente usa sus pantallas propias.
    /// </summary>
    public string? ScreenUrl { get; init; }
    /// <summary>"compact" mientras se juega (in_progress / last_minutes), "full" en el resto.</summary>
    public string? DisplayMode { get; init; }
    public bool CanExtend { get; init; }
    public MessageInfo? Message { get; init; }
    public BrandingInfo? Branding { get; init; }
}

public sealed class BookingInfo
{
    public string Id { get; init; } = "";
    public DateTimeOffset StartsAt { get; init; }
    public DateTimeOffset EndsAt { get; init; }
    public string? CustomerFirstName { get; init; }
    public bool CheckedIn { get; init; }
    public long PendingPaymentCents { get; init; }
    public string? Currency { get; init; }
}

public sealed class MessageInfo
{
    public string Text { get; init; } = "";
    public DateTimeOffset? ExpiresAt { get; init; }
}

public sealed class BrandingInfo
{
    public string? LogoUrl { get; init; }
    public string? PrimaryColor { get; init; }
}

public sealed class HeartbeatRequest
{
    public required string AppVersion { get; init; }
}

public sealed class AgentEvent
{
    public required string Kind { get; init; }
    public string? BookingId { get; init; }
    public required DateTimeOffset At { get; init; }
    public string? Detail { get; init; }
}

public sealed class EventsRequest
{
    public required IReadOnlyList<AgentEvent> Events { get; init; }
}

/// <summary>Tipos de evento permitidos por el contrato (sección 4).</summary>
public static class EventKinds
{
    public const string Started = "started";
    public const string WarningShown = "warning_shown";
    public const string EndedShown = "ended_shown";
    public const string QrShown = "qr_shown";
    public const string Offline = "offline";
    public const string Online = "online";
    public const string Error = "error";
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

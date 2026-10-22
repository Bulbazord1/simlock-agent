using SimLock.Core.Api;

namespace SimLock.Core.Display;

/// <summary>
/// Convierte el último estado del servidor + la hora actual (del servidor) en lo que hay que mostrar.
///
/// Entre un poll y el siguiente (o sin conexión) el agente avanza el estado solo con los horarios
/// que ya conoce: pasa a "últimos minutos" al cruzar un umbral y a "terminado" en endsAt.
/// Nunca retrocede un estado del servidor, nunca alarga un turno y nunca da por terminado
/// un turno antes del endsAt conocido.
/// </summary>
public static class StateProjector
{
    /// <summary>El contrato mantiene "ended" 15 minutos después del fin.</summary>
    public static readonly TimeSpan EndedHold = TimeSpan.FromMinutes(15);

    /// <summary>Cuánto se muestra "Tu turno terminó" a pantalla completa antes de pasar a reposo.</summary>
    public static readonly TimeSpan EndedVisible = TimeSpan.FromMinutes(3);

    public static DisplayModel Project(StateResponse s, DateTimeOffset now, bool offline)
    {
        var serverState = AgentStates.Parse(s.State);
        var booking = s.Booking;

        var model = new DisplayModel
        {
            State = serverState,
            Now = now,
            Offline = offline,
            BayName = s.Bay?.Name,
            VenueName = s.Venue?.Name,
            VenueTimezone = s.Venue?.Timezone,
            Branding = s.Branding,
            CanExtend = s.CanExtend,
            QrUrl = string.IsNullOrWhiteSpace(s.QrUrl) ? null : s.QrUrl,
            MessageText = s.Message is { } m && !string.IsNullOrWhiteSpace(m.Text) && (m.ExpiresAt is null || now < m.ExpiresAt)
                ? m.Text
                : null,
        };

        if (serverState == AgentState.Disabled)
            return model with { MessageText = null, QrUrl = null };

        // Estados sin reserva (o estado desconocido): se muestran tal cual.
        if (booking is null || serverState is AgentState.Idle or AgentState.Unknown)
            return Finish(model with { State = serverState == AgentState.Unknown ? AgentState.Idle : serverState });

        var state = Advance(serverState, booking, s.RequireCheckin, now, s.WarningMinutes);
        var remaining = booking.EndsAt - now;
        if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;

        int? activeWarning = state == AgentState.LastMinutes
            ? ActiveWarning(remaining, s.WarningMinutes) ?? s.ActiveWarningMinutes
            : null;

        // Pasados los 15 min de "ended" sin noticias del servidor, volver a la espera.
        if (state == AgentState.Ended && now >= booking.EndsAt + EndedHold)
            return Finish(model with { State = AgentState.Idle });

        return Finish(model with
        {
            State = state,
            BookingId = booking.Id,
            CustomerFirstName = booking.CustomerFirstName,
            StartsAt = booking.StartsAt,
            EndsAt = booking.EndsAt,
            Remaining = state is AgentState.InProgress or AgentState.LastMinutes ? remaining : null,
            ActiveWarningMinutes = activeWarning,
            PendingPaymentCents = booking.PendingPaymentCents,
            Currency = booking.Currency,
        });
    }

    private static AgentState Advance(AgentState server, BookingInfo b, bool requireCheckin, DateTimeOffset now, int[]? warnings)
    {
        var state = server;

        // Turno por empezar → empezó.
        if (state == AgentState.Upcoming && now >= b.StartsAt)
            state = requireCheckin && !b.CheckedIn ? AgentState.AwaitingCheckin : AgentState.InProgress;

        // Llegó el fin conocido. (Desde awaiting_checkin también: el turno terminó igual.)
        if ((state is AgentState.Upcoming or AgentState.AwaitingCheckin or AgentState.InProgress or AgentState.LastMinutes)
            && now >= b.EndsAt)
            return AgentState.Ended;

        // Cruzó un umbral de aviso.
        if (state == AgentState.InProgress && ActiveWarning(b.EndsAt - now, warnings) is not null)
            state = AgentState.LastMinutes;

        return state;
    }

    /// <summary>El umbral más chico ya cruzado (ej: quedan 9 min con avisos [15,10,5] → 10).</summary>
    public static int? ActiveWarning(TimeSpan remaining, int[]? warningMinutes)
    {
        if (warningMinutes is null || warningMinutes.Length == 0) return null;
        int? best = null;
        foreach (var w in warningMinutes)
        {
            if (w <= 0) continue;
            if (remaining <= TimeSpan.FromMinutes(w) && (best is null || w < best)) best = w;
        }
        return best;
    }

    private static DisplayModel Finish(DisplayModel m)
    {
        var showQr = m.QrUrl is not null && m.State switch
        {
            // En reposo sin reserva el servidor puede mandar el link para reservar desde el celular.
            AgentState.Idle or AgentState.Upcoming or AgentState.AwaitingCheckin => true,
            AgentState.LastMinutes or AgentState.Ended => m.CanExtend,
            _ => false,
        };
        var resting = m.State == AgentState.Idle
                      || (m.State == AgentState.Ended && m.EndsAt is { } end && m.Now >= end + EndedVisible);
        return m with { ShowQr = showQr, Resting = resting };
    }
}

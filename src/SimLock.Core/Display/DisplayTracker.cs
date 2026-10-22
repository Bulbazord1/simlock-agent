using System.Globalization;
using SimLock.Core.Api;
using SimLock.Core.Storage;

namespace SimLock.Core.Display;

/// <summary>Algo que la interfaz tiene que hacer una sola vez (no en cada cuadro).</summary>
public sealed record Announcement(int WarningMinutes, string? BookingId);

/// <summary>
/// Mira los cuadros sucesivos y decide qué cosas ocurren una sola vez: mostrar un aviso de
/// minutos, y los eventos de auditoría started / warning_shown / ended_shown / qr_shown.
/// Lo ya ocurrido se guarda en disco para no repetirlo si el agente se reinicia.
/// </summary>
public sealed class DisplayTracker(ISeenStore seen)
{
    public (Announcement? Announcement, IReadOnlyList<AgentEvent> Events) Observe(DisplayModel m)
    {
        var events = new List<AgentEvent>();
        Announcement? announcement = null;
        var id = m.BookingId;
        if (id is null) return (null, events);

        if ((m.State is AgentState.InProgress or AgentState.LastMinutes) && seen.Add($"started:{id}"))
            events.Add(Event(EventKinds.Started, id, m.Now));

        if (m.State == AgentState.LastMinutes && m.ActiveWarningMinutes is int w)
        {
            // Clave con endsAt: si el turno se extiende, los avisos vuelven a salir para el nuevo fin.
            var end = m.EndsAt?.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) ?? "";
            if (seen.Add($"warn:{id}:{end}:{w}"))
            {
                announcement = new Announcement(w, id);
                events.Add(Event(EventKinds.WarningShown, id, m.Now, w.ToString(CultureInfo.InvariantCulture)));
            }
        }

        if (m.State == AgentState.Ended && seen.Add($"ended:{id}:{m.EndsAt?.ToUnixTimeSeconds()}"))
            events.Add(Event(EventKinds.EndedShown, id, m.Now));

        if (m.ShowQr && seen.Add($"qr:{id}:{m.State.ToWire()}"))
            events.Add(Event(EventKinds.QrShown, id, m.Now, m.State.ToWire()));

        return (announcement, events);
    }

    private static AgentEvent Event(string kind, string? bookingId, DateTimeOffset at, string? detail = null) =>
        new() { Kind = kind, BookingId = bookingId, At = at, Detail = detail };
}

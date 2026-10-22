using System.Diagnostics;

namespace SimLock.Core.Time;

/// <summary>Reloj monotónico (no salta si alguien cambia la hora de Windows).</summary>
public interface IMonotonicClock
{
    long GetTimestamp();
    TimeSpan GetElapsed(long fromTimestamp);
}

public sealed class StopwatchClock : IMonotonicClock
{
    public static readonly StopwatchClock Instance = new();
    public long GetTimestamp() => Stopwatch.GetTimestamp();
    public TimeSpan GetElapsed(long fromTimestamp) => Stopwatch.GetElapsedTime(fromTimestamp);
}

/// <summary>
/// Hora del servidor estimada localmente: serverTime de la última respuesta + tiempo monotónico
/// transcurrido desde que llegó. El contrato pide no usar el reloj de Windows para la cuenta regresiva.
/// </summary>
public sealed class ServerClock(IMonotonicClock monotonic)
{
    private readonly object _gate = new();
    private DateTimeOffset _anchorServerTime;
    private long _anchorTimestamp;
    private bool _synced;

    public bool IsSynced { get { lock (_gate) return _synced; } }

    /// <param name="serverTime">serverTime de la respuesta.</param>
    /// <param name="receivedAtTimestamp">Timestamp monotónico de cuando llegó la respuesta.</param>
    /// <param name="roundTrip">Duración del pedido. Se asume que el servidor generó la hora a mitad de camino.</param>
    public void Sync(DateTimeOffset serverTime, long receivedAtTimestamp, TimeSpan roundTrip)
    {
        var halfTrip = roundTrip > TimeSpan.Zero && roundTrip < TimeSpan.FromSeconds(10)
            ? TimeSpan.FromTicks(roundTrip.Ticks / 2)
            : TimeSpan.Zero;
        lock (_gate)
        {
            _anchorServerTime = serverTime + halfTrip;
            _anchorTimestamp = receivedAtTimestamp;
            _synced = true;
        }
    }

    public DateTimeOffset Now
    {
        get
        {
            lock (_gate)
            {
                if (!_synced) throw new InvalidOperationException("El reloj todavía no se sincronizó con el servidor.");
                return _anchorServerTime + monotonic.GetElapsed(_anchorTimestamp);
            }
        }
    }
}

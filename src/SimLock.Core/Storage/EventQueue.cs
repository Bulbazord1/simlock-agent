using SimLock.Core.Api;

namespace SimLock.Core.Storage;

/// <summary>
/// Cola de eventos de auditoría que sobrevive a cortes de red y reinicios.
/// Se envía en lotes de hasta 50 (límite del contrato); si la cola crece demasiado se descartan los más viejos.
/// </summary>
public sealed class EventQueue
{
    public const int MaxQueued = 2000;

    private readonly string? _path;
    private readonly List<AgentEvent> _items;
    private readonly object _gate = new();

    public EventQueue(string? path)
    {
        _path = path;
        _items = path is null ? [] : JsonFile.Read<List<AgentEvent>>(path) ?? [];
    }

    public int Count { get { lock (_gate) return _items.Count; } }

    public void Enqueue(AgentEvent e) => EnqueueRange([e]);

    public void EnqueueRange(IEnumerable<AgentEvent> events)
    {
        lock (_gate)
        {
            var before = _items.Count;
            _items.AddRange(events);
            if (_items.Count == before) return;
            if (_items.Count > MaxQueued) _items.RemoveRange(0, _items.Count - MaxQueued);
            Save();
        }
    }

    /// <summary>Copia del primer lote, sin sacarlo. Se saca con <see cref="Acknowledge"/> cuando el servidor lo aceptó.</summary>
    public IReadOnlyList<AgentEvent> PeekBatch(int max = SimLockApiClient.MaxEventsPerRequest)
    {
        lock (_gate) return _items.Take(max).ToList();
    }

    public void Acknowledge(IReadOnlyList<AgentEvent> sent)
    {
        lock (_gate)
        {
            // Saca exactamente los que se enviaron (por referencia), aunque mientras tanto se hayan agregado otros.
            var sentSet = new HashSet<AgentEvent>(sent, ReferenceEqualityComparer.Instance);
            if (_items.RemoveAll(sentSet.Contains) > 0) Save();
        }
    }

    private void Save()
    {
        if (_path is null) return;
        try { JsonFile.Write(_path, _items); }
        catch (IOException) { /* best-effort */ }
    }
}

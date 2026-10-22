namespace SimLock.Core.Storage;

public interface ISeenStore
{
    /// <summary>Agrega la clave. Devuelve true si no estaba (o sea, "es la primera vez").</summary>
    bool Add(string key);
}

/// <summary>Conjunto acotado de claves "ya pasó" guardado en disco. Conserva las últimas <see cref="Capacity"/>.</summary>
public sealed class SeenStore : ISeenStore
{
    public const int Capacity = 500;

    private readonly string? _path;
    private readonly LinkedList<string> _order = new();
    private readonly HashSet<string> _set = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <param name="path">Archivo donde persistir. Null = solo en memoria.</param>
    public SeenStore(string? path)
    {
        _path = path;
        if (path is null) return;
        var saved = JsonFile.Read<List<string>>(path);
        if (saved is null) return;
        foreach (var k in saved.TakeLast(Capacity))
            if (_set.Add(k)) _order.AddLast(k);
    }

    public bool Add(string key)
    {
        lock (_gate)
        {
            if (!_set.Add(key)) return false;
            _order.AddLast(key);
            while (_order.Count > Capacity)
            {
                _set.Remove(_order.First!.Value);
                _order.RemoveFirst();
            }
            if (_path is not null)
            {
                try { JsonFile.Write(_path, _order.ToList()); }
                catch (IOException) { /* best-effort: en el peor caso un aviso se repite tras reiniciar */ }
            }
            return true;
        }
    }
}

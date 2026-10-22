using SimLock.Core.Qr;
using SimLock.Core.Tests;

// Uso:
//   SimLock.Core.Tests                 → corre todas las pruebas
//   SimLock.Core.Tests <filtro>        → corre las que contengan el filtro
//   SimLock.Core.Tests qr-dump <dir>   → escribe matrices QR de prueba (las verifica tools/verify_qr.py)
if (args.Length >= 2 && args[0] == "qr-dump")
{
    var dir = args[1];
    Directory.CreateDirectory(dir);
    var samples = new List<(string Name, string Text, QrEcc Ecc)>
    {
        ("url-real", "https://panel.forebooking.com/x/Zr4Lm0QxK2vB9sT1yPq8wE3nH6cJ5dA7fG0uI2oR", QrEcc.Medium),
        ("acentos", "Extendé tu turno — ñandú ✓", QrEcc.Medium),
        ("uno", "a", QrEcc.Low),
    };
    var rnd = new Random(42);
    const string alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_/:.?=&";
    foreach (var len in new[] { 10, 17, 30, 50, 78, 100, 140, 200, 254, 255, 300, 400, 600, 900, 1200, 1800, 2300 })
        foreach (var ecc in Enum.GetValues<QrEcc>())
        {
            var text = new string(Enumerable.Range(0, len).Select(_ => alphabet[rnd.Next(alphabet.Length)]).ToArray());
            samples.Add(($"len{len}-{ecc}", text, ecc));
        }

    var written = 0;
    foreach (var (name, text, ecc) in samples)
    {
        QrCode q;
        try { q = QrCode.EncodeText(text, ecc); }
        catch (ArgumentException) { continue; } // no entra en ese nivel de corrección
        var lines = Enumerable.Range(0, q.Size).Select(y => new string(Enumerable.Range(0, q.Size).Select(x => q[x, y] ? '1' : '0').ToArray()));
        File.WriteAllText(Path.Combine(dir, name + ".txt"), string.Join('\n', lines));
        File.WriteAllText(Path.Combine(dir, name + ".expected"), text);
        File.WriteAllText(Path.Combine(dir, name + ".meta"), $"v{q.Version} {ecc} mask{q.Mask}");
        written++;
    }
    Console.WriteLine($"{written} códigos escritos en {dir}");
    return 0;
}

return await Runner.RunAll(args.Length > 0 ? args[0] : null);

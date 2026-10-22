using System.Text;

namespace SimLock.Core.Qr;

public enum QrEcc { Low = 0, Medium = 1, Quartile = 2, High = 3 }

/// <summary>
/// Generador de códigos QR (modo byte, versiones 1–40) sin dependencias externas.
/// Sigue ISO/IEC 18004 y el algoritmo de referencia de Project Nayuki (MIT).
/// El agente solo dibuja el qrUrl que manda el servidor, así que con modo byte alcanza.
/// </summary>
public sealed class QrCode
{
    public int Version { get; }
    public int Size { get; }
    public QrEcc Ecc { get; }
    public int Mask { get; private set; }

    private readonly bool[,] _modules;    // [y, x] true = oscuro
    private readonly bool[,] _isFunction; // [y, x]

    public bool this[int x, int y] => x >= 0 && x < Size && y >= 0 && y < Size && _modules[y, x];

    public static QrCode EncodeText(string text, QrEcc ecc = QrEcc.Medium) =>
        EncodeBytes(Encoding.UTF8.GetBytes(text), ecc);

    public static QrCode EncodeBytes(byte[] data, QrEcc ecc = QrEcc.Medium)
    {
        // Elegir la versión más chica donde entran los datos.
        int version, dataUsedBits;
        for (version = 1; ; version++)
        {
            var capacityBits = NumDataCodewords(version, ecc) * 8;
            dataUsedBits = 4 + CharCountBits(version) + data.Length * 8;
            if (data.Length < (1 << CharCountBits(version)) && dataUsedBits <= capacityBits) break;
            if (version >= 40) throw new ArgumentException("Texto demasiado largo para un código QR.", nameof(data));
        }

        // Segmento en modo byte.
        var bb = new BitBuffer();
        bb.Append(0b0100, 4);
        bb.Append(data.Length, CharCountBits(version));
        foreach (var b in data) bb.Append(b, 8);

        // Terminador y relleno.
        var capacity = NumDataCodewords(version, ecc) * 8;
        bb.Append(0, Math.Min(4, capacity - bb.Count));
        bb.Append(0, (8 - bb.Count % 8) % 8);
        for (var pad = 0xEC; bb.Count < capacity; pad ^= 0xEC ^ 0x11) bb.Append(pad, 8);

        var codewords = new byte[bb.Count / 8];
        for (var i = 0; i < bb.Count; i++) codewords[i >> 3] |= (byte)(bb[i] << (7 - (i & 7)));

        return new QrCode(version, ecc, codewords);
    }

    private QrCode(int version, QrEcc ecc, byte[] dataCodewords)
    {
        Version = version;
        Ecc = ecc;
        Size = version * 4 + 17;
        _modules = new bool[Size, Size];
        _isFunction = new bool[Size, Size];

        DrawFunctionPatterns();
        var all = AddEccAndInterleave(dataCodewords);
        DrawCodewords(all);

        // Elegir la máscara con menor penalidad.
        var best = 0;
        var minPenalty = int.MaxValue;
        for (var m = 0; m < 8; m++)
        {
            ApplyMask(m);
            DrawFormatBits(m);
            var p = Penalty();
            if (p < minPenalty) { best = m; minPenalty = p; }
            ApplyMask(m); // deshacer (XOR)
        }
        Mask = best;
        ApplyMask(best);
        DrawFormatBits(best);
    }

    // ───────────── patrones fijos ─────────────

    private void DrawFunctionPatterns()
    {
        for (var i = 0; i < Size; i++)
        {
            SetFunction(6, i, i % 2 == 0);
            SetFunction(i, 6, i % 2 == 0);
        }
        DrawFinder(3, 3);
        DrawFinder(Size - 4, 3);
        DrawFinder(3, Size - 4);

        var align = AlignmentPositions();
        var n = align.Length;
        for (var i = 0; i < n; i++)
            for (var j = 0; j < n; j++)
                if (!(i == 0 && j == 0 || i == 0 && j == n - 1 || i == n - 1 && j == 0))
                    DrawAlignment(align[i], align[j]);

        DrawFormatBits(0); // reserva el área; se reescribe al final
        DrawVersion();
    }

    private void DrawFinder(int x, int y)
    {
        for (var dy = -4; dy <= 4; dy++)
            for (var dx = -4; dx <= 4; dx++)
            {
                var dist = Math.Max(Math.Abs(dx), Math.Abs(dy));
                int xx = x + dx, yy = y + dy;
                if (xx >= 0 && xx < Size && yy >= 0 && yy < Size)
                    SetFunction(xx, yy, dist != 2 && dist != 4);
            }
    }

    private void DrawAlignment(int x, int y)
    {
        for (var dy = -2; dy <= 2; dy++)
            for (var dx = -2; dx <= 2; dx++)
                SetFunction(x + dx, y + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
    }

    private int[] AlignmentPositions()
    {
        if (Version == 1) return [];
        var num = Version / 7 + 2;
        var step = (Version * 8 + num * 3 + 5) / (num * 4 - 4) * 2;
        var result = new int[num];
        result[0] = 6;
        for (int i = num - 1, pos = Size - 7; i >= 1; i--, pos -= step) result[i] = pos;
        return result;
    }

    private void DrawFormatBits(int mask)
    {
        var eccBits = Ecc switch { QrEcc.Low => 1, QrEcc.Medium => 0, QrEcc.Quartile => 3, _ => 2 };
        var data = eccBits << 3 | mask;
        var rem = data;
        for (var i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >> 9) * 0x537);
        var bits = (data << 10 | rem) ^ 0x5412;

        for (var i = 0; i <= 5; i++) SetFunction(8, i, Bit(bits, i));
        SetFunction(8, 7, Bit(bits, 6));
        SetFunction(8, 8, Bit(bits, 7));
        SetFunction(7, 8, Bit(bits, 8));
        for (var i = 9; i < 15; i++) SetFunction(14 - i, 8, Bit(bits, i));

        for (var i = 0; i < 8; i++) SetFunction(Size - 1 - i, 8, Bit(bits, i));
        for (var i = 8; i < 15; i++) SetFunction(8, Size - 15 + i, Bit(bits, i));
        SetFunction(8, Size - 8, true); // módulo oscuro fijo
    }

    private void DrawVersion()
    {
        if (Version < 7) return;
        var rem = Version;
        for (var i = 0; i < 12; i++) rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
        var bits = Version << 12 | rem;
        for (var i = 0; i < 18; i++)
        {
            var bit = Bit(bits, i);
            int a = Size - 11 + i % 3, b = i / 3;
            SetFunction(a, b, bit);
            SetFunction(b, a, bit);
        }
    }

    private void SetFunction(int x, int y, bool dark)
    {
        _modules[y, x] = dark;
        _isFunction[y, x] = true;
    }

    // ───────────── datos ─────────────

    private byte[] AddEccAndInterleave(byte[] data)
    {
        var numBlocks = NumEccBlocks[(int)Ecc][Version];
        var blockEccLen = EccCodewordsPerBlock[(int)Ecc][Version];
        var rawCodewords = NumRawDataModules(Version) / 8;
        var numShortBlocks = numBlocks - rawCodewords % numBlocks;
        var shortBlockLen = rawCodewords / numBlocks;

        var divisor = ReedSolomonDivisor(blockEccLen);
        var blocks = new byte[numBlocks][];
        for (int i = 0, k = 0; i < numBlocks; i++)
        {
            var datLen = shortBlockLen - blockEccLen + (i < numShortBlocks ? 0 : 1);
            var dat = data.AsSpan(k, datLen).ToArray();
            k += datLen;
            var ecc = ReedSolomonRemainder(dat, divisor);
            var block = new byte[shortBlockLen + 1];
            dat.CopyTo(block, 0);
            // Los bloques cortos tienen un hueco (relleno) antes del ECC.
            ecc.CopyTo(block, shortBlockLen + 1 - blockEccLen);
            blocks[i] = block;
        }

        var result = new byte[rawCodewords];
        var idx = 0;
        for (var i = 0; i < blocks[0].Length; i++)
            for (var j = 0; j < blocks.Length; j++)
                if (i != shortBlockLen - blockEccLen || j >= numShortBlocks)
                    result[idx++] = blocks[j][i];
        return result;
    }

    private void DrawCodewords(byte[] data)
    {
        var i = 0;
        for (var right = Size - 1; right >= 1; right -= 2)
        {
            if (right == 6) right = 5;
            for (var vert = 0; vert < Size; vert++)
                for (var j = 0; j < 2; j++)
                {
                    var x = right - j;
                    var upward = ((right + 1) & 2) == 0;
                    var y = upward ? Size - 1 - vert : vert;
                    if (!_isFunction[y, x] && i < data.Length * 8)
                    {
                        _modules[y, x] = Bit(data[i >> 3], 7 - (i & 7));
                        i++;
                    }
                }
        }
    }

    private void ApplyMask(int mask)
    {
        for (var y = 0; y < Size; y++)
            for (var x = 0; x < Size; x++)
            {
                var invert = mask switch
                {
                    0 => (x + y) % 2 == 0,
                    1 => y % 2 == 0,
                    2 => x % 3 == 0,
                    3 => (x + y) % 3 == 0,
                    4 => (x / 3 + y / 2) % 2 == 0,
                    5 => x * y % 2 + x * y % 3 == 0,
                    6 => (x * y % 2 + x * y % 3) % 2 == 0,
                    _ => ((x + y) % 2 + x * y % 3) % 2 == 0,
                };
                if (invert && !_isFunction[y, x]) _modules[y, x] = !_modules[y, x];
            }
    }

    // ───────────── penalidad (para elegir máscara) ─────────────

    private int Penalty()
    {
        var result = 0;

        // N1: corridas de 5+ módulos iguales; N3: patrones parecidos a un finder.
        for (var y = 0; y < Size; y++) result += LinePenalty(i => _modules[y, i]);
        for (var x = 0; x < Size; x++) result += LinePenalty(i => _modules[i, x]);

        // N2: bloques 2×2 del mismo color.
        for (var y = 0; y < Size - 1; y++)
            for (var x = 0; x < Size - 1; x++)
            {
                var c = _modules[y, x];
                if (c == _modules[y, x + 1] && c == _modules[y + 1, x] && c == _modules[y + 1, x + 1]) result += 3;
            }

        // N4: balance de oscuros.
        var dark = 0;
        foreach (var m in _modules) if (m) dark++;
        var total = Size * Size;
        var k = (Math.Abs(dark * 20 - total * 10) + total - 1) / total - 1;
        result += k * 10;
        return result;
    }

    private int LinePenalty(Func<int, bool> get)
    {
        var penalty = 0;
        var runColor = false;
        var runLen = 0;
        for (var i = 0; i < Size; i++)
        {
            var c = get(i);
            if (i > 0 && c == runColor)
            {
                runLen++;
                if (runLen == 5) penalty += 3;
                else if (runLen > 5) penalty++;
            }
            else
            {
                runColor = c;
                runLen = 1;
            }
        }

        // 1:1:3:1:1 oscuro con 4 claros a un lado (fuera del borde cuenta como claro).
        bool At(int i) => i >= 0 && i < Size && get(i);
        for (var i = -4; i < Size; i++)
        {
            if (At(i) && !At(i + 1) && At(i + 2) && At(i + 3) && At(i + 4) && !At(i + 5) && At(i + 6))
            {
                var before = !At(i - 1) && !At(i - 2) && !At(i - 3) && !At(i - 4);
                var after = !At(i + 7) && !At(i + 8) && !At(i + 9) && !At(i + 10);
                if (before || after) penalty += 40;
            }
        }
        return penalty;
    }

    // ───────────── Reed-Solomon sobre GF(2^8) ─────────────

    private static byte[] ReedSolomonDivisor(int degree)
    {
        var result = new byte[degree];
        result[degree - 1] = 1;
        var root = 1;
        for (var i = 0; i < degree; i++)
        {
            for (var j = 0; j < degree; j++)
            {
                result[j] = Multiply(result[j], root);
                if (j + 1 < degree) result[j] ^= result[j + 1];
            }
            root = Multiply(root, 0x02);
        }
        return result;
    }

    private static byte[] ReedSolomonRemainder(byte[] data, byte[] divisor)
    {
        var result = new byte[divisor.Length];
        foreach (var b in data)
        {
            var factor = b ^ result[0];
            Array.Copy(result, 1, result, 0, result.Length - 1);
            result[^1] = 0;
            for (var i = 0; i < result.Length; i++) result[i] ^= Multiply(divisor[i], factor);
        }
        return result;
    }

    private static byte Multiply(int x, int y)
    {
        var z = 0;
        for (var i = 7; i >= 0; i--)
        {
            z = (z << 1) ^ ((z >> 7) * 0x11D);
            z ^= ((y >> i) & 1) * x;
        }
        return (byte)z;
    }

    // ───────────── tablas ─────────────

    private static int CharCountBits(int version) => version <= 9 ? 8 : 16;

    private static int NumRawDataModules(int ver)
    {
        var result = (16 * ver + 128) * ver + 64;
        if (ver >= 2)
        {
            var numAlign = ver / 7 + 2;
            result -= (25 * numAlign - 10) * numAlign - 55;
            if (ver >= 7) result -= 36;
        }
        return result;
    }

    private static int NumDataCodewords(int ver, QrEcc ecc) =>
        NumRawDataModules(ver) / 8 - EccCodewordsPerBlock[(int)ecc][ver] * NumEccBlocks[(int)ecc][ver];

    private static bool Bit(int x, int i) => ((x >> i) & 1) != 0;

    private static readonly int[][] EccCodewordsPerBlock =
    [
        [-1, 7, 10, 15, 20, 26, 18, 20, 24, 30, 18, 20, 24, 26, 30, 22, 24, 28, 30, 28, 28, 28, 28, 30, 30, 26, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30],
        [-1, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26, 30, 22, 22, 24, 24, 28, 28, 26, 26, 26, 26, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28],
        [-1, 13, 22, 18, 26, 18, 24, 18, 22, 20, 24, 28, 26, 24, 20, 30, 24, 28, 28, 26, 30, 28, 30, 30, 30, 30, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30],
        [-1, 17, 28, 22, 16, 22, 28, 26, 26, 24, 28, 24, 28, 22, 24, 24, 30, 28, 28, 26, 28, 30, 24, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30],
    ];

    private static readonly int[][] NumEccBlocks =
    [
        [-1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 4, 4, 4, 4, 4, 6, 6, 6, 6, 7, 8, 8, 9, 9, 10, 12, 12, 12, 13, 14, 15, 16, 17, 18, 19, 19, 20, 21, 22, 24, 25],
        [-1, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5, 5, 8, 9, 9, 10, 10, 11, 13, 14, 16, 17, 17, 18, 20, 21, 23, 25, 26, 28, 29, 31, 33, 35, 37, 38, 40, 43, 45, 47, 49],
        [-1, 1, 1, 2, 2, 4, 4, 6, 6, 8, 8, 8, 10, 12, 16, 12, 17, 16, 18, 21, 20, 23, 23, 25, 27, 29, 34, 34, 35, 38, 40, 43, 45, 48, 51, 53, 56, 59, 62, 65, 68],
        [-1, 1, 1, 2, 4, 4, 4, 5, 6, 8, 8, 11, 11, 16, 16, 18, 16, 19, 21, 25, 25, 25, 34, 30, 32, 35, 37, 40, 42, 45, 48, 51, 54, 57, 60, 63, 66, 70, 74, 77, 81],
    ];

    private sealed class BitBuffer
    {
        private readonly List<bool> _bits = [];
        public int Count => _bits.Count;
        public int this[int i] => _bits[i] ? 1 : 0;
        public void Append(int value, int len)
        {
            for (var i = len - 1; i >= 0; i--) _bits.Add(((value >> i) & 1) != 0);
        }
    }
}

"""Verifica el generador QR propio: dibuja cada matriz y la decodifica con zxing-cpp.

Uso:
    dotnet run --project tests/SimLock.Core.Tests -- qr-dump /tmp/qr
    python3 tools/verify_qr.py /tmp/qr

Requiere: pip install zxing-cpp pillow
"""
import pathlib
import sys

import zxingcpp
from PIL import Image

SCALE = 6
QUIET = 4


def render(matrix_text: str) -> Image.Image:
    rows = matrix_text.split("\n")
    n = len(rows)
    side = (n + 2 * QUIET) * SCALE
    img = Image.new("L", (side, side), 255)
    px = img.load()
    for y, row in enumerate(rows):
        for x, c in enumerate(row):
            if c == "1":
                for dy in range(SCALE):
                    for dx in range(SCALE):
                        px[(x + QUIET) * SCALE + dx, (y + QUIET) * SCALE + dy] = 0
    return img


def main(folder: str) -> int:
    ok = bad = 0
    for f in sorted(pathlib.Path(folder).glob("*.txt")):
        expected = f.with_suffix(".expected").read_text(encoding="utf-8")
        meta = f.with_suffix(".meta").read_text()
        results = zxingcpp.read_barcodes(render(f.read_text()))
        got = results[0].text if results else None
        if got == expected:
            ok += 1
        else:
            bad += 1
            print(f"FALLA {f.stem} ({meta}): leído={got!r}")
    print(f"{ok} decodificados correctamente, {bad} fallas")
    return 0 if bad == 0 and ok > 0 else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1]))

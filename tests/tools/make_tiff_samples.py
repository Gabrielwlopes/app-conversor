"""Gera TIFFs de exemplo (com Pillow e tifffile) e, ao lado de cada um, um .rgba com os pixels esperados.

Uso:
    pip install pillow tifffile imagecodecs numpy
    python tests/tools/make_tiff_samples.py <pasta>
    dotnet run --project tests/Conversor.Core.Checks -- <pasta>

Arquivos sem .rgba são variantes que o app avisa que não suporta (JPEG dentro do TIFF, planos separados).
Também confere, com o Pillow, os TIFFs gravados pelo app:
    dotnet run --project tests/Conversor.Core.Checks -- --write <pasta>
    python tests/tools/make_tiff_samples.py --check <pasta>
"""

import os
import sys

import numpy as np
import tifffile
from PIL import Image


def photo(width, height, channels, seed, dtype=np.uint8):
    rng = np.random.default_rng(seed)
    y, x = np.mgrid[0:height, 0:width]
    top = 65535 if dtype == np.uint16 else 255
    planes = [
        (x * top // max(1, width - 1)),
        (y * top // max(1, height - 1)),
        ((x + y) * (top // 255) * 3) % (top + 1),
        ((x * 7 + y * 3) * (top // 255)) % (top + 1),
    ][:channels]
    data = np.stack(planes, axis=-1).astype(np.int64)
    data = (data + rng.integers(0, max(2, top // 32), size=data.shape)) % (top + 1)
    return data.astype(dtype)


def save_reference(path, rgba):
    with open(os.path.splitext(path)[0] + ".rgba", "wb") as f:
        f.write(np.ascontiguousarray(rgba, dtype=np.uint8).tobytes())


def with_alpha(rgb, alpha=None):
    a = np.full(rgb.shape[:2] + (1,), 255, np.uint8) if alpha is None else alpha[..., None]
    return np.concatenate([rgb.astype(np.uint8), a], axis=-1)


def grey_to_rgba(grey):
    return with_alpha(np.repeat(grey[..., None], 3, axis=-1))


def generate(folder):
    os.makedirs(folder, exist_ok=True)
    p = lambda name: os.path.join(folder, name)

    # Pillow (libtiff): o que câmeras, scanners e programas comuns gravam.
    rgb = photo(301, 207, 3, 1)
    for compression in ["raw", "tiff_lzw", "tiff_adobe_deflate", "packbits"]:
        path = p(f"pillow_rgb_{compression}.tif")
        Image.fromarray(rgb, "RGB").save(path, compression=compression)
        save_reference(path, with_alpha(rgb))

    rgba = photo(160, 90, 4, 2)
    path = p("pillow_rgba_lzw.tif")
    Image.fromarray(rgba, "RGBA").save(path, compression="tiff_lzw")
    save_reference(path, rgba)

    grey = photo(255, 129, 1, 3)[..., 0]
    path = p("pillow_grey_lzw.tif")
    Image.fromarray(grey, "L").save(path, compression="tiff_lzw")
    save_reference(path, grey_to_rgba(grey))

    bilevel = (photo(203, 61, 1, 4)[..., 0] > 128)
    for compression in ["raw", "packbits", "tiff_lzw"]:
        path = p(f"pillow_1bit_{compression}.tif")
        Image.fromarray(bilevel).convert("1").save(path, compression=compression)
        save_reference(path, np.asarray(Image.open(path).convert("RGBA")))

    palette = Image.fromarray(rgb, "RGB").quantize(colors=37)
    path = p("pillow_palette_lzw.tif")
    palette.save(path, compression="tiff_lzw")
    save_reference(path, np.asarray(Image.open(path).convert("RGBA")))

    # tifffile: blocos (tiles), preditor, big-endian, 16 bits, CMYK, várias faixas, 2 e 4 bits.
    path = p("tifffile_tiles_lzw_predictor.tif")
    tifffile.imwrite(path, rgb, tile=(64, 48), compression="lzw", predictor=True, photometric="rgb")
    save_reference(path, with_alpha(rgb))

    path = p("tifffile_strips_deflate_predictor_bigendian.tif")
    tifffile.imwrite(path, rgb, byteorder=">", rowsperstrip=7, compression="zlib", predictor=True, photometric="rgb")
    save_reference(path, with_alpha(rgb))

    rgba_tiles = photo(130, 70, 4, 5)
    path = p("tifffile_rgba_tiles_packbits.tif")
    tifffile.imwrite(path, rgba_tiles, tile=(32, 32), compression="packbits", photometric="rgb", extrasamples=["unassalpha"])
    save_reference(path, rgba_tiles)

    rgb16 = photo(97, 55, 3, 6, np.uint16)
    for order in ["<", ">"]:
        name = "le" if order == "<" else "be"
        path = p(f"tifffile_rgb16_lzw_predictor_{name}.tif")
        tifffile.imwrite(path, rgb16, byteorder=order, compression="lzw", predictor=True, photometric="rgb")
        save_reference(path, with_alpha((rgb16 >> 8).astype(np.uint8)))

    grey16 = photo(64, 40, 1, 7, np.uint16)[..., 0]
    path = p("tifffile_grey16_raw.tif")
    tifffile.imwrite(path, grey16, photometric="minisblack")
    save_reference(path, grey_to_rgba((grey16 >> 8).astype(np.uint8)))

    white_is_zero = photo(50, 30, 1, 8)[..., 0]
    path = p("tifffile_miniswhite.tif")
    tifffile.imwrite(path, white_is_zero, photometric="miniswhite")
    save_reference(path, grey_to_rgba(255 - white_is_zero))

    cmyk = photo(80, 45, 4, 9)
    path = p("tifffile_cmyk_lzw.tif")
    tifffile.imwrite(path, cmyk, photometric="separated", compression="lzw")
    c, m, y, k = [cmyk[..., i].astype(np.int64) for i in range(4)]
    expected = np.stack([(255 - c) * (255 - k) // 255, (255 - m) * (255 - k) // 255, (255 - y) * (255 - k) // 255], axis=-1)
    save_reference(path, with_alpha(expected.astype(np.uint8)))

    for bits in [2, 4]:
        levels = (photo(77, 33, 1, 10 + bits)[..., 0] >> (8 - bits)).astype(np.uint8)
        path = p(f"tifffile_grey{bits}bit.tif")
        tifffile.imwrite(path, levels, photometric="minisblack", bitspersample=bits)
        save_reference(path, grey_to_rgba((levels.astype(np.int64) * 255 // ((1 << bits) - 1)).astype(np.uint8)))

    # Variantes não suportadas: o app tem que recusar com uma mensagem, sem travar.
    tifffile.imwrite(p("naosuportado_jpeg.tif"), rgb, compression="jpeg", photometric="rgb")
    tifffile.imwrite(p("naosuportado_planar.tif"), np.moveaxis(rgb, -1, 0), planarconfig="separate", photometric="rgb")

    print(f"exemplos gravados em {folder}")


def check(folder):
    failures = 0
    for name in sorted(os.listdir(folder)):
        if not name.startswith("saida_") or not name.endswith(".tiff"):
            continue
        path = os.path.join(folder, name)
        expected = np.fromfile(os.path.splitext(path)[0] + ".rgba", np.uint8)
        image = Image.open(path)
        actual = np.asarray(image.convert("RGBA")).reshape(-1)
        ok = actual.shape == expected.shape and np.array_equal(actual, expected)
        tif = tifffile.TiffFile(path)
        info = f"{image.mode} {image.size} compressão={tif.pages[0].compression.name} preditor={tif.pages[0].predictor}"
        print(("OK " if ok else "FALHOU ") + name + " — " + info)
        failures += not ok
    sys.exit(1 if failures else 0)


if __name__ == "__main__":
    if len(sys.argv) == 3 and sys.argv[1] == "--check":
        check(sys.argv[2])
    elif len(sys.argv) == 2:
        generate(sys.argv[1])
    else:
        print(__doc__)
        sys.exit(2)

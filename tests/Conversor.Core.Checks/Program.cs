// Testes do Conversor.Core. Uso:
//   dotnet run --project tests/Conversor.Core.Checks
//   dotnet run --project tests/Conversor.Core.Checks -- <pasta>   (também confere os TIFFs de exemplo da pasta,
//                                                                   cada um com um .rgba de referência ao lado)
//   dotnet run --project tests/Conversor.Core.Checks -- --write <pasta>   (grava TIFFs para outro programa conferir)

using Conversor.Core;
using Conversor.Core.Tiff;

var failures = 0;
var passed = 0;

void Check(bool condition, string what)
{
    if (condition)
    {
        passed++;
        return;
    }
    failures++;
    Console.WriteLine($"FALHOU: {what}");
}

// ───────────── Detecção de formato ─────────────

byte[] Header(params object[] parts)
{
    var bytes = new List<byte>();
    foreach (var part in parts)
    {
        if (part is string text)
            bytes.AddRange(System.Text.Encoding.ASCII.GetBytes(text));
        else if (part is int value)
            bytes.Add((byte)value);
        else if (part is byte[] raw)
            bytes.AddRange(raw);
    }
    while (bytes.Count < 64)
        bytes.Add(0);
    return bytes.ToArray();
}

byte[] Ftyp(string major, params string[] compatible)
{
    var size = 16 + compatible.Length * 4;
    var box = new List<byte> { 0, 0, 0, (byte)size };
    box.AddRange(System.Text.Encoding.ASCII.GetBytes("ftyp" + major + "\0\0\0\0" + string.Concat(compatible)));
    return Header(box.ToArray());
}

Check(FileFormats.Detect(Header(0xFF, 0xD8, 0xFF, 0xE0)) == FileFormats.Jpg, "JPEG");
Check(FileFormats.Detect(Header(0x89, "PNG", 0x0D, 0x0A, 0x1A, 0x0A)) == FileFormats.Png, "PNG");
Check(FileFormats.Detect(Header("GIF89a")) == FileFormats.Gif, "GIF");
Check(FileFormats.Detect(Header("RIFF", 0, 0, 0, 0, "WEBPVP8 ")) == FileFormats.Webp, "WebP");
Check(FileFormats.Detect(Header("II*", 0)) == FileFormats.Tiff, "TIFF little-endian");
Check(FileFormats.Detect(Header("MM", 0, "*")) == FileFormats.Tiff, "TIFF big-endian");
Check(FileFormats.Detect(Header("BM", 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 40, 0, 0, 0)) == FileFormats.Bmp, "BMP");
Check(FileFormats.Detect(Header("BMW e Mercedes")) is null, "texto começando com BM não é BMP");
Check(FileFormats.Detect(Header("%PDF-1.7\n")) == FileFormats.Pdf, "PDF");
Check(FileFormats.Detect(Header("\r\n\r\n%PDF-1.4")) == FileFormats.Pdf, "PDF com lixo antes do cabeçalho");
Check(FileFormats.Detect(Ftyp("heic", "mif1", "heic")) == FileFormats.Heic, "HEIC");
Check(FileFormats.Detect(Ftyp("mif1", "mif1", "avif", "miaf")) == FileFormats.Avif, "AVIF com marca principal mif1");
Check(FileFormats.Detect(Ftyp("avif", "avif", "mif1")) == FileFormats.Avif, "AVIF");
Check(FileFormats.Detect(Ftyp("isom", "isom", "mp41")) is null, "MP4 não é imagem");
Check(FileFormats.Detect(Header("PK", 3, 4)) is null, "ZIP/DOCX não é entrada");
Check(FileFormats.FromFileName("Foto.JPEG") == FileFormats.Jpg, "extensão .JPEG");
Check(FileFormats.FromFileName("scan.tif") == FileFormats.Tiff, "extensão .tif");

// ───────────── Destinos ─────────────

var pdfTargets = Targets.For([FileFormats.Pdf]).Select(t => t.Format.Id).ToArray();
Check(pdfTargets.SequenceEqual(["docx", "txt", "jpg", "png"]), $"destinos de PDF: {string.Join(",", pdfTargets)}");
var jpgTargets = Targets.For([FileFormats.Jpg, FileFormats.Jpg]).Select(t => t.Format.Id).ToArray();
Check(jpgTargets.SequenceEqual(["png", "webp", "tiff", "pdf"]), $"destinos de JPGs: {string.Join(",", jpgTargets)}");
var mixedImages = Targets.For([FileFormats.Jpg, FileFormats.Png]).Select(t => t.Format.Id).ToArray();
Check(mixedImages.SequenceEqual(["png", "jpg", "webp", "tiff", "pdf"]), "JPG + PNG oferecem todos os formatos de imagem");
Check(Targets.For([FileFormats.Pdf, FileFormats.Jpg]).Count == 0, "PDF + imagem não têm destino comum");
Check(Targets.For([]).Count == 0, "nada escolhido");

// ───────────── Nomes de saída ─────────────

Check(OutputNames.For("Relatório final.pdf", FileFormats.Docx) == "Relatório final.docx", "nome simples");
Check(OutputNames.For("doc.pdf", FileFormats.Png, 0, 12) == "doc_p01.png", "página 1 de 12");
Check(OutputNames.For("doc.pdf", FileFormats.Png, 99, 120) == "doc_p100.png", "página 100 de 120");
Check(OutputNames.For("doc.pdf", FileFormats.Png, 0, 1) == "doc.png", "PDF de uma página não ganha número");
Check(OutputNames.For("a/b:c?.jpg", FileFormats.Png) == "a_b_c_.png", "caracteres proibidos");
Check(OutputNames.For(".jpg", FileFormats.Png) == "arquivo.png", "nome vazio");
Check(OutputNames.For(null, FileFormats.Pdf) == "arquivo.pdf", "sem nome");
Check(OutputNames.For("Relatório v1.2 final", FileFormats.Docx) == "Relatório v1.2 final.docx", "ponto no meio do nome não é extensão");

// ───────────── LZW ─────────────

var random = new Random(1234);
foreach (var (name, data) in new (string, byte[])[]
         {
             ("vazio", []),
             ("um byte", [42]),
             ("repetitivo", Enumerable.Repeat((byte)7, 100_000).ToArray()),
             ("aleatório", Bytes(random, 300_000, 256)),
             ("pouca variedade", Bytes(random, 500_000, 4)),
             ("texto", System.Text.Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("Conversor de arquivos para Android. ", 5000)))),
         })
{
    var packed = TiffLzw.Encode(data);
    var unpacked = new byte[data.Length];
    var count = TiffLzw.Decode(packed, unpacked);
    Check(count == data.Length && unpacked.AsSpan().SequenceEqual(data), $"LZW ida e volta: {name}");
}

// ───────────── TIFF: gravar e ler de volta ─────────────

foreach (var (width, height, alpha) in new[] { (1, 1, false), (17, 5, false), (300, 200, true), (2000, 900, false), (513, 777, true) })
{
    var source = Pixels(random, width, height, alpha);
    using var stream = new MemoryStream();
    TiffEncoder.Write(stream, width, height, alpha, (y, row) => Array.Copy(source, (long)y * width, row, 0, width));
    var page = TiffDecoder.Decode(stream.ToArray());
    Check(page.Width == width && page.Height == height && page.HasAlpha == alpha, $"TIFF {width}x{height}: tamanho");
    Check(page.Pixels.AsSpan().SequenceEqual(source), $"TIFF {width}x{height} {(alpha ? "RGBA" : "RGB")}: pixels iguais");
}

// ───────────── TIFF: PackBits (exemplo da especificação TIFF 6.0) ─────────────

{
    byte[] packed = [0xFE, 0xAA, 0x02, 0x80, 0x00, 0x2A, 0xFD, 0xAA, 0x03, 0x80, 0x00, 0x2A, 0x22, 0xF7, 0xAA];
    byte[] expected =
    [
        0xAA, 0xAA, 0xAA, 0x80, 0x00, 0x2A, 0xAA, 0xAA, 0xAA, 0xAA, 0x80, 0x00, 0x2A, 0x22,
        0xAA, 0xAA, 0xAA, 0xAA, 0xAA, 0xAA, 0xAA, 0xAA, 0xAA, 0xAA,
    ];
    // Uma imagem de 24 × 1 em tons de cinza, 8 bits, com esses dados.
    var tiff = MinimalTiff(24, 1, compression: 32773, data: packed);
    var page = TiffDecoder.Decode(tiff);
    var grey = page.Pixels.Select(p => (byte)p).ToArray();
    Check(grey.SequenceEqual(expected), "PackBits");
}

// ───────────── TIFFs de exemplo (opcional) ─────────────

if (args.Length == 2 && args[0] == "--write")
{
    Directory.CreateDirectory(args[1]);
    foreach (var (width, height, alpha) in new[] { (640, 480, false), (333, 251, true) })
    {
        var source = Pixels(random, width, height, alpha);
        var path = Path.Combine(args[1], $"saida_{width}x{height}_{(alpha ? "rgba" : "rgb")}.tiff");
        using (var file = File.Create(path))
            TiffEncoder.Write(file, width, height, alpha, (y, row) => Array.Copy(source, (long)y * width, row, 0, width));
        File.WriteAllBytes(Path.ChangeExtension(path, ".rgba"), ToRgba(source));
        Console.WriteLine($"gravado {path}");
    }
}
else if (args.Length == 1 && Directory.Exists(args[0]))
{
    foreach (var path in Directory.GetFiles(args[0], "*.tif*").Order())
    {
        var reference = Path.ChangeExtension(path, ".rgba");
        try
        {
            var page = TiffDecoder.Decode(File.ReadAllBytes(path));
            if (File.Exists(reference))
                Check(ToRgba(page.Pixels).AsSpan().SequenceEqual(File.ReadAllBytes(reference)), $"exemplo {Path.GetFileName(path)}");
            else
                Console.WriteLine($"  {Path.GetFileName(path)}: lido ({page.Width}x{page.Height}), sem referência");
        }
        catch (NotSupportedException e) when (!File.Exists(reference))
        {
            Console.WriteLine($"  {Path.GetFileName(path)}: não suportado, como esperado ({e.Message})");
        }
        catch (Exception e)
        {
            Check(false, $"exemplo {Path.GetFileName(path)}: {e.GetType().Name}: {e.Message}");
        }
    }
}

Console.WriteLine($"{passed} testes passaram, {failures} falharam.");
return failures == 0 ? 0 : 1;

static byte[] Bytes(Random random, int count, int variety)
{
    var data = new byte[count];
    for (var i = 0; i < count; i++)
        data[i] = (byte)random.Next(variety);
    return data;
}

static int[] Pixels(Random random, int width, int height, bool alpha)
{
    // Gradientes com ruído: parecido com foto, exercita o preditor e a tabela do LZW.
    var pixels = new int[width * height];
    for (var y = 0; y < height; y++)
    for (var x = 0; x < width; x++)
    {
        var r = (x * 255 / Math.Max(1, width - 1) + random.Next(8)) & 0xFF;
        var g = (y * 255 / Math.Max(1, height - 1) + random.Next(8)) & 0xFF;
        var b = (x + y + random.Next(32)) & 0xFF;
        var a = alpha ? (x * 7 + y * 3) & 0xFF : 0xFF;
        pixels[y * width + x] = a << 24 | r << 16 | g << 8 | b;
    }
    return pixels;
}

static byte[] ToRgba(int[] pixels)
{
    var bytes = new byte[pixels.Length * 4];
    for (var i = 0; i < pixels.Length; i++)
    {
        var p = pixels[i];
        bytes[i * 4] = (byte)(p >> 16);
        bytes[i * 4 + 1] = (byte)(p >> 8);
        bytes[i * 4 + 2] = (byte)p;
        bytes[i * 4 + 3] = (byte)(p >>> 24);
    }
    return bytes;
}

static byte[] MinimalTiff(int width, int height, int compression, byte[] data)
{
    using var stream = new MemoryStream();
    using var w = new BinaryWriter(stream);
    w.Write("II"u8);
    w.Write((ushort)42);
    w.Write(8u);
    (ushort Tag, ushort Type, uint Count, uint Value)[] entries =
    [
        (256, 4, 1, (uint)width), (257, 4, 1, (uint)height), (258, 3, 1, 8), (259, 3, 1, (uint)compression),
        (262, 3, 1, 1), (273, 4, 1, 0), (277, 3, 1, 1), (278, 4, 1, (uint)height), (279, 4, 1, (uint)data.Length),
    ];
    var dataOffset = (uint)(8 + 2 + entries.Length * 12 + 4);
    w.Write((ushort)entries.Length);
    foreach (var (tag, type, count, value) in entries)
    {
        w.Write(tag);
        w.Write(type);
        w.Write(count);
        w.Write(tag == 273 ? dataOffset : value);
    }
    w.Write(0u);
    w.Write(data);
    return stream.ToArray();
}

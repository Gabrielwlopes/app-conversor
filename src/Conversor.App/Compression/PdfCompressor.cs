// Adaptado do Filee (https://github.com/WDHUS4G/Filee, licença MIT, © Myeonggu Kim e colaboradores):
// src/Filee.Engines/Pdf/PdfCompressor.cs. A lógica é a do Filee; só a parte de pixels mudou: no computador o Filee usa
// o ImageMagick, que não existe no Android, então aqui abrir, reduzir e gravar as imagens usa o Bitmap do Android
// (classe Pixels, no fim do arquivo). Imagens CMYK ficam como estão (o Bitmap do Android não tem CMYK), e fotos em
// tons de cinza regravadas em JPEG passam a ser RGB (o codificador JPEG do Android sempre grava em cores).
//
// Makes a PDF smaller while it stays a PDF (PDFsharp + Magick.NET). Pictures drawn at more than the level's resolution
// are scaled down to it, photos are saved as JPEG at the level's quality and pictures with few colours (charts,
// screenshots, logos) losslessly; every other stream is deflated at the highest level; page thumbnails, other
// applications' private data and, when the preset drops metadata, the XMP metadata of pages and pictures are left out
// (PDFsharp writes the document's own XMP from its document information on every save). Text, fonts and vector
// drawings stay as they are. A picture is changed only when the result is smaller.

using System.IO.Compression;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.Content;
using PdfSharp.Pdf.Content.Objects;
using PdfSharp.Pdf.IO;

using Bitmap = Android.Graphics.Bitmap;
using BitmapFactory = Android.Graphics.BitmapFactory;

namespace Conversor.App.Compression;

/// <summary>Shrinks the pictures and streams of a PDF document in place (before it is saved).</summary>
internal static class PdfCompressor
{
    /// <summary>
    /// Pictures are scaled down only when they are drawn at more than this many times the level's resolution, like
    /// Ghostscript does: a picture just above it would lose sharpness for little saving.
    /// </summary>
    private const double DownsampleThreshold = 1.5;

    /// <summary>A picture drawn tiny (or with a transformation read wrong) never gets smaller than this, in pixels.</summary>
    private const int MinimumSide = 8;

    /// <summary>A JPEG that keeps its size is encoded again only when that saves at least this share of it.</summary>
    private const double MinimumJpegSaving = 0.1;

    /// <summary>Colours up to which a picture is a graphic (kept lossless) rather than a photo (JPEG).</summary>
    private const int GraphicColors = 256;

    /// <summary>The same for gray pictures, which never have more than 256 shades: a gray photo has many of them.</summary>
    private const int GraphicGrays = 32;

    /// <summary>Shrinks <paramref name="document"/>; save it afterwards.</summary>
    /// <param name="keepMetadata">False drops the XMP metadata of pages, pictures and forms.</param>
    /// <param name="dpi">Resolution pictures are scaled down to.</param>
    /// <param name="quality">JPEG quality of the pictures saved again.</param>
    public static void Compress(PdfDocument document, int dpi, int quality, bool keepMetadata, CancellationToken cancellationToken)
    {
        var done = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        foreach (var (image, use) in PdfPictureUses.Find(document))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (done.Add(image))
                ShrinkPicture(document, image, use, dpi, quality, done);
        }

        MergeDuplicatePictures(document);

        foreach (var page in document.Pages)
        {
            page.Elements.Remove("/Thumb");
            page.Elements.Remove("/PieceInfo");
        }
        document.Internals.Catalog.Elements.Remove("/PieceInfo");
        // PDFsharp writes the document's XMP afresh from its document information when it saves (as for every PDF it
        // writes); the old packet would stay in the file unreferenced, so it goes whatever the preset says.
        document.Internals.Catalog.Elements.Remove("/Metadata");

        foreach (var item in document.Internals.GetAllObjects())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item is not PdfDictionary dictionary)
                continue;
            if (!keepMetadata && dictionary.Elements.ContainsKey("/Metadata"))
                dictionary.Elements.Remove("/Metadata");
            if (dictionary.Stream is not null)
                Deflate(dictionary);
        }

        document.Options.CompressContentStreams = true;
        document.Options.FlateEncodeMode = PdfFlateEncodeMode.BestCompression;
        document.Options.Layout = PdfWriterLayout.Compact;
    }

    /// <summary>Scales a picture down and saves it as JPEG or Flate, when that's smaller. Soft masks follow it.</summary>
    private static void ShrinkPicture(PdfDocument document, PdfDictionary image, PictureUse use, int dpi, int quality, HashSet<PdfDictionary> done)
    {
        if (Decode(image) is not { } picture)
            return;
        using var pixels = picture.Image;
        var width = pixels.Width;
        var height = pixels.Height;

        // Pixels needed to show the picture at the target resolution where it is drawn largest.
        var scale = Math.Max(use.WidthInches * dpi / width, use.HeightInches * dpi / height);
        var downsample = scale * DownsampleThreshold < 1;
        var newWidth = downsample ? Math.Clamp((int)Math.Round(width * scale), Math.Min(MinimumSide, width), width) : width;
        var newHeight = downsample ? Math.Clamp((int)Math.Round(height * scale), Math.Min(MinimumSide, height), height) : height;
        downsample = newWidth < width || newHeight < height;
        if (downsample)
            pixels.Resize(newWidth, newHeight);

        var original = image.Stream.Value.Length;
        // A JPEG that only gets a lower quality is worth it only for a clear saving: it loses detail once more.
        var limit = !downsample && picture.WasJpeg ? (int)(original * (1 - MinimumJpegSaving)) : original;
        var colorLimit = picture.Components == 1 ? GraphicGrays : GraphicColors;
        var graphic = !picture.WasJpeg && pixels.CountColors(colorLimit) <= colorLimit;
        // Uma foto em cinza vira JPEG colorido (RGB); com /Decode (valores invertidos) isso mudaria a imagem: fica Flate.
        var grayWithDecode = picture.Components == 1 && image.Elements.ContainsKey("/Decode");
        var encoded = picture.Components == 4 || graphic || grayWithDecode
            ? EncodeFlate(pixels, picture.Components)
            : EncodeJpeg(pixels, picture.Components, quality);
        if (encoded is null || encoded.Value.Data.Length >= limit)
            return;

        image.Stream.Value = encoded.Value.Data;
        image.Elements.SetName("/Filter", encoded.Value.Filter);
        if (encoded.Value.DecodeParms is { } parms)
            image.Elements["/DecodeParms"] = Parms(document, parms);
        else
            image.Elements.Remove("/DecodeParms");
        image.Elements.SetInteger("/Width", newWidth);
        image.Elements.SetInteger("/Height", newHeight);
        image.Elements.SetInteger("/BitsPerComponent", 8);
        if (encoded.Value.GrayAsRgb)
            image.Elements.SetName("/ColorSpace", "/DeviceRGB");

        // The soft mask (transparency) may have its own size: scaled by as much as the picture, and kept lossless.
        if (downsample && Resolve(image.Elements["/SMask"]) is PdfDictionary mask && done.Add(mask))
            ShrinkMask(document, mask, (double)newWidth / width, (double)newHeight / height);
    }

    private static void ShrinkMask(PdfDocument document, PdfDictionary mask, double scaleX, double scaleY)
    {
        if (Decode(mask) is not { Components: 1, WasJpeg: false } picture)
            return;
        using var pixels = picture.Image;
        var width = (int)Math.Max(1, Math.Round(pixels.Width * scaleX));
        var height = (int)Math.Max(1, Math.Round(pixels.Height * scaleY));
        if (width != pixels.Width || height != pixels.Height)
            pixels.Resize(width, height);
        if (EncodeFlate(pixels, 1) is not { } encoded || encoded.Data.Length >= mask.Stream.Value.Length)
            return;
        mask.Stream.Value = encoded.Data;
        mask.Elements.SetName("/Filter", encoded.Filter);
        mask.Elements["/DecodeParms"] = Parms(document, encoded.DecodeParms!.Value);
        mask.Elements.SetInteger("/Width", width);
        mask.Elements.SetInteger("/Height", height);
        mask.Elements.SetInteger("/BitsPerComponent", 8);
    }

    /// <summary>
    /// Pictures stored more than once with the same data and settings (a logo embedded again on every page) are kept
    /// once: every reference to a copy points to the first. Soft masks first, so the pictures using them compare equal.
    /// </summary>
    private static void MergeDuplicatePictures(PdfDocument document)
    {
        var images = document.Internals.GetAllObjects().OfType<PdfDictionary>()
            .Where(d => d.Stream is not null && d.Elements.GetName("/Subtype") == "/Image" && d.Reference is not null)
            .ToList();
        foreach (var pass in new[] { images.Where(Plain).ToList(), images.Where(i => !Plain(i)).ToList() })
        {
            var first = new Dictionary<string, PdfDictionary>(StringComparer.Ordinal);
            var copies = new Dictionary<PdfDictionary, PdfReference>(ReferenceEqualityComparer.Instance);
            foreach (var image in pass)
            {
                var key = string.Join(" ", image.Elements.Keys.Where(k => k != "/Length").Order(StringComparer.Ordinal)
                    .Select(k => k + " " + image.Elements[k])) + " " + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(image.Stream.Value));
                if (!first.TryAdd(key, image))
                    copies[image] = first[key].Reference!;
            }
            if (copies.Count == 0)
                continue;
            foreach (var item in document.Internals.GetAllObjects())
            {
                if (item is PdfDictionary or PdfArray)
                    Redirect(item, copies, depth: 0);
            }
        }

        static bool Plain(PdfDictionary image) => !image.Elements.ContainsKey("/SMask") && !image.Elements.ContainsKey("/Mask");
    }

    /// <summary>Points references to the copies in <paramref name="copies"/> to the kept picture, in direct objects too.</summary>
    private static void Redirect(PdfItem container, Dictionary<PdfDictionary, PdfReference> copies, int depth)
    {
        if (depth > 32)
            return;
        if (container is PdfDictionary dictionary)
        {
            foreach (var key in dictionary.Elements.Keys.ToList())
            {
                var value = dictionary.Elements[key];
                if (value is PdfReference { Value: PdfDictionary target } && copies.TryGetValue(target, out var kept))
                    dictionary.Elements[key] = kept;
                else if (value is PdfDictionary { Reference: null } or PdfArray { Reference: null })
                    Redirect(value, copies, depth + 1);
            }
        }
        else if (container is PdfArray array)
        {
            for (var i = 0; i < array.Elements.Count; i++)
            {
                var value = array.Elements[i];
                if (value is PdfReference { Value: PdfDictionary target } && copies.TryGetValue(target, out var kept))
                    array.Elements[i] = kept;
                else if (value is PdfDictionary { Reference: null } or PdfArray { Reference: null })
                    Redirect(value, copies, depth + 1);
            }
        }
    }

    private static PdfDictionary Parms(PdfDocument document, (int Colors, int Columns) parms)
    {
        var dictionary = new PdfDictionary(document);
        dictionary.Elements.SetInteger("/Predictor", 15);
        dictionary.Elements.SetInteger("/Colors", parms.Colors);
        dictionary.Elements.SetInteger("/BitsPerComponent", 8);
        dictionary.Elements.SetInteger("/Columns", parms.Columns);
        return dictionary;
    }

    private sealed record DecodedPicture(Pixels Image, int Components, bool WasJpeg);

    /// <summary>
    /// The pixels of a picture Filee can encode again without changing how it looks: 8 bits per component, gray, RGB or
    /// CMYK (CMYK not from JPEG: Adobe's inverted CMYK JPEGs are easy to get wrong), stored as JPEG, Flate or
    /// uncompressed. Null for everything else (JBIG2, CCITT, JPEG 2000, 1-bit, indexed, spot colours, colour-key
    /// masks), which stays as it is.
    /// </summary>
    private static DecodedPicture? Decode(PdfDictionary image)
    {
        var elements = image.Elements;
        if (elements.GetName("/Subtype") != "/Image" || elements.GetBoolean("/ImageMask") || elements.GetInteger("/BitsPerComponent") != 8
            || Resolve(elements["/Mask"]) is PdfArray || elements.ContainsKey("/F"))
            return null;
        var width = elements.GetInteger("/Width");
        var height = elements.GetInteger("/Height");
        var components = Components(Resolve(elements["/ColorSpace"]));
        if (width <= 0 || height <= 0 || components == 0 || (long)width * height > 100_000_000)
            return null;

        var filters = Filters(image);
        var data = image.Stream.Value;
        try
        {
            if (filters is ["/DCTDecode"])
            {
                // ColorTransform overrides how the JPEG's colours are read: rare, so left alone.
                if (components == 4 || Parms(image) is { } dct && dct.Elements.ContainsKey("/ColorTransform"))
                    return null;
                if (JpegFrame(data) is not { } frame || frame.Width != width || frame.Height != height || frame.Components != components)
                    return null;
                return Pixels.FromJpeg(data) is { } jpeg && jpeg.Width == width && jpeg.Height == height
                    ? new DecodedPicture(jpeg, components, WasJpeg: true)
                    : null;
            }

            if (filters is not ([] or ["/FlateDecode"] or ["/RunLengthDecode"]))
                return null;
            var raw = filters switch
            {
                [] => data,
                ["/RunLengthDecode"] => UndoRunLength(data),
                _ => Inflate(data),
            };
            if (filters is ["/FlateDecode"] && Parms(image) is { } flate && flate.Elements.GetInteger("/Predictor") > 1)
            {
                // PNG prediction of whole pixels only; TIFF prediction (2) is rare in pictures.
                var parms = flate.Elements;
                var colors = parms.ContainsKey("/Colors") ? parms.GetInteger("/Colors") : 1;
                var columns = parms.ContainsKey("/Columns") ? parms.GetInteger("/Columns") : 1;
                var bits = parms.ContainsKey("/BitsPerComponent") ? parms.GetInteger("/BitsPerComponent") : 8;
                if (parms.GetInteger("/Predictor") < 10 || colors != components || columns != width || bits != 8
                    || UndoPngPredictor(raw, width * components, height, components) is not { } unpredicted)
                    return null;
                raw = unpredicted;
            }
            var size = width * height * components;
            if (raw.Length < size || components == 4)
                return null; // CMYK: o Bitmap do Android não guarda CMYK, a imagem fica como está
            return new DecodedPicture(Pixels.FromRaw(raw, width, height, components), components, WasJpeg: false);
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IOException or Java.Lang.Throwable)
        {
            return null; // damaged or unusual data: the picture stays as it is
        }
    }

    private static (byte[] Data, string Filter, (int Colors, int Columns)? DecodeParms, bool GrayAsRgb)? EncodeJpeg(Pixels pixels, int components, int quality)
    {
        var data = pixels.ToJpeg(quality);
        // Exactly what the image dictionary says, or the picture would come out wrong. Android always writes colour
        // (3 components): a gray picture then becomes RGB, which the caller writes into the dictionary.
        return JpegFrame(data) is var (w, h, n) && w == pixels.Width && h == pixels.Height && (n == components || (components == 1 && n == 3))
            ? (data, "/DCTDecode", null, components == 1)
            : null;
    }

    private static (byte[] Data, string Filter, (int Colors, int Columns)? DecodeParms, bool GrayAsRgb)? EncodeFlate(Pixels pixels, int components)
    {
        if (components == 4)
            return null;
        var raw = pixels.ToRaw(components);
        var stride = pixels.Width * components;
        if (raw.Length != stride * pixels.Height)
            return null;
        return (Deflate(PngPredict(raw, stride, pixels.Height, components)), "/FlateDecode", (components, pixels.Width), false);
    }

    /// <summary>Deflates an uncompressed stream, or deflates a Flate stream again at the highest level, when smaller.</summary>
    private static void Deflate(PdfDictionary dictionary)
    {
        var type = dictionary.Elements.GetName("/Type");
        // XMP stays readable for tools that search files for it; object and cross-reference streams are PDFsharp's.
        if (type is "/Metadata" or "/ObjStm" or "/XRef" || dictionary.Elements.ContainsKey("/F"))
            return;
        var filters = Filters(dictionary);
        var data = dictionary.Stream.Value;
        if (data.Length < 64)
            return;
        try
        {
            if (filters.Length == 0)
            {
                var packed = Deflate(data);
                if (packed.Length >= data.Length)
                    return;
                dictionary.Stream.Value = packed;
                dictionary.Elements.SetName("/Filter", "/FlateDecode");
            }
            else if (filters is ["/FlateDecode"])
            {
                // The data stays the same (predictor included), only packed tighter.
                var packed = Deflate(Inflate(data));
                if (packed.Length < data.Length)
                    dictionary.Stream.Value = packed;
            }
            else if (filters is ["/RunLengthDecode"])
            {
                var packed = Deflate(UndoRunLength(data));
                if (packed.Length >= data.Length)
                    return;
                dictionary.Stream.Value = packed;
                dictionary.Elements.SetName("/Filter", "/FlateDecode");
            }
        }
        catch (InvalidDataException)
        {
            // damaged stream: left as it is
        }
    }

    private static string[] Filters(PdfDictionary dictionary) => Resolve(dictionary.Elements["/Filter"]) switch
    {
        PdfName name => [name.Value],
        PdfArray array => [.. array.Elements.Select(e => Resolve(e) is PdfName n ? n.Value : "?")],
        _ => [],
    };

    private static PdfDictionary? Parms(PdfDictionary dictionary) => Resolve(dictionary.Elements["/DecodeParms"]) switch
    {
        PdfDictionary parms => parms,
        PdfArray { Elements.Count: 1 } array => Resolve(array.Elements[0]) as PdfDictionary,
        _ => null,
    };

    /// <summary>Components of a colour space Filee re-encodes (gray, RGB, CMYK and their calibrated / ICC forms), else 0.</summary>
    private static int Components(PdfItem? colorSpace) => colorSpace switch
    {
        PdfName { Value: "/DeviceGray" } => 1,
        PdfName { Value: "/DeviceRGB" } => 3,
        PdfName { Value: "/DeviceCMYK" } => 4,
        PdfArray { Elements.Count: > 0 } array => (Resolve(array.Elements[0]) as PdfName)?.Value switch
        {
            "/CalGray" => 1,
            "/CalRGB" => 3,
            "/ICCBased" when array.Elements.Count > 1 && Resolve(array.Elements[1]) is PdfDictionary icc
                => icc.Elements.GetInteger("/N") is var n and (1 or 3 or 4) ? n : 0,
            _ => 0,
        },
        _ => 0,
    };

    /// <summary>The object a reference points to; a name stored as an object of its own (ImageMagick does) as a name.</summary>
    internal static PdfItem? Resolve(PdfItem? item) => item is PdfReference reference
        ? reference.Value is PdfNameObject name ? new PdfName(name.Value) : reference.Value
        : item;

    private static byte[] Inflate(byte[] data)
    {
        using var input = new ZLibStream(new MemoryStream(data), CompressionMode.Decompress);
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }

    private static byte[] Deflate(byte[] data)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            zlib.Write(data);
        return output.ToArray();
    }

    /// <summary>
    /// RunLengthDecode (ImageMagick writes pictures with it): a length byte n &lt; 128 copies the next n + 1 bytes, n &gt; 128
    /// repeats the next byte 257 - n times, 128 ends the data.
    /// </summary>
    private static byte[] UndoRunLength(byte[] data)
    {
        using var output = new MemoryStream(data.Length * 2);
        var i = 0;
        while (i < data.Length)
        {
            var length = data[i++];
            if (length == 128)
                break;
            if (length < 128)
            {
                var count = Math.Min(length + 1, data.Length - i);
                output.Write(data, i, count);
                i += count;
            }
            else if (i < data.Length)
            {
                var value = data[i++];
                for (var n = 0; n < 257 - length; n++)
                    output.WriteByte(value);
            }
        }
        return output.ToArray();
    }

    /// <summary>Reverses PNG prediction (/Predictor 10-15): each row starts with its filter type. Null when damaged.</summary>
    private static byte[]? UndoPngPredictor(byte[] data, int stride, int rows, int bpp)
    {
        if (data.Length < (long)(stride + 1) * rows)
            return null;
        var result = new byte[stride * rows];
        var none = new byte[stride];
        for (var r = 0; r < rows; r++)
        {
            var type = data[r * (stride + 1)];
            var source = data.AsSpan(r * (stride + 1) + 1, stride);
            var row = result.AsSpan(r * stride, stride);
            ReadOnlySpan<byte> up = r == 0 ? none : result.AsSpan((r - 1) * stride, stride);
            for (var x = 0; x < stride; x++)
            {
                int a = x >= bpp ? row[x - bpp] : 0, b = up[x], c = x >= bpp ? up[x - bpp] : 0;
                int predicted;
                switch (type)
                {
                    case 0: predicted = 0; break;
                    case 1: predicted = a; break;
                    case 2: predicted = b; break;
                    case 3: predicted = (a + b) / 2; break;
                    case 4: predicted = Paeth(a, b, c); break;
                    default: return null;
                }
                row[x] = (byte)(source[x] + predicted);
            }
        }
        return result;
    }

    /// <summary>PNG-predicts rows (/Predictor 15), each with the filter that leaves the smallest values.</summary>
    private static byte[] PngPredict(byte[] raw, int stride, int rows, int bpp)
    {
        var output = new byte[(stride + 1) * rows];
        var candidates = new byte[5][];
        for (var t = 0; t < candidates.Length; t++)
            candidates[t] = new byte[stride];
        var none = new byte[stride];
        for (var r = 0; r < rows; r++)
        {
            ReadOnlySpan<byte> row = raw.AsSpan(r * stride, stride);
            ReadOnlySpan<byte> up = r == 0 ? none : raw.AsSpan((r - 1) * stride, stride);
            var best = 0;
            var bestSum = long.MaxValue;
            for (var type = 0; type < candidates.Length; type++)
            {
                var filtered = candidates[type];
                long sum = 0;
                for (var x = 0; x < stride; x++)
                {
                    int a = x >= bpp ? row[x - bpp] : 0, b = up[x], c = x >= bpp ? up[x - bpp] : 0;
                    var predicted = type switch { 0 => 0, 1 => a, 2 => b, 3 => (a + b) / 2, _ => Paeth(a, b, c) };
                    var value = (byte)(row[x] - predicted);
                    filtered[x] = value;
                    sum += value < 128 ? value : 256 - value;
                }
                if (sum < bestSum)
                {
                    bestSum = sum;
                    best = type;
                }
            }
            output[r * (stride + 1)] = (byte)best;
            candidates[best].CopyTo(output.AsSpan(r * (stride + 1) + 1));
        }
        return output;
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    /// <summary>Width, height and components of a JPEG's frame header, or null.</summary>
    private static (int Width, int Height, int Components)? JpegFrame(byte[] jpeg)
    {
        var i = 2;
        while (i + 9 < jpeg.Length)
        {
            if (jpeg[i] != 0xFF)
                return null;
            var marker = jpeg[i + 1];
            var length = (jpeg[i + 2] << 8) | jpeg[i + 3];
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
                return ((jpeg[i + 7] << 8) | jpeg[i + 8], (jpeg[i + 5] << 8) | jpeg[i + 6], jpeg[i + 9]);
            i += 2 + length;
        }
        return null;
    }

    /// <summary>
    /// Os pixels de uma imagem num Bitmap do Android (no lugar do MagickImage do Filee). Cinza fica com R = G = B.
    /// </summary>
    private sealed class Pixels : IDisposable
    {
        private Bitmap _bitmap;

        private Pixels(Bitmap bitmap) => _bitmap = bitmap;

        public int Width => _bitmap.Width;

        public int Height => _bitmap.Height;

        public static Pixels? FromJpeg(byte[] data)
        {
            var options = new BitmapFactory.Options { InPreferredConfig = Bitmap.Config.Argb8888 };
            var bitmap = BitmapFactory.DecodeByteArray(data, 0, data.Length, options);
            return bitmap is null ? null : new Pixels(bitmap);
        }

        public static Pixels FromRaw(byte[] raw, int width, int height, int components)
        {
            var bitmap = Bitmap.CreateBitmap(width, height, Bitmap.Config.Argb8888!) ?? throw new OutOfMemoryException();
            var row = new int[width];
            for (var y = 0; y < height; y++)
            {
                var at = y * width * components;
                for (var x = 0; x < width; x++)
                {
                    int r, g, b;
                    if (components == 1)
                    {
                        r = g = b = raw[at + x];
                    }
                    else
                    {
                        var i = at + x * 3;
                        r = raw[i];
                        g = raw[i + 1];
                        b = raw[i + 2];
                    }
                    row[x] = unchecked((int)0xFF000000) | r << 16 | g << 8 | b;
                }
                bitmap.SetPixels(row, 0, width, 0, y, width, 1);
            }
            return new Pixels(bitmap);
        }

        /// <summary>Reduz para o tamanho pedido: primeiro pela metade enquanto der (fica nítido), depois o ajuste final.</summary>
        public void Resize(int width, int height)
        {
            var current = _bitmap;
            while (current.Width / 2 >= width && current.Height / 2 >= height && current.Width >= 2 && current.Height >= 2)
                current = Replace(current, Bitmap.CreateScaledBitmap(current, current.Width / 2, current.Height / 2, true));
            if (current.Width != width || current.Height != height)
                current = Replace(current, Bitmap.CreateScaledBitmap(current, width, height, true));
            _bitmap = current;
        }

        private static Bitmap Replace(Bitmap old, Bitmap? scaled)
        {
            if (scaled is null)
                throw new OutOfMemoryException();
            old.Recycle();
            old.Dispose();
            return scaled;
        }

        /// <summary>Quantas cores diferentes a imagem tem, contando só até passar de <paramref name="limit"/>.</summary>
        public int CountColors(int limit)
        {
            var colors = new HashSet<int>();
            var row = new int[Width];
            for (var y = 0; y < Height; y++)
            {
                _bitmap.GetPixels(row, 0, Width, 0, y, Width, 1);
                foreach (var color in row)
                {
                    if (colors.Add(color) && colors.Count > limit)
                        return colors.Count;
                }
            }
            return colors.Count;
        }

        public byte[] ToJpeg(int quality)
        {
            using var stream = new MemoryStream();
            if (!_bitmap.Compress(Bitmap.CompressFormat.Jpeg!, quality, stream))
                throw new IOException("O Android não conseguiu gravar o JPEG.");
            return stream.ToArray();
        }

        /// <summary>As amostras de 8 bits, uma (cinza) ou três (RGB) por pixel, linha a linha.</summary>
        public byte[] ToRaw(int components)
        {
            var raw = new byte[Width * Height * components];
            var row = new int[Width];
            for (var y = 0; y < Height; y++)
            {
                _bitmap.GetPixels(row, 0, Width, 0, y, Width, 1);
                var at = y * Width * components;
                for (var x = 0; x < Width; x++)
                {
                    var color = row[x];
                    int r = (color >> 16) & 0xFF, g = (color >> 8) & 0xFF, b = color & 0xFF;
                    if (components == 1)
                    {
                        raw[at + x] = (byte)((r * 299 + g * 587 + b * 114 + 500) / 1000);
                    }
                    else
                    {
                        raw[at + x * 3] = (byte)r;
                        raw[at + x * 3 + 1] = (byte)g;
                        raw[at + x * 3 + 2] = (byte)b;
                    }
                }
            }
            return raw;
        }

        public void Dispose()
        {
            _bitmap.Recycle();
            _bitmap.Dispose();
        }
    }
}

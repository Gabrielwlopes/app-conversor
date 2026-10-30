using Conversor.Core;
using Conversor.Core.Tiff;
using Bitmap = Android.Graphics.Bitmap;
using BitmapFactory = Android.Graphics.BitmapFactory;
using Canvas = Android.Graphics.Canvas;
using Color = Android.Graphics.Color;
using ImageDecoder = Android.Graphics.ImageDecoder;

namespace Conversor.App.Conversion;

/// <summary>Ler e gravar imagens com os codificadores do próprio Android (e o TIFF do Conversor.Core).</summary>
internal static class ImageCodec
{
    /// <summary>Qualidade de JPG e WebP: alta, sem diferença visível do original.</summary>
    public const int Quality = 92;

    /// <summary>Acima disso a imagem é reduzida ao abrir, para caber na memória do celular.</summary>
    private const long MaxPixels = 80_000_000;

    /// <summary>
    /// Abre JPG, PNG, WebP, GIF, BMP, HEIC e AVIF (AVIF a partir do Android 12) já na orientação certa (a rotação
    /// gravada pela câmera é aplicada). O bitmap fica na memória comum, para poder ser lido pixel a pixel.
    /// </summary>
    public static Bitmap Decode(string path, FileFormat format)
    {
        try
        {
            var source = ImageDecoder.CreateSource(new Java.IO.File(path));
            return ImageDecoder.DecodeBitmap(source, new SoftwareBitmap());
        }
        catch (Exception decoderError) when (decoderError is not OutOfMemoryException and not Java.Lang.OutOfMemoryError)
        {
            // Plano B: o decodificador antigo (não gira pela câmera, mas abre alguns arquivos que o novo recusa).
            var options = new BitmapFactory.Options { InPreferredConfig = Bitmap.Config.Argb8888 };
            return BitmapFactory.DecodeFile(path, options)
                   ?? throw new NotSupportedException(
                       format == FileFormats.Avif && Android.OS.Build.VERSION.SdkInt < Android.OS.BuildVersionCodes.S
                           ? "Este celular não abre imagens AVIF (precisa do Android 12 ou mais novo)."
                           : "A imagem está corrompida ou num formato que o Android não abre.", decoderError);
        }
    }

    /// <summary>Uma página de TIFF (lida pelo Conversor.Core) como bitmap do Android.</summary>
    public static Bitmap FromTiff(TiffPage page)
    {
        var bitmap = Bitmap.CreateBitmap(page.Width, page.Height, Bitmap.Config.Argb8888!)
                     ?? throw new OutOfMemoryException();
        // Linha a linha: passar o array inteiro de uma vez faria uma cópia do mesmo tamanho na memória do Java.
        var row = new int[page.Width];
        for (var y = 0; y < page.Height; y++)
        {
            Array.Copy(page.Pixels, (long)y * page.Width, row, 0, page.Width);
            bitmap.SetPixels(row, 0, page.Width, 0, y, page.Width, 1);
        }
        bitmap.HasAlpha = page.HasAlpha;
        return bitmap;
    }

    /// <summary>Grava o bitmap em PNG, JPG, WebP ou TIFF.</summary>
    public static void Save(Bitmap bitmap, FileFormat format, string path, CancellationToken cancellationToken = default)
    {
        using var stream = File.Create(path);
        bool ok;
        switch (format.Id)
        {
            case "png":
                ok = bitmap.Compress(Bitmap.CompressFormat.Png!, 100, stream);
                break;
            case "jpg":
                ok = WithoutTransparency(bitmap, opaque => opaque.Compress(Bitmap.CompressFormat.Jpeg!, Quality, stream));
                break;
            case "webp":
#pragma warning disable CA1422, CS0618 // WEBP antigo: só usado antes do Android 11
                var webp = Android.OS.Build.VERSION.SdkInt >= Android.OS.BuildVersionCodes.R
                    ? Bitmap.CompressFormat.WebpLossy!
                    : Bitmap.CompressFormat.Webp!;
#pragma warning restore CA1422, CS0618
                ok = bitmap.Compress(webp, Quality, stream);
                break;
            case "tiff":
                var width = bitmap.Width;
                TiffEncoder.Write(stream, width, bitmap.Height, bitmap.HasAlpha,
                    (y, row) => bitmap.GetPixels(row, 0, width, 0, y, width, 1), cancellationToken: cancellationToken);
                ok = true;
                break;
            default:
                throw new NotSupportedException($"Gravar {format.Label} não é suportado.");
        }
        if (!ok)
            throw new IOException($"O Android não conseguiu gravar o {format.Label}.");
    }

    /// <summary>JPG não tem transparência: as partes transparentes ficam brancas (e não pretas).</summary>
    public static T WithoutTransparency<T>(Bitmap bitmap, Func<Bitmap, T> use)
    {
        if (!bitmap.HasAlpha)
            return use(bitmap);
        var opaque = Bitmap.CreateBitmap(bitmap.Width, bitmap.Height, Bitmap.Config.Argb8888!)
                     ?? throw new OutOfMemoryException();
        try
        {
            using (var canvas = new Canvas(opaque))
            {
                canvas.DrawColor(Color.White);
                canvas.DrawBitmap(bitmap, 0f, 0f, null);
            }
            return use(opaque);
        }
        finally
        {
            Release(opaque);
        }
    }

    /// <summary>Libera a memória dos pixels na hora (sem esperar o coletor de lixo).</summary>
    public static void Release(Bitmap? bitmap)
    {
        if (bitmap is null)
            return;
        bitmap.Recycle();
        bitmap.Dispose();
    }

    /// <summary>Pede ao decodificador um bitmap comum (não de GPU) e reduz imagens gigantes.</summary>
    private sealed class SoftwareBitmap : Java.Lang.Object, ImageDecoder.IOnHeaderDecodedListener
    {
        public void OnHeaderDecoded(ImageDecoder decoder, ImageDecoder.ImageInfo info, ImageDecoder.Source source)
        {
            // Bitmaps editáveis nunca ficam na GPU: assim dá para ler os pixels (TIFF) e desenhar sobre eles.
            decoder.MutableRequired = true;
            var size = info.Size;
            if (size is not null && (long)size.Width * size.Height > MaxPixels)
            {
                var scale = Math.Sqrt((double)MaxPixels / ((long)size.Width * size.Height));
                decoder.SetTargetSize(Math.Max(1, (int)(size.Width * scale)), Math.Max(1, (int)(size.Height * scale)));
            }
        }
    }
}

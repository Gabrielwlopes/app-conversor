using Conversor.Core;
using Conversor.Core.Tiff;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using Bitmap = Android.Graphics.Bitmap;

namespace Conversor.App.Conversion;

/// <summary>
/// Imagens → um PDF, uma imagem por página, com o PDFsharp (a mesma biblioteca que o Filee usa para isso).
/// Fotos JPG entram no PDF como estão, sem perder qualidade nem crescer.
/// </summary>
internal static class PdfBuilder
{
    /// <summary>Largura da página em pontos: a de uma folha A4 (em pé) ou a altura dela (deitada).</summary>
    private const double A4Short = 595.28, A4Long = 841.89;

    public static ConvertedFile FromImages(IReadOnlyList<PickedFile> images, string folder, string outputName,
        Action<double, string> report, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(folder);
        using var document = new PdfDocument();
        document.Info.Creator = "Conversor (Android)";

        for (var i = 0; i < images.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var image = images[i];
            report((double)i / images.Count, $"Adicionando {image.Name} ({i + 1} de {images.Count})");

            if (image.Format == FileFormats.Tiff)
            {
                var bytes = File.ReadAllBytes(image.LocalPath);
                var pages = TiffDecoder.PageCount(bytes);
                for (var p = 0; p < pages; p++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var bitmap = ImageCodec.FromTiff(TiffDecoder.Decode(bytes, p));
                    try
                    {
                        AddBitmap(document, bitmap, lossless: true);
                    }
                    finally
                    {
                        ImageCodec.Release(bitmap);
                    }
                }
            }
            else if (image.Format == FileFormats.Jpg && IsUpright(image.LocalPath))
            {
                // O JPG vai direto para dentro do PDF: nada é recomprimido.
                using var stream = File.OpenRead(image.LocalPath);
                AddPage(document, stream);
            }
            else
            {
                var bitmap = ImageCodec.Decode(image.LocalPath, image.Format);
                try
                {
                    // Prints de tela, desenhos e imagens com transparência ficam sem perdas (PNG); fotos, em JPG.
                    var lossless = bitmap.HasAlpha || image.Format == FileFormats.Png || image.Format == FileFormats.Gif
                                   || image.Format == FileFormats.Bmp;
                    AddBitmap(document, bitmap, lossless);
                }
                finally
                {
                    ImageCodec.Release(bitmap);
                }
            }
        }

        if (document.PageCount == 0)
            throw new NotSupportedException("Nenhuma imagem pôde ser adicionada ao PDF.");
        report(1, "Gravando o PDF");
        var path = Path.Combine(folder, outputName);
        document.Save(path);
        return new ConvertedFile(path, outputName, FileFormats.Pdf);
    }

    private static void AddBitmap(PdfDocument document, Bitmap bitmap, bool lossless)
    {
        using var stream = new MemoryStream();
        var ok = lossless
            ? bitmap.Compress(Bitmap.CompressFormat.Png!, 100, stream)
            : ImageCodec.WithoutTransparency(bitmap, opaque => opaque.Compress(Bitmap.CompressFormat.Jpeg!, ImageCodec.Quality, stream));
        if (!ok)
            throw new IOException("O Android não conseguiu preparar a imagem para o PDF.");
        stream.Position = 0;
        AddPage(document, stream);
    }

    /// <summary>Uma página do tamanho de uma folha A4 de largura, com a altura que a imagem pede.</summary>
    private static void AddPage(PdfDocument document, Stream imageData)
    {
        using var image = XImage.FromStream(imageData);
        var landscape = image.PixelWidth > image.PixelHeight;
        var width = landscape ? A4Long : A4Short;
        var height = width * image.PixelHeight / Math.Max(1, image.PixelWidth);

        var page = document.AddPage();
        page.Width = XUnit.FromPoint(width);
        page.Height = XUnit.FromPoint(height);
        using var graphics = XGraphics.FromPdfPage(page);
        graphics.DrawImage(image, 0, 0, width, height);
    }

    /// <summary>A foto já está de pé (a câmera não gravou uma rotação para aplicar depois).</summary>
    private static bool IsUpright(string path)
    {
        try
        {
            var exif = new Android.Media.ExifInterface(path);
            return exif.GetAttributeInt("Orientation", 1) is 0 or 1;
        }
        catch (Exception)
        {
            return false; // na dúvida, a foto é aberta e gravada de novo já girada
        }
    }
}

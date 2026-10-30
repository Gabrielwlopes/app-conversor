using Android.Graphics.Pdf;
using Android.OS;
using Conversor.Core;
using Bitmap = Android.Graphics.Bitmap;
using Canvas = Android.Graphics.Canvas;
using Color = Android.Graphics.Color;

namespace Conversor.App.Conversion;

/// <summary>PDF → JPG / PNG, uma imagem por página, com o leitor de PDF que vem no Android.</summary>
internal static class PdfPages
{
    /// <summary>Resolução das imagens (boa para ler e imprimir; uma página A4 dá 1240 × 1754 pixels).</summary>
    private const float Dpi = 150f;

    /// <summary>Lado maior máximo de cada imagem, para páginas enormes (plantas, banners) caberem na memória.</summary>
    private const int MaxSide = 4096;

    public static IReadOnlyList<ConvertedFile> Render(PickedFile pdf, FileFormat target, string folder,
        Action<double> report, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(folder);
        var results = new List<ConvertedFile>();
        // Os objetos de PDF do Android precisam ser fechados com Close() (Dispose não fecha a página).
        var descriptor = ParcelFileDescriptor.Open(new Java.IO.File(pdf.LocalPath), ParcelFileMode.ReadOnly)
                         ?? throw new IOException("Não foi possível abrir o PDF.");
        var renderer = new PdfRenderer(descriptor);
        try
        {
            var count = renderer.PageCount;
            if (count == 0)
                throw new NotSupportedException("O PDF não tem páginas.");

            for (var i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = renderer.OpenPage(i) ?? throw new IOException($"Não foi possível abrir a página {i + 1}.");
                try
                {
                    results.Add(RenderPage(page, pdf.Name, target, folder, i, count, cancellationToken));
                }
                finally
                {
                    page.Close();
                    page.Dispose();
                }
                report((i + 1.0) / count);
            }
        }
        finally
        {
            renderer.Close(); // fecha também o descritor do arquivo
            renderer.Dispose();
            descriptor.Dispose();
        }
        return results;
    }

    private static ConvertedFile RenderPage(PdfRenderer.Page page, string originalName, FileFormat target, string folder,
        int index, int count, CancellationToken cancellationToken)
    {
        var scale = Dpi / 72f; // tamanhos do PDF vêm em pontos (1/72 de polegada)
        var longest = Math.Max(page.Width, page.Height) * scale;
        if (longest > MaxSide)
            scale *= MaxSide / longest;
        var width = Math.Max(1, (int)(page.Width * scale));
        var height = Math.Max(1, (int)(page.Height * scale));

        var bitmap = Bitmap.CreateBitmap(width, height, Bitmap.Config.Argb8888!) ?? throw new OutOfMemoryException();
        try
        {
            using (var canvas = new Canvas(bitmap))
                canvas.DrawColor(Color.White); // o fundo do PDF é transparente: fica branco, como no papel
            page.Render(bitmap, null, null, PdfRenderMode.ForDisplay);
            bitmap.HasAlpha = false;

            var name = OutputNames.For(originalName, target, index, count);
            var path = Path.Combine(folder, name);
            ImageCodec.Save(bitmap, target, path, cancellationToken);
            return new ConvertedFile(path, name, target);
        }
        finally
        {
            ImageCodec.Release(bitmap);
        }
    }
}

using Conversor.App.Conversion;
using Conversor.Core;
using Conversor.Core.Tiff;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Bitmap = Android.Graphics.Bitmap;

namespace Conversor.App.Compression;

/// <summary>
/// Comprimir arquivos: PDF continua PDF (com o compressor do Filee, adaptado), foto vira JPG menor (ou PNG, se tem
/// transparência). Se o resultado não ficar menor, sai uma cópia do original e o app avisa que já estava otimizado.
/// </summary>
internal static class Compressor
{
    public const string Suffix = "_comprimido";

    /// <summary>
    /// PDF menor: imagens reduzidas à resolução do nível e regravadas em JPEG, o resto recomprimido no máximo, miniaturas
    /// e metadados de outros programas removidos. Texto, fontes e desenhos ficam exatamente como estavam.
    /// </summary>
    public static ConvertedFile Pdf(PickedFile input, CompressionLevel level, string folder, Action<double> report,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(folder);
        var name = OutputNames.For(input.Name, FileFormats.Pdf, suffix: Suffix);
        var path = Path.Combine(folder, name);
        report(0.05);
        using (var document = OpenForChanges(input.LocalPath))
        {
            PdfCompressor.Compress(document, level.PdfDpi, level.PdfQuality, keepMetadata: false, cancellationToken);
            report(0.8);
            document.Save(path);
        }
        report(1);
        return KeepSmaller(input, path, name, FileFormats.Pdf, null, 1);
    }

    /// <summary>Foto menor: lado maior limitado pelo nível e JPEG na qualidade do nível. TIFF: uma por página.</summary>
    public static IReadOnlyList<ConvertedFile> Image(PickedFile input, CompressionLevel level, string folder,
        Action<double> report, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(folder);
        if (input.Format == FileFormats.Tiff)
        {
            var bytes = File.ReadAllBytes(input.LocalPath);
            var pages = TiffDecoder.PageCount(bytes);
            var results = new List<ConvertedFile>();
            for (var p = 0; p < pages; p++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var bitmap = ImageCodec.FromTiff(TiffDecoder.Decode(bytes, p));
                try
                {
                    results.Add(CompressBitmap(bitmap, input, level, folder, pages > 1 ? p : null, pages));
                }
                finally
                {
                    ImageCodec.Release(bitmap);
                }
                report((p + 1.0) / pages);
            }
            return results;
        }

        var image = ImageCodec.Decode(input.LocalPath, input.Format);
        try
        {
            report(0.4);
            return [CompressBitmap(image, input, level, folder, null, 1)];
        }
        finally
        {
            ImageCodec.Release(image);
            report(1);
        }
    }

    private static ConvertedFile CompressBitmap(Bitmap bitmap, PickedFile input, CompressionLevel level, string folder,
        int? page, int pageCount)
    {
        var scaled = ImageCodec.Downscale(bitmap, level.PhotoMaxSide);
        try
        {
            var format = scaled.HasAlpha ? FileFormats.Png : FileFormats.Jpg;
            var name = OutputNames.For(input.Name, format, page, pageCount, Suffix);
            var path = Path.Combine(folder, name);
            using (var stream = File.Create(path))
            {
                var ok = format == FileFormats.Png
                    ? scaled.Compress(Bitmap.CompressFormat.Png!, 100, stream)
                    : scaled.Compress(Bitmap.CompressFormat.Jpeg!, level.PhotoQuality, stream);
                if (!ok)
                    throw new IOException("O Android não conseguiu gravar a imagem comprimida.");
            }
            return KeepSmaller(input, path, name, format, page, pageCount);
        }
        finally
        {
            if (!ReferenceEquals(scaled, bitmap))
                ImageCodec.Release(scaled);
        }
    }

    /// <summary>
    /// Quando o resultado não ficou menor que o original (arquivo já otimizado, HEIC que em JPG cresceria...), entrega
    /// uma cópia do original, no formato dele, marcada como "já estava otimizado". Páginas de um TIFF não se comparam.
    /// </summary>
    private static ConvertedFile KeepSmaller(PickedFile input, string path, string name, FileFormat format, int? page, int pageCount)
    {
        if (pageCount > 1 || new FileInfo(path).Length < input.Size)
            return new ConvertedFile(path, name, format);

        File.Delete(path);
        var originalName = OutputNames.For(input.Name, input.Format, page, pageCount, Suffix);
        var copy = Path.Combine(Path.GetDirectoryName(path)!, originalName);
        File.Copy(input.LocalPath, copy, overwrite: true);
        return new ConvertedFile(copy, originalName, input.Format, NotSmaller: true);
    }

    /// <summary>Abre o PDF para alterar (como o Filee faz: um PDF com senha só de "dono" é copiado página a página).</summary>
    private static PdfDocument OpenForChanges(string path)
    {
        try
        {
            return PdfReader.Open(path, PdfDocumentOpenMode.Modify);
        }
        catch (PdfReaderException ex) when (ex.Message.Contains("owner password", StringComparison.OrdinalIgnoreCase))
        {
            using var source = PdfReader.Open(path, PdfDocumentOpenMode.Import);
            var copy = new PdfDocument();
            foreach (var page in source.Pages)
                copy.AddPage(page);
            return copy;
        }
    }
}

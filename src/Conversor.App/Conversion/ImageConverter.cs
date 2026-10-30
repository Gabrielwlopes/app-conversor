using Conversor.Core;
using Conversor.Core.Tiff;

namespace Conversor.App.Conversion;

/// <summary>Imagem → PNG, JPG, WebP ou TIFF. Um TIFF de várias páginas vira uma imagem por página.</summary>
internal static class ImageConverter
{
    public static IReadOnlyList<ConvertedFile> Convert(PickedFile input, FileFormat target, string folder,
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
                    results.Add(Write(bitmap, input.Name, target, folder, p, pages, cancellationToken));
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
            return [Write(image, input.Name, target, folder, null, 1, cancellationToken)];
        }
        finally
        {
            ImageCodec.Release(image);
            report(1);
        }
    }

    private static ConvertedFile Write(Android.Graphics.Bitmap bitmap, string originalName, FileFormat target,
        string folder, int? page, int pageCount, CancellationToken cancellationToken)
    {
        var name = OutputNames.For(originalName, target, page, pageCount);
        var path = Path.Combine(folder, name);
        ImageCodec.Save(bitmap, target, path, cancellationToken);
        return new ConvertedFile(path, name, target);
    }
}

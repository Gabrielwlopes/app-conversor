using Android.Content;
using Conversor.Core;
using AUri = Android.Net.Uri;

namespace Conversor.App.Conversion;

/// <summary>Copia um arquivo escolhido (ou compartilhado por outro app) para a pasta temporária do app.</summary>
internal static class FileImport
{
    /// <exception cref="NotSupportedException">O arquivo não é PDF nem imagem (a mensagem diz qual).</exception>
    public static PickedFile Import(Context context, AUri uri, string folder)
    {
        var resolver = context.ContentResolver ?? throw new InvalidOperationException("ContentResolver indisponível.");
        var name = DisplayName(resolver, uri) ?? uri.LastPathSegment ?? "arquivo";

        Directory.CreateDirectory(folder);
        var local = Path.Combine(folder, Guid.NewGuid().ToString("N"));
        using (var input = resolver.OpenInputStream(uri) ?? throw new IOException($"Não foi possível abrir \"{name}\"."))
        using (var output = File.Create(local))
            input.CopyTo(output);

        var header = new byte[FileFormats.HeaderLength];
        int read;
        using (var stream = File.OpenRead(local))
            read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);

        var format = FileFormats.Detect(header.AsSpan(0, read));
        if (format is null)
        {
            File.Delete(local);
            throw new NotSupportedException($"\"{name}\" não é um PDF nem uma imagem que o app converte.");
        }
        return new PickedFile(local, name, new FileInfo(local).Length, format);
    }

    private static string? DisplayName(ContentResolver resolver, AUri uri)
    {
        try
        {
            using var cursor = resolver.Query(uri, ["_display_name"], null, null, null);
            if (cursor is not null && cursor.MoveToFirst())
            {
                var column = cursor.GetColumnIndex("_display_name");
                if (column >= 0 && !cursor.IsNull(column))
                    return cursor.GetString(column);
            }
        }
        catch (Exception)
        {
            // Alguns apps compartilham arquivos sem responder a consultas: o nome vem do endereço.
        }
        return null;
    }
}

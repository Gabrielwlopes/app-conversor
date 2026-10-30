using Android.Content;
using Android.Provider;
using AUri = Android.Net.Uri;

namespace Conversor.App.Conversion;

/// <summary>Um arquivo salvo em Downloads/Conversor.</summary>
/// <param name="Uri">Endereço no Android, usado para abrir e compartilhar.</param>
/// <param name="Name">Nome final (o Android acrescenta " (1)" se já existir um igual).</param>
internal sealed record SavedFile(AUri Uri, string Name, string MimeType);

/// <summary>Salva na pasta Downloads/Conversor (Android 10+: não precisa de permissão).</summary>
internal static class Downloads
{
    public const string Folder = "Conversor";

    public static SavedFile Save(Context context, ConvertedFile file)
    {
        var resolver = context.ContentResolver ?? throw new InvalidOperationException("ContentResolver indisponível.");
        var values = new ContentValues();
        values.Put("_display_name", file.Name);
        values.Put("mime_type", file.Format.MimeType);
        values.Put("relative_path", (Android.OS.Environment.DirectoryDownloads ?? "Download") + "/" + Folder);
        values.Put("is_pending", 1); // outros apps só veem o arquivo quando estiver completo

        var collection = MediaStore.Downloads.ExternalContentUri ?? throw new IOException("A pasta Downloads não está disponível.");
        var uri = resolver.Insert(collection, values) ?? throw new IOException("Não foi possível criar o arquivo em Downloads.");
        try
        {
            using (var output = resolver.OpenOutputStream(uri) ?? throw new IOException("Não foi possível gravar em Downloads."))
            using (var input = File.OpenRead(file.LocalPath))
                input.CopyTo(output);

            var done = new ContentValues();
            done.Put("is_pending", 0);
            resolver.Update(uri, done, null, null);
        }
        catch
        {
            resolver.Delete(uri, null, null);
            throw;
        }
        return new SavedFile(uri, FinalName(resolver, uri) ?? file.Name, file.Format.MimeType);
    }

    private static string? FinalName(ContentResolver resolver, AUri uri)
    {
        try
        {
            using var cursor = resolver.Query(uri, ["_display_name"], null, null, null);
            return cursor is not null && cursor.MoveToFirst() ? cursor.GetString(0) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

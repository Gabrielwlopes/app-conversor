namespace Conversor.Core;

/// <summary>Grande grupo de um formato: decide quais conversões fazem sentido.</summary>
public enum FormatFamily
{
    Other,
    Pdf,
    Image,
}

/// <summary>Um formato de arquivo que o app lê ou grava.</summary>
/// <param name="Id">Identificador curto (o mesmo que o Filee usa: "pdf", "jpg", "docx", ...).</param>
/// <param name="Label">Nome mostrado na tela.</param>
/// <param name="Extension">Extensão sem ponto usada ao salvar.</param>
/// <param name="MimeType">Tipo MIME usado ao salvar, abrir e compartilhar.</param>
public sealed record FileFormat(string Id, string Label, string Extension, string MimeType, FormatFamily Family);

/// <summary>Os formatos conhecidos e a detecção pelo conteúdo do arquivo.</summary>
public static class FileFormats
{
    public static readonly FileFormat Pdf = new("pdf", "PDF", "pdf", "application/pdf", FormatFamily.Pdf);
    public static readonly FileFormat Jpg = new("jpg", "JPG", "jpg", "image/jpeg", FormatFamily.Image);
    public static readonly FileFormat Png = new("png", "PNG", "png", "image/png", FormatFamily.Image);
    public static readonly FileFormat Webp = new("webp", "WebP", "webp", "image/webp", FormatFamily.Image);
    public static readonly FileFormat Gif = new("gif", "GIF", "gif", "image/gif", FormatFamily.Image);
    public static readonly FileFormat Bmp = new("bmp", "BMP", "bmp", "image/bmp", FormatFamily.Image);
    public static readonly FileFormat Heic = new("heic", "HEIC", "heic", "image/heic", FormatFamily.Image);
    public static readonly FileFormat Avif = new("avif", "AVIF", "avif", "image/avif", FormatFamily.Image);
    public static readonly FileFormat Tiff = new("tiff", "TIFF", "tiff", "image/tiff", FormatFamily.Image);

    public static readonly FileFormat Docx = new("docx", "DOCX", "docx",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document", FormatFamily.Other);

    public static readonly FileFormat Txt = new("txt", "TXT", "txt", "text/plain", FormatFamily.Other);

    /// <summary>Quantos bytes do começo do arquivo <see cref="Detect"/> precisa ver.</summary>
    public const int HeaderLength = 1024;

    private static readonly FileFormat[] All = [Pdf, Jpg, Png, Webp, Gif, Bmp, Heic, Avif, Tiff, Docx, Txt];

    /// <summary>Procura um formato pelo id ("jpg", "tiff", ...).</summary>
    public static FileFormat? ById(string id) =>
        All.FirstOrDefault(f => string.Equals(f.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Reconhece o formato pelos primeiros bytes (assinatura), que valem mais que a extensão ou o MIME informado por
    /// outro app: um "foto.jpg" que na verdade é HEIC é tratado como HEIC.
    /// </summary>
    /// <returns>O formato, ou null quando o arquivo não é um que o app converte.</returns>
    public static FileFormat? Detect(ReadOnlySpan<byte> header)
    {
        if (StartsWith(header, [0xFF, 0xD8, 0xFF]))
            return Jpg;
        if (StartsWith(header, [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]))
            return Png;
        if (StartsWith(header, "GIF87a"u8) || StartsWith(header, "GIF89a"u8))
            return Gif;
        if (header.Length >= 12 && StartsWith(header, "RIFF"u8) && header[8..12].SequenceEqual("WEBP"u8))
            return Webp;
        if (StartsWith(header, "II*\0"u8) || StartsWith(header, "MM\0*"u8))
            return Tiff;
        if (header.Length >= 16 && StartsWith(header, "BM"u8) && IsBmpHeader(header))
            return Bmp;
        if (IsoBrand(header) is { } brand)
            return brand;
        // Um PDF pode ter alguns bytes de lixo antes de "%PDF-"; os leitores aceitam isso dentro do primeiro kilobyte.
        if (header[..Math.Min(header.Length, HeaderLength)].IndexOf("%PDF-"u8) >= 0)
            return Pdf;
        return null;
    }

    /// <summary>Formato pela extensão do nome (usado só para escolher um nome de saída quando falta a assinatura).</summary>
    public static FileFormat? FromFileName(string? fileName)
    {
        var extension = Path.GetExtension(fileName ?? "").TrimStart('.').ToLowerInvariant();
        return extension switch
        {
            "jpeg" or "jpe" or "jfif" => Jpg,
            "tif" => Tiff,
            "heif" => Heic,
            _ => All.FirstOrDefault(f => f.Extension == extension),
        };
    }

    private static bool StartsWith(ReadOnlySpan<byte> data, ReadOnlySpan<byte> prefix) =>
        data.Length >= prefix.Length && data[..prefix.Length].SequenceEqual(prefix);

    /// <summary>"BM" sozinho é comum em texto; confere também o tamanho do cabeçalho DIB.</summary>
    private static bool IsBmpHeader(ReadOnlySpan<byte> header)
    {
        var dibSize = BitConverter.ToUInt32(header[14..18]);
        return dibSize is 12 or 40 or 52 or 56 or 64 or 108 or 124;
    }

    /// <summary>HEIC e AVIF são arquivos ISO BMFF: uma caixa "ftyp" com a marca principal e as compatíveis.</summary>
    private static FileFormat? IsoBrand(ReadOnlySpan<byte> header)
    {
        if (header.Length < 16 || !header[4..8].SequenceEqual("ftyp"u8))
            return null;
        var boxSize = (int)Math.Min((uint)(header[0] << 24 | header[1] << 16 | header[2] << 8 | header[3]), (uint)header.Length);
        var brands = new List<string> { System.Text.Encoding.ASCII.GetString(header[8..12]) };
        for (var i = 16; i + 4 <= boxSize; i += 4)
            brands.Add(System.Text.Encoding.ASCII.GetString(header[i..(i + 4)]));

        // AVIF primeiro: um AVIF costuma listar também a marca genérica "mif1", que o HEIC usa.
        if (brands.Any(b => b is "avif" or "avis"))
            return Avif;
        if (brands.Any(b => b is "heic" or "heix" or "hevc" or "hevx" or "heim" or "heis" or "mif1" or "msf1"))
            return Heic;
        return null;
    }
}

using System.Text;

namespace Conversor.Core;

/// <summary>Nomes dos arquivos gravados.</summary>
public static class OutputNames
{
    private const int MaxBaseLength = 80;

    /// <summary>
    /// O nome do arquivo original sem a extensão, sem caracteres que o Android não aceita em nomes de arquivo.
    /// </summary>
    public static string BaseName(string? originalName)
    {
        var name = originalName ?? "";
        var dot = name.LastIndexOf('.');
        // Tira a extensão ("foto.jpeg"), mas não um pedaço do nome ("Relatório v1.2 final").
        if (dot >= 0 && name.Length - dot - 1 is >= 1 and <= 5 && name[(dot + 1)..].All(char.IsAsciiLetterOrDigit))
            name = name[..dot];
        var clean = new StringBuilder(name.Length);
        foreach (var c in name)
            clean.Append(c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' || char.IsControl(c) ? '_' : c);
        var result = clean.ToString().Trim().Trim('.');
        if (result.Length > MaxBaseLength)
            result = result[..MaxBaseLength].TrimEnd();
        return result.Length == 0 ? "arquivo" : result;
    }

    /// <summary>
    /// Nome de saída: <c>foto.png</c>, ou <c>documento_p01.png</c> quando uma entrada vira várias páginas.
    /// </summary>
    /// <param name="page">Índice da página a partir de 0, ou null quando a entrada vira um arquivo só.</param>
    /// <param name="pageCount">Total de páginas (define quantos dígitos o número tem).</param>
    public static string For(string? originalName, FileFormat target, int? page = null, int pageCount = 1)
    {
        var name = BaseName(originalName);
        if (page is { } index && pageCount > 1)
        {
            var digits = Math.Max(2, pageCount.ToString(System.Globalization.CultureInfo.InvariantCulture).Length);
            name += "_p" + (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(digits, '0');
        }
        return $"{name}.{target.Extension}";
    }
}

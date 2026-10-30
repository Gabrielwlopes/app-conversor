using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Office;
using Filee.Engines.Pdf;

namespace Conversor.App.Conversion;

/// <summary>
/// O motor do Filee (external/Filee), usado sem mudanças: o mesmo código que converte PDF em Word e em texto no
/// Filee de computador. Ele lê o PDF com o PdfPig, reconstrói parágrafos, títulos, tabelas e imagens na ordem de
/// leitura, e grava o DOCX com o gerador próprio do Filee, sem Word nem LibreOffice.
/// </summary>
internal static class FileeEngine
{
    /// <summary>Converte um PDF em <paramref name="target"/> ("docx" ou "txt").</summary>
    /// <returns>O caminho do arquivo gravado.</returns>
    public static async Task<string> ConvertPdfAsync(string pdfPath, string target, string workFolder,
        IProgress<double> progress, CancellationToken cancellationToken)
    {
        IConverter converter = target switch
        {
            "docx" => new DocxWriterConverter(),
            "txt" => new PdfTextConverter(),
            _ => throw new NotSupportedException($"O Filee não faz PDF → {target.ToUpperInvariant()} aqui."),
        };

        Directory.CreateDirectory(workFolder);
        var output = Path.Combine(workFolder, "saida." + target);
        // Preset padrão do Filee: sem OCR (reconhecimento de texto em imagem não existe no Android).
        var step = new ConversionStep(pdfPath, "pdf", target, new Preset(), new OneOutput(output), workFolder);
        var files = await converter.ConvertAsync(step, progress, cancellationToken).ConfigureAwait(false);
        return files.Count > 0 && File.Exists(files[0])
            ? files[0]
            : throw new IOException("O Filee terminou sem gravar o arquivo.");
    }

    /// <summary>Entrega sempre o mesmo caminho de saída ao Filee.</summary>
    private sealed class OneOutput(string path) : IOutputAllocator
    {
        public string? Allocate(string extension, string? suffix = null) => path;
    }
}

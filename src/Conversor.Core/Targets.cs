namespace Conversor.Core;

/// <summary>Um formato de destino oferecido na tela.</summary>
/// <param name="Format">O formato gravado.</param>
/// <param name="Description">Explicação curta embaixo do nome ("Word", "1 imagem por página", ...).</param>
public sealed record TargetOption(FileFormat Format, string Description);

/// <summary>Quais formatos de destino fazem sentido para os arquivos escolhidos.</summary>
public static class Targets
{
    /// <summary>
    /// Os destinos possíveis para todos os <paramref name="sources"/> de uma vez. PDFs e imagens misturados não têm
    /// destino em comum, então a lista volta vazia e a tela pede para escolher só um tipo.
    /// </summary>
    public static IReadOnlyList<TargetOption> For(IReadOnlyCollection<FileFormat> sources)
    {
        if (sources.Count == 0)
            return [];

        if (sources.All(s => s.Family == FormatFamily.Pdf))
        {
            var pages = sources.Count == 1 ? "1 imagem por página" : "1 imagem por página de cada PDF";
            return
            [
                new(FileFormats.Docx, "Word (editável)"),
                new(FileFormats.Txt, "Só o texto"),
                new(FileFormats.Jpg, pages),
                new(FileFormats.Png, pages),
            ];
        }

        if (sources.All(s => s.Family == FormatFamily.Image))
        {
            // Converter JPG em JPG não muda nada: o formato de todos os arquivos (se for um só) sai da lista.
            var same = sources.Select(s => s.Id).Distinct().Count() == 1 ? sources.First().Id : null;
            List<TargetOption> options =
            [
                new(FileFormats.Png, "Sem perdas, com transparência"),
                new(FileFormats.Jpg, "Menor, para fotos"),
                new(FileFormats.Webp, "Leve, para web"),
                new(FileFormats.Tiff, "Sem perdas, para impressão"),
                new(FileFormats.Pdf, sources.Count == 1 ? "Documento" : "Um PDF com todas"),
            ];
            return options.Where(o => o.Format.Id != same).ToList();
        }

        return [];
    }
}

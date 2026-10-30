namespace Conversor.Core;

/// <summary>Uma opção de destino na tela: converter para um formato, ou comprimir.</summary>
/// <param name="Format">
/// O formato gravado. Ao comprimir: PDF para PDFs; para imagens, JPG (ou PNG, quando a imagem tem transparência).
/// </param>
/// <param name="Description">Explicação curta embaixo do nome ("Word", "1 imagem por página", ...).</param>
/// <param name="Compress">Comprimir em vez de converter.</param>
public sealed record TargetOption(FileFormat Format, string Description, bool Compress = false)
{
    /// <summary>Nome do botão.</summary>
    public string Label => Compress ? "Comprimir" : Format.Label;

    /// <summary>Identifica a opção entre uma atualização da tela e outra (a descrição pode mudar).</summary>
    public string Key => Compress ? "comprimir" : Format.Id;
}

/// <summary>Quais destinos fazem sentido para os arquivos escolhidos.</summary>
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
                new(FileFormats.Pdf, "PDF menor, mesmas páginas", Compress: true),
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
            options.RemoveAll(o => o.Format.Id == same);
            options.Add(new(FileFormats.Jpg, sources.Count == 1 ? "Foto menor, mesma imagem" : "Fotos menores, mesmas imagens",
                Compress: true));
            return options;
        }

        return [];
    }
}

using Conversor.Core;

namespace Conversor.App.Conversion;

/// <summary>Converte os arquivos escolhidos, um por um, para o formato de destino.</summary>
internal static class ConversionJob
{
    /// <summary>
    /// Roda fora da tela principal. Um arquivo com problema não impede os outros: o erro dele entra na lista de
    /// erros do resultado. Só o cancelamento interrompe tudo.
    /// </summary>
    public static async Task<JobResult> RunAsync(IReadOnlyList<PickedFile> inputs, FileFormat target, string workFolder,
        IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        var outputs = new List<ConvertedFile>();
        var errors = new List<string>();

        if (target == FileFormats.Pdf)
        {
            // Imagens → PDF: todas juntas num documento só, na ordem escolhida.
            var name = inputs.Count == 1
                ? OutputNames.For(inputs[0].Name, FileFormats.Pdf)
                : $"Imagens {DateTime.Now:yyyy-MM-dd HH'h'mm}.pdf";
            try
            {
                outputs.Add(PdfBuilder.FromImages(inputs, Path.Combine(workFolder, "pdf"), name,
                    (fraction, message) => progress.Report(new JobProgress(fraction, message)), cancellationToken));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                errors.Add(Errors.Describe(e));
            }
            return new JobResult(outputs, errors);
        }

        for (var i = 0; i < inputs.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var input = inputs[i];
            var label = inputs.Count > 1 ? $"{input.Name} ({i + 1} de {inputs.Count})" : input.Name;
            var index = i;
            void Report(double fraction) =>
                progress.Report(new JobProgress((index + Math.Clamp(fraction, 0, 1)) / inputs.Count, "Convertendo " + label));
            Report(0);

            try
            {
                var folder = Path.Combine(workFolder, Guid.NewGuid().ToString("N"));
                outputs.AddRange(await ConvertOneAsync(input, target, folder, Report, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                errors.Add($"{input.Name}: {Errors.Describe(e)}");
            }
        }
        return new JobResult(outputs, errors);
    }

    private static async Task<IReadOnlyList<ConvertedFile>> ConvertOneAsync(PickedFile input, FileFormat target,
        string folder, Action<double> report, CancellationToken cancellationToken)
    {
        switch (input.Format.Family, target.Id)
        {
            case (FormatFamily.Pdf, "docx" or "txt"):
                var path = await FileeEngine.ConvertPdfAsync(input.LocalPath, target.Id, folder,
                    new ActionProgress(report), cancellationToken).ConfigureAwait(false);
                return [new ConvertedFile(path, OutputNames.For(input.Name, target), target)];

            case (FormatFamily.Pdf, "jpg" or "png"):
                return PdfPages.Render(input, target, folder, report, cancellationToken);

            case (FormatFamily.Image, "png" or "jpg" or "webp" or "tiff"):
                return ImageConverter.Convert(input, target, folder, report, cancellationToken);

            default:
                throw new NotSupportedException($"{input.Format.Label} → {target.Label} não é suportado.");
        }
    }
}

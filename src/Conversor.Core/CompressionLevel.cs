namespace Conversor.Core;

/// <summary>Quanto comprimir: quanto menor o arquivo, menos detalhe nas imagens.</summary>
/// <param name="Id">Identificador ("leve", "media", "forte").</param>
/// <param name="Label">Nome na tela.</param>
/// <param name="Description">Para que serve, mostrado embaixo das opções.</param>
/// <param name="PdfDpi">Resolução máxima das imagens dentro de um PDF (as mesmas do Filee: 300, 150 e 72).</param>
/// <param name="PdfQuality">Qualidade JPEG das imagens regravadas dentro do PDF.</param>
/// <param name="PhotoMaxSide">Lado maior máximo de uma foto, em pixels.</param>
/// <param name="PhotoQuality">Qualidade JPEG da foto comprimida.</param>
public sealed record CompressionLevel(
    string Id, string Label, string Description, int PdfDpi, int PdfQuality, int PhotoMaxSide, int PhotoQuality)
{
    public static readonly CompressionLevel Light = new("leve", "Leve",
        "Quase sem perda de qualidade. Bom para imprimir.", 300, 85, 4096, 85);

    public static readonly CompressionLevel Balanced = new("media", "Média",
        "Bem menor e ainda nítido. Bom para e-mail, WhatsApp e sites.", 150, 65, 2560, 75);

    public static readonly CompressionLevel Strong = new("forte", "Forte",
        "O menor possível. Bom para ler na tela.", 72, 55, 1600, 60);

    public static IReadOnlyList<CompressionLevel> All { get; } = [Light, Balanced, Strong];
}

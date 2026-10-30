using Conversor.Core;

namespace Conversor.App.Conversion;

/// <summary>Um arquivo escolhido pelo usuário, já copiado para a pasta temporária do app.</summary>
/// <param name="LocalPath">Cópia local (o app nunca altera o original).</param>
/// <param name="Name">Nome original, mostrado na tela e usado para nomear a saída.</param>
/// <param name="Format">Formato reconhecido pelo conteúdo do arquivo.</param>
internal sealed record PickedFile(string LocalPath, string Name, long Size, FileFormat Format);

/// <summary>Um arquivo convertido, ainda na pasta temporária.</summary>
/// <param name="Name">Nome com que vai ser salvo em Downloads.</param>
/// <param name="NotSmaller">Ao comprimir: o arquivo já estava otimizado, então a saída é uma cópia do original.</param>
internal sealed record ConvertedFile(string LocalPath, string Name, FileFormat Format, bool NotSmaller = false);

/// <summary>Andamento: fração de 0 a 1 e o texto mostrado embaixo da barra.</summary>
internal sealed record JobProgress(double Fraction, string Message);

/// <summary>O que uma conversão produziu, e o que deu errado (uma linha por arquivo).</summary>
internal sealed record JobResult(IReadOnlyList<ConvertedFile> Files, IReadOnlyList<string> Errors);

/// <summary>Repassa o andamento de uma etapa (0 a 1) para quem a chamou.</summary>
internal sealed class ActionProgress(Action<double> report) : IProgress<double>
{
    public void Report(double value) => report(Math.Clamp(value, 0, 1));
}

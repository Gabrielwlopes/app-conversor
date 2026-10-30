namespace Conversor.App.Conversion;

/// <summary>Transforma erros técnicos em frases que o usuário entende.</summary>
internal static class Errors
{
    public static string Describe(Exception error)
    {
        var message = error.Message ?? "";
        if (message.Contains("password", StringComparison.OrdinalIgnoreCase)
            || error is Java.Lang.SecurityException)
            return "O PDF tem senha. Abra com a senha, salve uma cópia sem senha e converta a cópia.";
        if (message.Contains("contains no text", StringComparison.OrdinalIgnoreCase))
            return "Este PDF não tem texto (parece ser digitalizado). Converta para Word, que mantém as páginas como imagem, ou para JPG/PNG.";
        if (error is OutOfMemoryException or Java.Lang.OutOfMemoryError
            || error.InnerException is OutOfMemoryException or Java.Lang.OutOfMemoryError)
            return "O arquivo é grande demais para a memória do celular.";
        if (error is DllNotFoundException or EntryPointNotFoundException or TypeInitializationException)
            return "Uma parte do conversor não carregou neste celular. (" + message + ")";
        if (error is NotSupportedException or IOException)
            return message;
        if (error is Java.Lang.Throwable)
            return "O Android recusou o arquivo: " + message;
        return "Erro inesperado: " + message;
    }
}

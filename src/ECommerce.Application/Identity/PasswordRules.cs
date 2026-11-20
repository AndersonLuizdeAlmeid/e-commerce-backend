using FluentValidation;

namespace ECommerce.Application.Identity;

/// <summary>
/// Regra de senha (entrega 6, S4; ASVS V2.1): de 12 a 128 caracteres, sem exigir maiúscula, número ou símbolo, e
/// recusando as senhas comuns. Uma frase longa ("minha joia preferida da loja") é mais forte e mais fácil de lembrar
/// do que "Senha@123".
/// </summary>
internal static class PasswordRules
{
    public const int MinLength = 12;
    public const int MaxLength = 128;

    public const string LengthMessage = "A senha precisa ter pelo menos 12 caracteres. Uma frase funciona bem.";
    public const string CommonMessage = "Essa senha é muito comum. Escolha outra, de preferência uma frase.";

    public static IRuleBuilderOptions<T, string> StrongPassword<T>(this IRuleBuilder<T, string> rule) =>
        rule
            .NotEmpty().WithMessage("Informe a senha.")
            .MinimumLength(MinLength).WithMessage(LengthMessage)
            .MaximumLength(MaxLength).WithMessage($"A senha pode ter no máximo {MaxLength} caracteres.")
            .Must(password => !IsCommon(password)).WithMessage(CommonMessage);

    // Palavras que, sozinhas e enfeitadas com números ou símbolos ("senha12345678", "Password@2026"), formam as senhas
    // mais tentadas nos vazamentos. Comparadas sem acento, maiúscula, número nem símbolo.
    private static readonly HashSet<string> CommonWords = new(StringComparer.Ordinal)
    {
        "", "senha", "minhasenha", "senhasegura", "novasenha", "password", "mypassword", "passw", "pass", "qwerty",
        "qwertyuiop", "asdfgh", "asdfghjkl", "zxcvbnm", "abc", "abcdef", "abcdefgh", "admin", "administrador", "root",
        "login", "teste", "test", "usuario", "user", "brasil", "loja", "mo", "iloveyou", "teamo", "amor", "deus",
        "jesus", "flamengo", "corinthians", "palmeiras", "gremio", "internacional", "abcd", "aaaa", "welcome",
        "bemvindo", "letmein", "dragon", "monkey", "princesa", "estrela", "familia", "felicidade",
    };

    private const string Sequences = "abcdefghijklmnopqrstuvwxyz qwertyuiopasdfghjklzxcvbnm 01234567890 09876543210";

    /// <summary>Senha montada só com palavra comum, sequência do teclado ou um caractere repetido.</summary>
    public static bool IsCommon(string? password)
    {
        if (string.IsNullOrEmpty(password))
        {
            return false;
        }

        var normalized = Normalize(password);
        if (normalized.Distinct().Count() <= 2)
        {
            return true;
        }

        if (normalized.Length >= 6 && Sequences.Contains(normalized, StringComparison.Ordinal))
        {
            return true;
        }

        var letters = new string(normalized.Where(char.IsLetter).ToArray());
        return CommonWords.Contains(letters) || CommonWords.Contains(Reverse(letters));
    }

    private static string Normalize(string password)
    {
        var decomposed = password.ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD);
        return new string(decomposed
            .Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
            .Where(char.IsLetterOrDigit)
            .ToArray());
    }

    private static string Reverse(string value)
    {
        var chars = value.ToCharArray();
        Array.Reverse(chars);
        return new string(chars);
    }
}

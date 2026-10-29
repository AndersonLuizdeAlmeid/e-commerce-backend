using FluentValidation;

namespace ECommerce.Application.Identity;

/// <summary>RF-001 CA3 / arquivo 08 §3: 8 a 128 caracteres, com maiúscula, minúscula e número.</summary>
internal static class PasswordRules
{
    public const int MinLength = 8;
    public const int MaxLength = 128;

    public static IRuleBuilderOptions<T, string> StrongPassword<T>(this IRuleBuilder<T, string> rule) =>
        rule
            .NotEmpty().WithMessage("Informe a senha.")
            .Length(MinLength, MaxLength).WithMessage($"A senha deve ter entre {MinLength} e {MaxLength} caracteres.")
            .Must(password => password is not null && password.Any(char.IsUpper) && password.Any(char.IsLower) && password.Any(char.IsDigit))
            .WithMessage("A senha precisa ter letra maiúscula, letra minúscula e número.");
}

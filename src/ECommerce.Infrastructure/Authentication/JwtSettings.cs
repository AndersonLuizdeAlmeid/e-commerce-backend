using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace ECommerce.Infrastructure.Authentication;

/// <summary>Seção <c>Jwt</c> (arquivo 08 §6). A chave vem de variável de ambiente/secret, nunca do appsettings de produção.</summary>
public sealed class JwtSettings
{
    public const string SectionName = "Jwt";

    /// <summary>HS256 exige chave de no mínimo 256 bits.</summary>
    public const int MinimumKeyBytes = 32;

    /// <summary>Chave pública de desenvolvimento (appsettings.Development.json); recusada fora de Development.</summary>
    public const string DevelopmentKeyMarker = "DEV-ONLY";

    /// <summary>Claim com o security stamp do usuário (arquivo 08 §2).</summary>
    public const string SecurityStampClaim = "sstamp";

    public string Issuer { get; init; } = "ecommerce-api";
    public string Audience { get; init; } = "ecommerce-web";
    public string SigningKey { get; init; } = string.Empty;
    public int AccessTokenMinutes { get; init; } = 15;
    public int RefreshTokenDays { get; init; } = 7;
    public int RefreshTokenAbsoluteDays { get; init; } = 30;

    public SymmetricSecurityKey CreateSigningKey() => new(Encoding.UTF8.GetBytes(SigningKey));
}

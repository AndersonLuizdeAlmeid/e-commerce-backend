using System.Security.Claims;
using ECommerce.Application.Abstractions.Authentication;
using ECommerce.Application.Abstractions.Messaging;
using ECommerce.Application.Identity.GetSecurityStamp;
using ECommerce.Domain.Identity;
using ECommerce.Infrastructure.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace ECommerce.Api.Extensions;

internal static class Policies
{
    public const string Customer = "Customer";
    public const string Admin = "Admin";
}

/// <summary>JWT bearer (arquivo 08) + policies com fallback autenticado (arquivo 09 §2).</summary>
internal static class AuthExtensions
{
    public const string RoleClaim = "role";

    public static IServiceCollection AddApiAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        var jwt = configuration.GetSection(JwtSettings.SectionName).Get<JwtSettings>() ?? new JwtSettings();

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = jwt.Issuer,
                    ValidateAudience = true,
                    ValidAudience = jwt.Audience,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = jwt.CreateSigningKey(),
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = JwtRegisteredClaimNames.Sub,
                    RoleClaimType = RoleClaim,
                };
            });

        services.AddAuthorizationBuilder()
            // Seguro por padrão: endpoint público precisa declarar AllowAnonymous.
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
            .AddPolicy(Policies.Customer, policy => policy.RequireRole(nameof(Role.Customer)))
            .AddPolicy(Policies.Admin, policy => policy
                .RequireRole(nameof(Role.Admin))
                .AddRequirements(new ActiveSecurityStampRequirement()));

        services.AddScoped<IAuthorizationHandler, ActiveSecurityStampHandler>();
        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, HttpCurrentUser>();

        return services;
    }
}

/// <summary>Token só vale se o security stamp ainda for o atual (troca de senha, rebaixamento, desativação).</summary>
internal sealed class ActiveSecurityStampRequirement : IAuthorizationRequirement;

internal sealed class ActiveSecurityStampHandler(IQueryHandler<GetSecurityStampQuery, Guid?> stamps)
    : AuthorizationHandler<ActiveSecurityStampRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context, ActiveSecurityStampRequirement requirement)
    {
        var sub = context.User.FindFirstValue(JwtRegisteredClaimNames.Sub);
        var tokenStamp = context.User.FindFirstValue(JwtSettings.SecurityStampClaim);
        if (!Guid.TryParse(sub, out var userId) || !Guid.TryParse(tokenStamp, out var stamp))
        {
            return;
        }

        var current = await stamps.Handle(new GetSecurityStampQuery(new UserId(userId)), CancellationToken.None);
        if (current.IsSuccess && current.Value == stamp)
        {
            context.Succeed(requirement);
        }
    }
}

internal sealed class HttpCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;

    public UserId UserId =>
        Guid.TryParse(Principal?.FindFirstValue(JwtRegisteredClaimNames.Sub), out var id)
            ? new UserId(id)
            : throw new InvalidOperationException("Não há usuário autenticado neste request.");

    public string? Role => Principal?.FindFirstValue(AuthExtensions.RoleClaim);
}

using ECommerce.Api.Extensions;
using ECommerce.Application.Abstractions.Authentication;
using ECommerce.Application.Abstractions.Messaging;
using ECommerce.Application.Customers;
using Microsoft.AspNetCore.Http.HttpResults;

namespace ECommerce.Api.Endpoints.Customers;

internal sealed record UpdateProfileRequest(string FullName, string Phone, string? Cpf);

/// <summary><c>/api/v1/me</c> — dados do próprio cliente (arquivo 07 §3). Endereços entram na Fase 4.</summary>
internal sealed class MeEndpoints : IEndpointGroup
{
    public void Map(IEndpointRouteBuilder app)
    {
        // Policy no grupo: impossível esquecer em um endpoint novo (arquivo 09 §2).
        var group = app.MapGroup("/me").WithTags("Me").RequireAuthorization(Policies.Customer);

        group.MapGet("/profile", GetProfile)
            .WithName("GetProfile").WithSummary("Perfil do cliente (RF-005)");

        group.MapPut("/profile", UpdateProfile)
            .WithName("UpdateProfile").WithSummary("Atualiza nome, telefone e CPF (RF-005)")
            .WithDescription("`cpf` vazio mantém o CPF atual.")
            .RequireRateLimiting(RateLimitPolicies.CustomerWrite);
    }

    private static async Task<Results<Ok<ProfileResponse>, ProblemHttpResult>> GetProfile(
        ICurrentUser currentUser,
        IQueryHandler<GetProfileQuery, ProfileResponse> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new GetProfileQuery(currentUser.UserId), cancellationToken);
        return result.ToHttpResult();
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> UpdateProfile(
        UpdateProfileRequest request,
        ICurrentUser currentUser,
        ICommandHandler<UpdateProfileCommand> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(
            new UpdateProfileCommand(currentUser.UserId, request.FullName, request.Phone, request.Cpf), cancellationToken);
        return result.IsSuccess ? TypedResults.NoContent() : result.Error.ToProblem();
    }
}

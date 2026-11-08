using ECommerce.Api.Extensions;
using ECommerce.Application.Abstractions.Authentication;
using ECommerce.Application.Abstractions.Messaging;
using ECommerce.Application.Customers;
using Microsoft.AspNetCore.Http.HttpResults;

namespace ECommerce.Api.Endpoints.Customers;

internal sealed record UpdateProfileRequest(string FullName, string Phone, string? Cpf);

internal sealed record AddressRequest(
    string? Recipient,
    string? PostalCode,
    string? Street,
    string? Number,
    string? Complement,
    string? District,
    string? City,
    string? State,
    bool IsDefault = false);

/// <summary><c>/api/v1/me</c> — dados do próprio cliente (arquivo 07 §3): perfil e endereços.</summary>
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

        var addresses = group.MapGroup("/addresses");

        addresses.MapGet("/", ListAddresses)
            .WithName("ListAddresses").WithSummary("Endereços do cliente, o padrão primeiro (RF-007)");
        addresses.MapPost("/", CreateAddress)
            .WithName("CreateAddress").WithSummary("Guarda um endereço (RF-007)")
            .WithDescription(
                "O primeiro endereço já nasce padrão; `isDefault` torna o novo o padrão. A UF precisa conferir com o CEP. " +
                "Até 10 endereços (422 depois disso).")
            .RequireRateLimiting(RateLimitPolicies.CustomerWrite);
        addresses.MapPut("/{id:guid}", UpdateAddress)
            .WithName("UpdateAddress").WithSummary("Corrige um endereço (RF-007)")
            .WithDescription("Endereço de outro cliente → 404 (CA1). Pedidos já feitos não mudam: eles guardam uma cópia.")
            .RequireRateLimiting(RateLimitPolicies.CustomerWrite);
        addresses.MapDelete("/{id:guid}", DeleteAddress)
            .WithName("DeleteAddress").WithSummary("Apaga um endereço (RF-007)")
            .WithDescription("Apagar o padrão passa o posto para o endereço mais recente.")
            .RequireRateLimiting(RateLimitPolicies.CustomerWrite);
        addresses.MapPut("/{id:guid}/default", SetDefaultAddress)
            .WithName("SetDefaultAddress").WithSummary("Torna o endereço o padrão (RF-007)")
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

    private static async Task<Results<Ok<IReadOnlyList<AddressResponse>>, ProblemHttpResult>> ListAddresses(
        ICurrentUser currentUser,
        IQueryHandler<ListAddressesQuery, IReadOnlyList<AddressResponse>> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new ListAddressesQuery(currentUser.UserId), cancellationToken);
        return result.ToHttpResult();
    }

    private static async Task<Results<Created<AddressResponse>, ProblemHttpResult>> CreateAddress(
        AddressRequest request,
        ICurrentUser currentUser,
        ICommandHandler<CreateAddressCommand, AddressResponse> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(
            new CreateAddressCommand(
                currentUser.UserId,
                request.Recipient,
                request.PostalCode,
                request.Street,
                request.Number,
                request.Complement,
                request.District,
                request.City,
                request.State,
                request.IsDefault),
            cancellationToken);
        return result.IsSuccess
            ? TypedResults.Created($"/api/v1/me/addresses/{result.Value.Id}", result.Value)
            : result.Error.ToProblem();
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> UpdateAddress(
        Guid id,
        AddressRequest request,
        ICurrentUser currentUser,
        ICommandHandler<UpdateAddressCommand> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(
            new UpdateAddressCommand(
                currentUser.UserId,
                id,
                request.Recipient,
                request.PostalCode,
                request.Street,
                request.Number,
                request.Complement,
                request.District,
                request.City,
                request.State),
            cancellationToken);
        return result.IsSuccess ? TypedResults.NoContent() : result.Error.ToProblem();
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAddress(
        Guid id, ICurrentUser currentUser, ICommandHandler<DeleteAddressCommand> handler, CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new DeleteAddressCommand(currentUser.UserId, id), cancellationToken);
        return result.IsSuccess ? TypedResults.NoContent() : result.Error.ToProblem();
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> SetDefaultAddress(
        Guid id, ICurrentUser currentUser, ICommandHandler<SetDefaultAddressCommand> handler, CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new SetDefaultAddressCommand(currentUser.UserId, id), cancellationToken);
        return result.IsSuccess ? TypedResults.NoContent() : result.Error.ToProblem();
    }
}

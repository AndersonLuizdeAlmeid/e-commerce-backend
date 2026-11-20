using ECommerce.Api.Endpoints.Identity;
using ECommerce.Api.Extensions;
using ECommerce.Application.Abstractions.Authentication;
using ECommerce.Application.Abstractions.Caching;
using ECommerce.Application.Abstractions.Messaging;
using ECommerce.Application.Customers;
using ECommerce.Application.Identity.Privacy;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace ECommerce.Api.Endpoints.Customers;

internal sealed record UpdateProfileRequest(string FullName, string Phone, string? Cpf);

internal sealed record DeleteAccountRequest(string? Password);

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

        group.MapGet("/data-export", ExportData)
            .WithName("ExportMyData").WithSummary("Baixa todos os dados da conta em JSON (LGPD)")
            .WithDescription("Perfil, endereços e pedidos com itens, situação e pagamentos, só da própria conta. Até 3 por dia (429 depois).")
            .RequireRateLimiting(RateLimitPolicies.DataExport);

        group.MapDelete(string.Empty, DeleteAccount)
            .WithName("DeleteMyAccount").WithSummary("Exclui a conta (LGPD): apaga os dados pessoais e mantém os pedidos sem eles")
            .WithDescription(
                "Pede a senha atual (400 `Identity.WrongCurrentPassword` se não conferir). Pedido entre aguardando pagamento e enviado → " +
                "422 `Identity.AccountHasOpenOrders`; estorno em andamento → 422 `Identity.AccountHasPendingRefund`. Sucesso: 204, " +
                "todas as sessões encerradas e o cookie de sessão apagado.")
            .RequireRateLimiting(RateLimitPolicies.AuthStrict);

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

    private static async Task<Results<Ok<DataExportResponse>, ProblemHttpResult>> ExportData(
        HttpContext context,
        ICurrentUser currentUser,
        IQueryHandler<ExportMyDataQuery, DataExportResponse> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new ExportMyDataQuery(currentUser.UserId), cancellationToken);
        if (result.IsSuccess)
        {
            context.Response.Headers.ContentDisposition = "attachment; filename=\"meus-dados.json\"";
        }

        return result.ToHttpResult();
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAccount(
        [FromBody] DeleteAccountRequest request,
        HttpContext context,
        ICurrentUser currentUser,
        ICommandHandler<DeleteMyAccountCommand> handler,
        ICacheService cache,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new DeleteMyAccountCommand(currentUser.UserId, request.Password), cancellationToken);
        if (result.IsFailure)
        {
            return result.Error.ToProblem();
        }

        // Depois do commit: o stamp em cache cai, e o access token que ainda estiver valendo para no próximo request.
        await cache.RemoveByTagAsync($"user:{currentUser.UserId.Value}", cancellationToken);
        RefreshTokenCookie.Delete(context);
        return TypedResults.NoContent();
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

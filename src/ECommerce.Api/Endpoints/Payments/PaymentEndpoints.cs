using ECommerce.Api.Extensions;
using ECommerce.Application.Abstractions.Authentication;
using ECommerce.Application.Abstractions.Messaging;
using ECommerce.Application.Abstractions.Payments;
using ECommerce.Application.Payments;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace ECommerce.Api.Endpoints.Payments;

/// <summary>Cartão: o Brick devolve o token e o meio; o servidor nunca vê número nem CVV (arquivo 21 §2).</summary>
internal sealed record PayOrderRequest(string Method, string? CardToken, string? PaymentMethodId, string? IssuerId, int? Installments);

/// <summary>Pagamento do pedido (arquivo 07 §7) e notificação da plataforma (arquivo 07 §8).</summary>
internal sealed partial class PaymentEndpoints : IEndpointGroup
{
    /// <summary>Notificação do Mercado Pago tem poucos KB; acima disso não é dele.</summary>
    private const int MaxWebhookBytes = 64 * 1024;

    public void Map(IEndpointRouteBuilder app)
    {
        var payments = app.MapGroup("/orders/{id:guid}/payments").WithTags("Pagamentos").RequireAuthorization(Policies.Customer);

        payments.MapPost("/", Pay).WithName("PayOrder")
            .WithSummary("Paga o pedido com PIX ou cartão (RF-044)")
            .WithDescription(
                "Só pedido aguardando pagamento e no prazo (CA1, senão 422). O valor é o total do pedido (CA4). PIX pedido de novo " +
                "devolve o mesmo QR enquanto vale. Cartão recusado → `status: Rejected`, e a cliente pode tentar de novo (CA3). " +
                "`Idempotency-Key` repete a resposta da mesma tentativa.")
            .RequireRateLimiting(RateLimitPolicies.CustomerWrite);
        payments.MapGet("/current", Current).WithName("GetCurrentPayment")
            .WithSummary("Última tentativa de pagamento do pedido; a tela do PIX consulta enquanto espera");
        payments.MapPost("/simulate-approval", Simulate).WithName("SimulatePaymentApproval")
            .WithSummary("Só em desenvolvimento, com o provedor falso: aprova o PIX pendente")
            .WithDescription("Passa pelo mesmo caminho do webhook. Com o Mercado Pago de verdade → 404.")
            .RequireRateLimiting(RateLimitPolicies.CustomerWrite);

        app.MapPost("/webhooks/payments/{provider}", Webhook).WithTags("Webhooks").WithName("PaymentWebhook")
            .WithSummary("Notificação da plataforma de pagamento (RF-045)")
            .WithDescription("Sem login e sem CORS. Assinatura inválida → 401. Responde 200 rápido; a consulta à plataforma vai para o Outbox.")
            .AllowAnonymous()
            .WithMetadata(new DisableCorsAttribute())
            .RequireRateLimiting(RateLimitPolicies.PublicRead);
    }

    private static async Task<Results<Ok<PaymentResponse>, ProblemHttpResult>> Pay(
        Guid id,
        PayOrderRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        ICurrentUser currentUser,
        ICommandHandler<PayOrderCommand, PaymentResponse> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(
            new PayOrderCommand(
                currentUser.UserId, id, request.Method ?? string.Empty, request.CardToken, request.PaymentMethodId, request.IssuerId, request.Installments, idempotencyKey),
            cancellationToken);
        return result.ToHttpResult();
    }

    private static async Task<Results<Ok<PaymentResponse>, ProblemHttpResult>> Current(
        Guid id, ICurrentUser currentUser, IQueryHandler<GetCurrentPaymentQuery, PaymentResponse> handler, CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new GetCurrentPaymentQuery(currentUser.UserId, id), cancellationToken);
        return result.ToHttpResult();
    }

    private static async Task<Results<Accepted, ProblemHttpResult>> Simulate(
        Guid id, ICurrentUser currentUser, ICommandHandler<SimulatePaymentApprovalCommand> handler, CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new SimulatePaymentApprovalCommand(currentUser.UserId, id), cancellationToken);
        return result.IsSuccess ? TypedResults.Accepted((string?)null) : result.Error.ToProblem();
    }

    private static async Task<IResult> Webhook(
        string provider,
        HttpRequest request,
        IPaymentGateway gateway,
        IPaymentWebhookVerifier verifier,
        ICommandHandler<ReceivePaymentNotificationCommand> handler,
        ILogger<PaymentEndpoints> logger,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(provider, gateway.Name, StringComparison.OrdinalIgnoreCase))
        {
            return TypedResults.NotFound();
        }

        if (request.ContentLength > MaxWebhookBytes)
        {
            return TypedResults.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        using var reader = new StreamReader(request.Body);
        var body = await reader.ReadToEndAsync(cancellationToken);
        var headers = request.Headers.ToDictionary(header => header.Key.ToLowerInvariant(), header => header.Value.ToString(), StringComparer.Ordinal);
        var query = request.Query.ToDictionary(item => item.Key, item => item.Value.ToString(), StringComparer.Ordinal);

        var check = verifier.Verify(headers, query, body);
        if (!check.SignatureValid)
        {
            LogInvalidSignature(logger, request.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "?");
            return TypedResults.Unauthorized();
        }

        if (check.Notification is null)
        {
            return TypedResults.Ok();
        }

        var result = await handler.Handle(new ReceivePaymentNotificationCommand(gateway.Name, check.Notification, body), cancellationToken);
        return result.IsSuccess ? TypedResults.Ok() : result.Error.ToProblem();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Webhook de pagamento com assinatura inválida, vindo de {RemoteIp}")]
    private static partial void LogInvalidSignature(ILogger logger, string remoteIp);
}

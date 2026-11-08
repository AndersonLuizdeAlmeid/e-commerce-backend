using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ECommerce.Application.Abstractions.Payments;
using ECommerce.Domain.Abstractions;
using ECommerce.Domain.Payments;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ECommerce.Infrastructure.Payments;

/// <summary>
/// Seção <c>Payments</c> (arquivo 21, entrega 4 §3). Access Token e segredo do webhook são segredos: só por variável de
/// ambiente ou user-secrets, nunca no appsettings (arquivo 17).
/// </summary>
public sealed class PaymentSettings
{
    public const string SectionName = "Payments";
    public const string FakeProvider = "Fake";
    public const string MercadoPagoProvider = "MercadoPago";

    /// <summary><c>MercadoPago</c>, ou <c>Fake</c> em desenvolvimento e nos testes (P3).</summary>
    public string Provider { get; init; } = MercadoPagoProvider;

    /// <summary>Máximo de parcelas no cartão (P11). Juros e parcelas sem juros são configurados na conta do Mercado Pago.</summary>
    public int MaxInstallments { get; init; } = 1;

    public MercadoPagoSettings MercadoPago { get; init; } = new();

    public sealed class MercadoPagoSettings
    {
        public string BaseUrl { get; init; } = "https://api.mercadopago.com/";

        /// <summary>Credencial de teste (<c>TEST-...</c>) em dev e staging; de produção só no lançamento.</summary>
        public string AccessToken { get; init; } = string.Empty;

        /// <summary>Assinatura secreta do webhook, do painel da aplicação no Mercado Pago (P7).</summary>
        public string WebhookSecret { get; init; } = string.Empty;

        /// <summary>Opcional: endereço de notificação por pagamento. Sem ele, vale o cadastrado no painel.</summary>
        public string? NotificationUrl { get; init; }

        /// <summary>Como a cobrança aparece na fatura do cartão (até 13 caracteres).</summary>
        public string StatementDescriptor { get; init; } = "MO ACESSORIOS";
    }
}

/// <summary>
/// Adaptador do Mercado Pago, Checkout Transparente (D01, ADR-010). Fala com a API de pagamentos; os dados do cartão
/// chegam já como token do Brick (SAQ-A). Erro 4xx é recusa; rede, timeout e 5xx são "plataforma fora".
/// </summary>
internal sealed partial class MercadoPagoGateway(HttpClient http, IOptions<PaymentSettings> options, ILogger<MercadoPagoGateway> logger)
    : IPaymentGateway
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly TimeZoneInfo SaoPaulo = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");

    public string Name => PaymentSettings.MercadoPagoProvider;

    public bool CanSimulate => false;

    public async Task<Result<PaymentSnapshot>> CreatePaymentAsync(CreatePaymentRequest request, CancellationToken cancellationToken)
    {
        var settings = options.Value.MercadoPago;
        // Dicionário não respeita o "ignorar nulos" do serializador: só entram as chaves com valor.
        var payer = new Dictionary<string, object>
        {
            ["email"] = request.Payer.Email,
            ["first_name"] = request.Payer.FirstName,
            ["last_name"] = request.Payer.LastName,
        };
        if (request.Payer.Document is { } document)
        {
            payer["identification"] = new { type = request.Payer.DocumentType, number = document };
        }

        var body = new Dictionary<string, object?>
        {
            ["transaction_amount"] = request.Amount,
            ["description"] = $"Pedido {request.OrderCode} — MO Acessórios",
            ["external_reference"] = request.OrderId.ToString(),
            ["statement_descriptor"] = settings.StatementDescriptor,
            ["payer"] = payer,
        };
        if (!string.IsNullOrWhiteSpace(settings.NotificationUrl))
        {
            body["notification_url"] = settings.NotificationUrl;
        }

        if (request.Method == PaymentMethod.Pix)
        {
            body["payment_method_id"] = "pix";
            if (request.ExpiresAtUtc is { } expires)
            {
                body["date_of_expiration"] = FormatDate(expires);
            }
        }
        else
        {
            body["token"] = request.CardToken;
            body["payment_method_id"] = request.PaymentMethodId;
            body["installments"] = request.Installments ?? 1;
            if (!string.IsNullOrWhiteSpace(request.IssuerId))
            {
                body["issuer_id"] = request.IssuerId;
            }
        }

        using var message = new HttpRequestMessage(HttpMethod.Post, "v1/payments") { Content = JsonContent.Create(body, options: Json) };
        message.Headers.TryAddWithoutValidation("X-Idempotency-Key", request.IdempotencyKey);
        return await SendAsync(message, settings, rejectOn4xx: true, cancellationToken);
    }

    public async Task<Result<PaymentSnapshot>> GetPaymentAsync(string providerPaymentId, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, $"v1/payments/{Uri.EscapeDataString(providerPaymentId)}");
        return await SendAsync(message, options.Value.MercadoPago, rejectOn4xx: false, cancellationToken);
    }

    public async Task<Result> CancelAsync(string providerPaymentId, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Put, $"v1/payments/{Uri.EscapeDataString(providerPaymentId)}")
        {
            Content = JsonContent.Create(new { status = "cancelled" }),
        };
        var result = await SendAsync(message, options.Value.MercadoPago, rejectOn4xx: true, cancellationToken);
        return result.IsSuccess ? Result.Success() : Result.Failure(result.Error);
    }

    public async Task<Result> RefundAsync(string providerPaymentId, Guid idempotencyKey, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, $"v1/payments/{Uri.EscapeDataString(providerPaymentId)}/refunds")
        {
            Content = JsonContent.Create(new { }),
        };
        message.Headers.TryAddWithoutValidation("X-Idempotency-Key", idempotencyKey.ToString());
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.Value.MercadoPago.AccessToken);

        try
        {
            using var response = await http.SendAsync(message, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return Result.Success();
            }

            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            LogProviderError(logger, "estorno", (int)response.StatusCode, Trim(text));
            return Result.Failure((int)response.StatusCode >= 500 ? PaymentErrors.ProviderUnavailable : PaymentErrors.ProviderRejected);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            LogProviderFailure(logger, "estorno", exception);
            return Result.Failure(PaymentErrors.ProviderUnavailable);
        }
    }

    public Task<Result> SimulateApprovalAsync(string providerPaymentId, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Failure(PaymentErrors.SimulationNotAvailable));

    /// <summary>Status do Mercado Pago → nossos (entrega 4 §3, P6).</summary>
    internal static (PaymentStatus Status, bool Disputed) MapStatus(string? status, string? detail) => status switch
    {
        "approved" => (PaymentStatus.Approved, false),
        "pending" or "in_process" or "authorized" => (PaymentStatus.Pending, false),
        "rejected" => (PaymentStatus.Rejected, false),
        "cancelled" when detail is "expired" => (PaymentStatus.Expired, false),
        "cancelled" => (PaymentStatus.Cancelled, false),
        "refunded" => (PaymentStatus.Refunded, false),
        "in_mediation" or "charged_back" => (PaymentStatus.Approved, true),
        _ => (PaymentStatus.Pending, false),
    };

    internal static PaymentSnapshot Parse(string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var status = root.TryGetProperty("status", out var s) ? s.GetString() : null;
        var detail = root.TryGetProperty("status_detail", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
        var (mapped, disputed) = MapStatus(status, detail);

        string? qrCode = null;
        string? qrCodeBase64 = null;
        if (root.TryGetProperty("point_of_interaction", out var poi) && poi.ValueKind == JsonValueKind.Object
            && poi.TryGetProperty("transaction_data", out var data) && data.ValueKind == JsonValueKind.Object)
        {
            qrCode = data.TryGetProperty("qr_code", out var q) && q.ValueKind == JsonValueKind.String ? q.GetString() : null;
            qrCodeBase64 = data.TryGetProperty("qr_code_base64", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null;
        }

        return new PaymentSnapshot(
            root.GetProperty("id").ToString(),
            mapped,
            detail,
            root.TryGetProperty("transaction_amount", out var amount) && amount.ValueKind == JsonValueKind.Number ? amount.GetDecimal() : 0m,
            root.TryGetProperty("external_reference", out var reference) && reference.ValueKind == JsonValueKind.String ? reference.GetString() : null,
            qrCode,
            qrCodeBase64,
            Date(root, "date_of_expiration"),
            Date(root, "date_approved"),
            disputed);
    }

    private async Task<Result<PaymentSnapshot>> SendAsync(
        HttpRequestMessage message, PaymentSettings.MercadoPagoSettings settings, bool rejectOn4xx, CancellationToken cancellationToken)
    {
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.AccessToken);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        try
        {
            using var response = await http.SendAsync(message, cancellationToken);
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return Parse(text);
            }

            LogProviderError(logger, message.Method.Method, (int)response.StatusCode, Trim(text));
            if (response.StatusCode == HttpStatusCode.NotFound && !rejectOn4xx)
            {
                return PaymentErrors.PaymentNotFound;
            }

            return rejectOn4xx && (int)response.StatusCode is >= 400 and < 500 && response.StatusCode != HttpStatusCode.TooManyRequests
                ? PaymentErrors.ProviderRejected
                : PaymentErrors.ProviderUnavailable;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            LogProviderFailure(logger, message.Method.Method, exception);
            return PaymentErrors.ProviderUnavailable;
        }
    }

    /// <summary>"2026-11-10T09:55:40.000-03:00": o formato que a API aceita, no horário de Brasília.</summary>
    private static string FormatDate(DateTime utc)
    {
        var local = new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToOffset(SaoPaulo.GetUtcOffset(utc));
        return local.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture);
    }

    private static DateTime? Date(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed.UtcDateTime
            : null;

    /// <summary>A resposta pode trazer o e-mail do pagador: o log fica com o começo, sem o corpo inteiro (arquivo 21 §7).</summary>
    private static string Trim(string text) => text.Length > 300 ? text[..300] : text;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Mercado Pago respondeu {Status} em {Operation}: {Body}")]
    private static partial void LogProviderError(ILogger logger, string operation, int status, string body);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Falha ao falar com o Mercado Pago em {Operation}")]
    private static partial void LogProviderFailure(ILogger logger, string operation, Exception exception);
}

/// <summary>
/// Confere a notificação do Mercado Pago (entrega 4 §3, P7): HMAC-SHA256 do modelo
/// <c>id:{data.id};request-id:{x-request-id};ts:{ts};</c> com a assinatura secreta, comparado em tempo constante.
/// Como manda a documentação, a parte que não vem na notificação sai do modelo; o <c>data.id</c> vem da URL e, na falta
/// dele, do corpo. Não há janela de tempo: a reentrega legítima chega horas depois, e reenviar uma notificação capturada
/// não faz mal, porque o evento é deduplicado e o pagamento é sempre consultado na plataforma.
/// </summary>
internal sealed partial class MercadoPagoWebhookVerifier(IOptions<PaymentSettings> options, ILogger<MercadoPagoWebhookVerifier> logger)
    : IPaymentWebhookVerifier
{
    public PaymentWebhookCheck Verify(IReadOnlyDictionary<string, string> headers, IReadOnlyDictionary<string, string> query, string body)
    {
        var secret = options.Value.MercadoPago.WebhookSecret;
        if (string.IsNullOrEmpty(secret))
        {
            LogRejected(logger, "Payments:MercadoPago:WebhookSecret não configurado");
            return PaymentWebhookCheck.Invalid;
        }

        if (!headers.TryGetValue("x-signature", out var signature))
        {
            LogRejected(logger, "sem cabeçalho x-signature");
            return PaymentWebhookCheck.Invalid;
        }

        string? ts = null;
        string? v1 = null;
        foreach (var part in signature.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2, StringSplitOptions.TrimEntries);
            if (pair.Length == 2 && pair[0] == "ts")
            {
                ts = pair[1];
            }
            else if (pair.Length == 2 && pair[0] == "v1")
            {
                v1 = pair[1];
            }
        }

        string? type = query.GetValueOrDefault("type") ?? query.GetValueOrDefault("topic");
        string? bodyDataId = null;
        string? eventId = null;
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String)
                {
                    type = t.GetString();
                }

                if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object && data.TryGetProperty("id", out var id))
                {
                    bodyDataId = id.ToString();
                }

                eventId = root.TryGetProperty("id", out var notification) ? notification.ToString() : null;
            }
        }
        catch (JsonException)
        {
            LogRejected(logger, "corpo que não é JSON");
            return PaymentWebhookCheck.Invalid;
        }

        var queryDataId = query.GetValueOrDefault("data.id") ?? query.GetValueOrDefault("id");
        var requestId = headers.GetValueOrDefault("x-request-id");

        byte[]? received = null;
        if (v1 is not null)
        {
            try
            {
                received = Convert.FromHexString(v1);
            }
            catch (FormatException)
            {
                received = null;
            }
        }

        if (ts is null || received is null)
        {
            LogRejected(logger, "x-signature sem ts ou v1 válidos");
            return PaymentWebhookCheck.Invalid;
        }

        // O id da URL é o oficial; o do corpo cobre proxy que perca a query. Sem id nenhum, o modelo fica sem a parte.
        string?[] ids = queryDataId is null && bodyDataId is null ? [null] : new[] { queryDataId, bodyDataId }.Where(id => id is not null).Distinct().ToArray();
        foreach (var id in ids)
        {
            if (CryptographicOperations.FixedTimeEquals(MercadoPagoSignature.Hash(secret, id, requestId, ts), received))
            {
                var dataId = id ?? string.Empty;
                return type is "payment" or null && dataId.Length > 0
                    ? new PaymentWebhookCheck(true, new PaymentNotification(eventId ?? $"{dataId}:{requestId}", dataId))
                    : PaymentWebhookCheck.Ignored;
            }
        }

        // Nada aqui é segredo: ajuda a ver o que a plataforma mandou quando a assinatura não confere.
        LogMismatch(
            logger,
            ts,
            requestId is not null,
            queryDataId ?? "(nenhum)",
            bodyDataId ?? "(nenhum)",
            type ?? "(nenhum)",
            string.Join(",", query.Keys));
        return PaymentWebhookCheck.Invalid;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Webhook do Mercado Pago recusado: {Reason}")]
    private static partial void LogRejected(ILogger logger, string reason);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Assinatura do webhook não confere: ts={Ts}, x-request-id presente={HasRequestId}, data.id na URL={QueryDataId}, data.id no corpo={BodyDataId}, type={Type}, parâmetros da URL={QueryKeys}")]
    private static partial void LogMismatch(
        ILogger logger, string ts, bool hasRequestId, string queryDataId, string bodyDataId, string type, string queryKeys);
}

/// <summary>A assinatura das notificações do Mercado Pago (entrega 4 §3, P7), montada como a plataforma faz.</summary>
public static class MercadoPagoSignature
{
    /// <summary>
    /// HMAC-SHA256 de <c>id:{data.id};request-id:{x-request-id};ts:{ts};</c>. O id alfanumérico vai em minúsculas, e a
    /// parte que não veio na notificação sai do modelo.
    /// </summary>
    public static byte[] Hash(string secret, string? dataId, string? requestId, string ts)
    {
        var manifest = new StringBuilder();
        if (!string.IsNullOrEmpty(dataId))
        {
            manifest.Append("id:").Append(dataId.All(char.IsAsciiLetterOrDigit) ? dataId.ToLowerInvariant() : dataId).Append(';');
        }

        if (!string.IsNullOrEmpty(requestId))
        {
            manifest.Append("request-id:").Append(requestId).Append(';');
        }

        manifest.Append("ts:").Append(ts).Append(';');
        return HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(manifest.ToString()));
    }

    /// <summary>Valor do cabeçalho <c>x-signature</c>: <c>ts=...,v1=...</c>.</summary>
    public static string Header(string secret, string? dataId, string? requestId, string ts) =>
        $"ts={ts},v1={Convert.ToHexStringLower(Hash(secret, dataId, requestId, ts))}";
}

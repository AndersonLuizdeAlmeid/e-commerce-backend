using System.Diagnostics;
using System.Net;
using System.Text.Json;
using ECommerce.Api.FunctionalTests.Infrastructure;
using ECommerce.Api.FunctionalTests.Ordering;
using ECommerce.Application.Abstractions.Messaging;
using ECommerce.Application.Inventory;
using ECommerce.Infrastructure.Jobs;
using Microsoft.Extensions.DependencyInjection;

namespace ECommerce.Api.FunctionalTests.Platform;

/// <summary>Entrega 6.1: Outbox com reserva e vagas, EM-11 e vigia de operação com EM-14.</summary>
public sealed class OperationsTests(ApiFactory factory) : ShopperTest(factory)
{
    private const string SendEmailType = "notifications.send-email";

    /// <summary>EM-03 direto no Outbox, como se um caso de uso tivesse gravado.</summary>
    private Task EnqueueEmailAsync(string to, string extraColumns = "", string extraValues = "") =>
        Factory.ExecuteSqlAsync(
            $"""
            INSERT INTO messaging.outbox_messages (id, type, payload, occurred_at, attempts, next_attempt_at{extraColumns})
            VALUES (gen_random_uuid(), '{SendEmailType}', @Payload::jsonb, now(), 0, now(){extraValues})
            """,
            new
            {
                Payload = JsonSerializer.Serialize(new { template = "EM-03", to, relatedEntityId = (Guid?)null, model = new { name = "Ana" }, secrets = new { } }),
            });

    private Task<IReadOnlyList<string>> CheckAlertsAsync() =>
        Factory.Services.GetRequiredService<OpsMonitor>().CheckAsync(CancellationToken);

    private async Task<int> DigestAsync()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<ICommandHandler<SendLowStockDigestCommand, int>>()
            .Handle(new SendLowStockDigestCommand(), CancellationToken);
        result.IsSuccess.ShouldBeTrue();
        return result.Value;
    }

    /// <summary>Pedido com PIX gerado na plataforma falsa; devolve o id do pedido.</summary>
    private async Task<Guid> OrderWithPixAsync(string email, string piece)
    {
        using var admin = await StoreAsync();
        var variant = await PieceAsync(admin, piece, 5);
        var (customer, address) = await ShopperAsync(email, (variant, 1));
        using (customer)
        {
            var order = (await JsonAsync(await PlaceAsync(customer, address), HttpStatusCode.Created)).GetProperty("id").GetGuid();
            (await customer.PostAsync($"/api/v1/orders/{order}/payments", new { method = "Pix" })).EnsureSuccessStatusCode();
            return order;
        }
    }

    [Fact, Trait("Spec", "ENT-6-P1")]
    public async Task Outbox_SlowMessage_DoesNotHoldBackTheOthers()
    {
        Factory.Emails.SlowRecipients["lenta@loja.teste"] = TimeSpan.FromSeconds(8);
        await EnqueueEmailAsync("lenta@loja.teste");
        await Task.Delay(500, CancellationToken); // a lenta já ocupa uma vaga

        var clock = Stopwatch.StartNew();
        await EnqueueEmailAsync("rapida@loja.teste");
        await Factory.Emails.WaitForAsync("rapida@loja.teste", "senha foi alterada", TimeSpan.FromSeconds(5));
        clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
        Factory.Emails.Sent.ShouldNotContain(m => m.To == "lenta@loja.teste");

        await Factory.Emails.WaitForAsync("lenta@loja.teste", "senha foi alterada", TimeSpan.FromSeconds(15));
    }

    [Fact, Trait("Spec", "ENT-6-P1")]
    public async Task Outbox_WhileProcessing_HoldsNoLockOnTheMessage()
    {
        Factory.Emails.SlowRecipients["lenta@loja.teste"] = TimeSpan.FromSeconds(6);
        await EnqueueEmailAsync("lenta@loja.teste");
        await Task.Delay(1000, CancellationToken);

        // Antes, a linha ficava travada na transação do lote durante a chamada externa; agora outra conexão escreve nela na hora.
        var clock = Stopwatch.StartNew();
        await Factory.ExecuteSqlAsync("SET lock_timeout = '1s'; UPDATE messaging.outbox_messages SET last_error = last_error");
        clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(1));

        var reserved = await Factory.QuerySingleAsync<bool>("SELECT locked_until > now() AND lock_token IS NOT NULL FROM messaging.outbox_messages");
        reserved.ShouldBeTrue();
        await Factory.Emails.WaitForAsync("lenta@loja.teste", "senha foi alterada", TimeSpan.FromSeconds(15));
        (await Factory.QuerySingleAsync<bool>("SELECT processed_at IS NOT NULL AND locked_until IS NULL FROM messaging.outbox_messages")).ShouldBeTrue();
    }

    [Fact, Trait("Spec", "ENT-6-P1")]
    public async Task Outbox_ExpiredLease_IsProcessedAgain_ButActiveLeaseIsNot()
    {
        // Reserva vencida: um processamento que morreu no meio (API reiniciada).
        await EnqueueEmailAsync("vencida@loja.teste", ", locked_until, lock_token", ", now() - interval '1 minute', gen_random_uuid()");
        // Reserva em vigor: outra instância está processando.
        await EnqueueEmailAsync("ocupada@loja.teste", ", locked_until, lock_token", ", now() + interval '10 minutes', gen_random_uuid()");

        await Factory.Emails.WaitForAsync("vencida@loja.teste", "senha foi alterada");
        await Task.Delay(1000, CancellationToken);
        Factory.Emails.Sent.ShouldNotContain(m => m.To == "ocupada@loja.teste");
    }

    [Fact, Trait("Spec", "EM-11")]
    public async Task LowStockDigest_ListsOnlyPiecesForSaleAtTheThreshold_OncePerDay()
    {
        var category = await Catalog.CategoryAsync("Colares", "colares");
        var low = await Catalog.ProductAsync(category, "Colar Estrela", "colar-estrela");
        await Catalog.VariantAsync(low, "COL-EST-1", stock: 1);
        var plenty = await Catalog.ProductAsync(category, "Colar Sol", "colar-sol");
        await Catalog.VariantAsync(plenty, "COL-SOL-1", stock: 10);
        var hidden = await Catalog.ProductAsync(category, "Colar Antigo", "colar-antigo", isActive: false);
        await Catalog.VariantAsync(hidden, "COL-ANT-1", stock: 0);

        (await DigestAsync()).ShouldBe(1);

        var email = await Factory.Emails.WaitForAsync(ApiFactory.StoreEmail, "Estoque baixo: 1 peça");
        email.TextBody.ShouldContain("Colar Estrela");
        email.TextBody.ShouldContain("SKU COL-EST-1 · 1 em estoque (limite 3)");
        email.TextBody.ShouldNotContain("COL-SOL-1");
        email.TextBody.ShouldNotContain("COL-ANT-1");
        email.TextBody.ShouldContain("https://loja.teste/admin/estoque");

        (await DigestAsync()).ShouldBe(0);
        await Task.Delay(1000, CancellationToken);
        Factory.Emails.Sent.Count(m => m.Subject.StartsWith("Estoque baixo", StringComparison.Ordinal)).ShouldBe(1);
    }

    [Fact, Trait("Spec", "EM-11")]
    public async Task LowStockDigest_WithoutLowStock_SendsNothing()
    {
        var category = await Catalog.CategoryAsync("Colares", "colares");
        await Catalog.VariantAsync(await Catalog.ProductAsync(category, "Colar Sol", "colar-sol"), "COL-SOL-1", stock: 10);

        (await DigestAsync()).ShouldBe(0);

        (await Factory.QuerySingleAsync<long>("SELECT count(*) FROM messaging.outbox_messages")).ShouldBe(0);
    }

    [Fact, Trait("Spec", "ENT-6-P3")]
    public async Task Monitor_WhenAllIsWell_SendsNothing()
    {
        (await CheckAlertsAsync()).ShouldBeEmpty();

        Factory.Emails.Sent.ShouldBeEmpty();
    }

    [Fact, Trait("Spec", "ENT-6-P3")]
    public async Task Monitor_FailedMessage_AlertsOnceDirectly_ThenWaitsTheInterval()
    {
        await EnqueueEmailAsync("x@loja.teste", ", failed_at, processed_at, last_error", ", now(), NULL, 'Resend respondeu 500'");
        await Factory.ExecuteSqlAsync("UPDATE messaging.outbox_messages SET attempts = 5");

        (await CheckAlertsAsync()).ShouldBe(["outbox-failed"]);

        var alert = Factory.Emails.Sent.Single();
        alert.To.ShouldBe(ApiFactory.AlertEmail);
        alert.Subject.ShouldBe("Alerta da loja: mensagens que desistiram");
        alert.TextBody.ShouldContain("1 mensagem(ns) do Outbox desistiram");
        alert.TextBody.ShouldContain("notifications.send-email em");
        alert.TextBody.ShouldContain("Resend respondeu 500");
        // Direto pelo provedor: o alerta não depende do Outbox que ele está avisando.
        (await Factory.QuerySingleAsync<long>("SELECT count(*) FROM messaging.outbox_messages")).ShouldBe(1);

        // Nova falha dentro de 6 h: não repete agora.
        await EnqueueEmailAsync("y@loja.teste", ", failed_at", ", now()");
        (await CheckAlertsAsync()).ShouldBeEmpty();

        // Passadas as 6 h, só a falha nova entra.
        await Factory.ExecuteSqlAsync("UPDATE ops.job_marks SET last_at = last_at - interval '7 hours'");
        await Factory.ExecuteSqlAsync("UPDATE messaging.outbox_messages SET failed_at = failed_at - interval '8 hours' WHERE payload->>'to' = 'x@loja.teste'");
        (await CheckAlertsAsync()).ShouldBe(["outbox-failed"]);
        Factory.Emails.Sent.Last().TextBody.ShouldContain("1 mensagem(ns) do Outbox desistiram");
    }

    [Fact, Trait("Spec", "ENT-6-P3")]
    public async Task Monitor_DelayedQueue_Alerts()
    {
        // Esperando há 20 min, com a próxima tentativa só daqui a 1 h (o processador não pega agora).
        await EnqueueEmailAsync("x@loja.teste");
        await Factory.ExecuteSqlAsync(
            "UPDATE messaging.outbox_messages SET occurred_at = now() - interval '20 minutes', next_attempt_at = now() + interval '1 hour', attempts = 3, last_error = 'timeout'");

        (await CheckAlertsAsync()).ShouldBe(["outbox-delayed"]);

        Factory.Emails.Sent.Single().Subject.ShouldBe("Alerta da loja: fila de mensagens atrasada");
    }

    [Fact, Trait("Spec", "ENT-6-P3")]
    public async Task Monitor_PixApprovedWithoutWebhook_Alerts_ButNotWhenTheWebhookCame()
    {
        var withoutWebhook = await OrderWithPixAsync("maria@loja.teste", "Colar Lua");
        var withWebhook = await OrderWithPixAsync("joana@loja.teste", "Colar Sol");
        // Como se a plataforma real tivesse aprovado há 20 min; só um dos dois recebeu notificação.
        await Factory.ExecuteSqlAsync(
            """
            UPDATE payments.payments SET provider = 'MercadoPago', status = 'Approved', approved_at = now() - interval '20 minutes'
             WHERE order_id IN (@withoutWebhook, @withWebhook);
            INSERT INTO payments.payment_events (provider, provider_event_id, provider_payment_id, payload, received_at)
            SELECT 'MercadoPago', 'evt-1', provider_payment_id, '{}'::jsonb, now() FROM payments.payments WHERE order_id = @withWebhook;
            """,
            new { withoutWebhook, withWebhook });
        Factory.Emails.Clear();

        (await CheckAlertsAsync()).ShouldBe(["pix-without-webhook"]);

        var alert = Factory.Emails.Sent.Single(m => m.To == ApiFactory.AlertEmail);
        alert.TextBody.ShouldContain("1 PIX foram aprovados sem nenhuma notificação");
    }

    [Fact, Trait("Spec", "ENT-6-P3")]
    public async Task Monitor_ShippingFallback_Alerts()
    {
        var order = await OrderWithPixAsync("maria@loja.teste", "Colar Lua");
        await Factory.ExecuteSqlAsync("UPDATE ordering.orders SET shipping_is_fallback = true WHERE id = @order", new { order });
        Factory.Emails.Clear();

        (await CheckAlertsAsync()).ShouldBe(["shipping-fallback"]);

        Factory.Emails.Sent.Single(m => m.To == ApiFactory.AlertEmail).TextBody.ShouldContain("1 pedido(s) usaram o frete de contingência");
    }

    [Fact, Trait("Spec", "ENT-6-P3")]
    public async Task Monitor_WhenTheProviderFails_TriesAgainNextRound()
    {
        await EnqueueEmailAsync("x@loja.teste", ", failed_at", ", now()");
        Factory.Emails.FailNext = 1;

        (await CheckAlertsAsync()).ShouldBeEmpty();
        (await Factory.QuerySingleAsync<long>("SELECT count(*) FROM ops.job_marks")).ShouldBe(0);

        (await CheckAlertsAsync()).ShouldBe(["outbox-failed"]);
    }
}

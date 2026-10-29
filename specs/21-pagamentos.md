# 21 — Pagamentos

**Status:** Rascunho — plataforma decidida: **Mercado Pago** (arquivo 20, D01), só **PIX e cartão** (D02). Detalhamento na Fase 4.

## 1. Objetivo

Permitir que o cliente pague o pedido online (PIX e cartão de crédito; **sem boleto** — arquivo 20, D02) por meio de uma **plataforma de pagamento** (gateway), com confirmação automática do pagamento no pedido.

## 2. Princípios

- **A loja nunca toca em dados de cartão.** Número, CVV e validade são capturados pelo componente/checkout da plataforma (tokenização ou checkout hospedado). Isso mantém a loja fora do escopo pesado do **PCI-DSS** (fica em SAQ-A).
- **A fonte da verdade do status do pagamento é o webhook da plataforma**, nunca o retorno do navegador (redirect de "sucesso" pode ser forjado ou nunca acontecer).
- **Plataforma substituível:** o domínio conhece só a porta `IPaymentGateway`; cada plataforma é um *adapter* na Infrastructure.

## 3. Plataforma

> **Decidido (21/10/2026): Mercado Pago**, Checkout Transparente com Bricks (cartão tokenizado no navegador). Taxas de referência: PIX 0,99% (dinheiro na hora), cartão à vista 3,99% em 30 dias ou ~4,98% na hora. Parcelamento configurável (D07, pendente só para o lançamento). As outras opções ficam abaixo como histórico da comparação.


| Plataforma | PIX | Cartão | Boleto | Observações |
|------------|:---:|:------:|:------:|-------------|
| **Mercado Pago** (escolhido) | ✅ | ✅ (parcelado) | ✅ | Muito conhecido pelo consumidor brasileiro; checkout transparente (Bricks) ou hospedado (Checkout Pro); webhooks assinados. |
| Pagar.me (Stone) | ✅ | ✅ | ✅ | API sólida, foco em e-commerce. |
| Asaas | ✅ | ✅ | ✅ | Simples, bom para pequenos volumes. |
| Stripe | ✅ | ✅ | ✅ | Excelente API/documentação; taxas e suporte locais a comparar. |

> Critérios para o cliente decidir: **taxas** por método, **prazo de recebimento**, parcelamento, conta já existente, suporte.

## 4. Modelo (contexto **Payments**)

Novo bounded context. Agregado **`Payment`**:

| Campo | Descrição |
|-------|-----------|
| `Id` | UUID v7 |
| `OrderId` | pedido pago |
| `Provider` | `MercadoPago` \| `PagarMe` \| ... |
| `ProviderPaymentId` | id na plataforma |
| `Method` | `Pix` \| `CreditCard` \| `Boleto` |
| `Amount` | `Money` — **sempre igual ao total do pedido** (calculado no servidor) |
| `Status` | `Pending` → `Approved` \| `Rejected` \| `Expired` \| `Cancelled`; `Approved` → `Refunded` \| `RefundFailed` |
| `ExpiresAtUtc` | PIX/boleto: limite para pagamento (alinhado ao cancelamento de 48 h) |
| `PixQrCode` / `PixCopyPaste` / `BoletoUrl` | dados para o cliente pagar |
| `Installments` | parcelas (cartão) |

Um pedido pode ter **várias tentativas** de pagamento (ex.: cartão recusado, depois PIX); no máximo **uma** `Approved`.

### Porta

```csharp
public interface IPaymentGateway
{
    Task<Result<PaymentIntent>> CreatePaymentAsync(CreatePaymentRequest req, CancellationToken ct); // idempotente por PaymentId
    Task<Result<PaymentSnapshot>> GetPaymentAsync(string providerPaymentId, CancellationToken ct);
    Task<Result> RefundAsync(string providerPaymentId, Money amount, CancellationToken ct);
    bool TryParseWebhook(HttpRequestData request, out WebhookNotification notification);         // valida assinatura
}
```

## 5. Fluxos

### 5.1 Pagar um pedido (RF-044)
1. Cliente cria o pedido (RF-040) → status `AguardandoPagamento`, estoque baixado.
2. Cliente escolhe o método → `POST /orders/{id}/payments` `{ method, cardToken?, installments? }`.
3. API cria o `Payment` (`Pending`) e chama `IPaymentGateway.CreatePaymentAsync` com **chave de idempotência = PaymentId**.
4. Resposta ao frontend:
   - **PIX**: QR Code + "copia e cola" + expiração.
   - **Cartão**: resultado imediato quando a plataforma responde de forma síncrona (aprovado/recusado), ainda confirmado via webhook.
   - **Boleto**: link/linha digitável + vencimento.

### 5.2 Confirmação (webhook) (RF-045)
1. Plataforma chama `POST /api/v1/webhooks/payments/{provider}`.
2. API **valida a assinatura** (HMAC/segredo da plataforma) → inválida: `401`, log de segurança.
3. Registra o evento em `payments.payment_events` (**idempotente** pelo id do evento na plataforma — reentregas são ignoradas).
4. **Consulta o pagamento na plataforma** (`GetPaymentAsync`) — não confia apenas no corpo do webhook.
5. Se `Approved` e valor confere com o pedido → `Payment.Approve()` + `Order.ConfirmPayment()` na mesma transação → evento `OrderPaymentConfirmed` → e-mail (arquivo 23).
6. Responde `200` rápido; o processamento pesado vai para o Outbox (arquivo 25).

### 5.3 Pagamento recusado/expirado
- `Rejected`: pedido continua `AguardandoPagamento`; cliente pode tentar outro método.
- `Expired` (PIX/boleto): sem efeito imediato; o job de 48 h cancela o pedido (arquivo 25).

### 5.4 Cancelamento com pagamento aprovado
- Cancelar um pedido `PagamentoConfirmado` dispara **estorno total** (`RefundAsync`) — status do pagamento `Refunded` confirmado também por webhook.
- Falha no estorno → o pedido fica `Cancelado`, o pagamento fica `RefundFailed` e o admin recebe um alerta para tratar manualmente (o painel lista estornos pendentes).

### 5.5 Reconciliação
- Job a cada 15 min consulta pagamentos `Pending` com mais de 10 min sem webhook (proteção contra webhook perdido).

## 6. Regras de negócio

- RN-100: o valor cobrado é sempre o `Total` do pedido calculado no servidor.
- RN-101: pagamento aprovado após o pedido já ter sido cancelado (ex.: pagou no último minuto e o job cancelou) → **estorno automático** + e-mail explicando.
- RN-102: um pedido não pode ter dois pagamentos aprovados; se acontecer, o segundo é estornado.
- RN-103 (*fora do MVP: boleto não será oferecido, D02*): boleto, se habilitado, tem vencimento de **2 dias** — compensação pode levar até 3 dias úteis, por isso o cancelamento automático de pedidos com boleto usa o prazo do boleto + compensação, e não 48 h (ver arquivo 20, D02).

## 7. Segurança

- Credenciais da plataforma em secret manager; chaves diferentes para sandbox e produção.
- Endpoint de webhook: `AllowAnonymous`, **sem CORS**, rate limit próprio, validação de assinatura obrigatória, opcionalmente allowlist de IPs da plataforma.
- Logs nunca contêm dados de cartão nem o payload completo do webhook em produção.

## 8. Testes

- Adapter testado contra **sandbox** da plataforma (testes manuais/contrato) e com **WireMock.Net** nos testes automatizados.
- Casos obrigatórios: webhook com assinatura inválida, webhook duplicado, webhook fora de ordem, pagamento após cancelamento, estorno com falha.

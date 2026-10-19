# 22 — Frete e Envio (Correios)

**Status:** Rascunho — decisão do cliente: envio pelos **Correios**, com cálculo **integrado** se possível, e **regras de frete flexíveis**.

## 1. Objetivo

- Calcular o frete real (PAC e SEDEX) no carrinho e no checkout a partir do CEP do cliente.
- Permitir que a loja aplique **regras comerciais flexíveis** sobre o valor calculado (frete grátis, desconto, por região).
- Facilitar o envio: geração de etiqueta e **rastreio automático** (evolução).

## 2. Como integrar com os Correios

| Opção | Como funciona | Prós | Contras |
|-------|---------------|------|---------|
| **A. API oficial dos Correios** (`api.correios.com.br`: Preço, Prazo, Rastro, Pré-postagem) | Integração direta com token do contrato | Sem intermediário; preços do contrato | **Exige contrato comercial** com os Correios (cartão de postagem) |
| **B. Agregador (ex.: Melhor Envio)** (sugestão se não houver contrato) | Cota Correios (PAC/SEDEX/Mini Envios) e outras transportadoras numa única API | Sem contrato; descontos sobre o balcão; gera etiqueta e rastreio | Intermediário e saldo pré-pago para etiquetas |

> **Decisão pendente (arquivo 20, D03):** o cliente já tem contrato com os Correios? Sim → opção A. Não → opção B. Nos dois casos o domínio é o mesmo, só muda o *adapter*.

## 3. Modelo

### 3.1 Dados de produto necessários
Cada **variação** (arquivo 04) precisa de **peso (g)** e **dimensões (cm: altura, largura, comprimento)** — com valores padrão no produto e possibilidade de sobrescrever na variação. Sem isso não há cálculo de frete.

### 3.2 Porta

```csharp
public interface IShippingQuoteProvider
{
    Task<Result<IReadOnlyList<ShippingQuote>>> QuoteAsync(ShippingQuoteRequest req, CancellationToken ct);
}
public sealed record ShippingQuoteRequest(PostalCode Origin, PostalCode Destination, IReadOnlyList<Package> Packages, Money DeclaredValue);
public sealed record ShippingQuote(string ServiceCode, string ServiceName, Money Price, int DeliveryDays);
```

- `Package` é montado por um `PackagingService` simples (soma de pesos; caixa padrão configurável), suficiente no início.

### 3.3 Regras flexíveis (`ShippingRule`) — configuradas pelo admin

Aplicadas **sobre** a cotação, em ordem de prioridade:

| Tipo de regra | Exemplo |
|---------------|---------|
| Frete grátis acima de valor | Subtotal ≥ R$ 299 → PAC grátis |
| Frete grátis/desconto por região (UF ou faixa de CEP) | Sul e Sudeste: 50% de desconto no PAC |
| Acréscimo fixo / percentual | + R$ 3 de embalagem |
| Valor fixo de contingência | Se a API dos Correios estiver fora: PAC R$ 25, SEDEX R$ 45 |
| Ativar/desativar serviço | Ocultar SEDEX para determinada região |

Campos: `Name`, `Priority`, `IsActive`, `ValidFrom/ValidTo` (promoções com data), condições (`MinSubtotal`, `States[]`, `PostalCodeRanges[]`, `ServiceCodes[]`) e efeito (`FreeShipping` \| `PercentDiscount` \| `FixedDiscount` \| `Surcharge`).

## 4. Fluxos

### 4.1 Cotação (RF-032)
`POST /api/v1/shipping/quotes` `{ postalCode, items: [{ variantId, quantity }] }` → lista de opções (`PAC — R$ 18,90 — 6 dias úteis`, `SEDEX — ...`).
- Público (o visitante pode simular no produto/carrinho), com **rate limit** próprio (arquivo 11).
- **Cache** de 30 min por (CEP destino + pacote) — arquivo 12.
- Prazo exibido = prazo dos Correios + **dias de manuseio** configuráveis.

### 4.2 Checkout
- O cliente escolhe o serviço; o backend **recalcula** a cotação no momento do pedido (nunca confia no valor vindo do navegador) e grava `ShippingService`, `ShippingFee`, `EstimatedDeliveryDays` no pedido.

### 4.3 Envio e rastreio (RF-053 / evolução)
- MVP: admin informa o **código de rastreio** ao marcar `Enviado`; e-mail ao cliente com link de rastreio.
- Evolução: geração de etiqueta pela API (pré-postagem/Melhor Envio) e job que consulta o rastro e marca `Entregue` automaticamente.

### 4.4 Endereço
- Preenchimento automático por CEP via **ViaCEP** (ou API de CEP dos Correios), no frontend e validado no backend.

## 5. Falhas

- Timeout de cotação: **3 s**, com retry e circuit breaker (`Microsoft.Extensions.Http.Resilience`).
- API indisponível → regra de **valor fixo de contingência**; o pedido registra que o frete foi de contingência.

## 6. Testes
- Regras de frete: testes unitários parametrizados (prioridade, datas, regiões, combinação).
- Adapter: WireMock.Net com respostas reais gravadas do sandbox.

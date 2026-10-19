# 20 — Decisões

**Status:** Parcialmente decidido

## 1. Decididas pelo cliente (19/10/2026)

| # | Pergunta | Decisão | Specs afetadas |
|---|----------|---------|----------------|
| Q01 | Variações (cor/tamanho)? | **Sim**, cores e tamanhos com estoque/SKU por variação + **botão de código de barras** | 01, 04, 05, 07 |
| Q02 | Frete? | **Correios**, integrado se possível; regras **flexíveis** definidas pela loja | 22 |
| Q03 | Compra sem conta? | **Não** | 01, 09 |
| Q04 | Pagamento? | **Integrado com uma plataforma** já na primeira versão | 21 |
| Q05 | Cancelar pedido não pago? | **Sim, após 48 h** | 25 |
| Q10 | SEO é prioridade? | **Sim** → Next.js com renderização no servidor | 16, 24 |
| T05 | Envio de e-mail? | **Sim** | 23 |

## 2. Ainda em aberto — negócio (precisam do cliente)

| # | Pergunta | Sugestão | Impacta |
|---|----------|----------|---------|
| D01 | **Qual plataforma de pagamento?** (Mercado Pago, Pagar.me, Asaas, Stripe) — comparar taxas e prazo de recebimento | Mercado Pago | 21 |
| D02 | Aceitar **boleto**? Ele compensa em até 3 dias úteis, o que conflita com as 48 h | Só **PIX e cartão** | 21, 25 |
| D03 | A loja tem **contrato com os Correios**? | Sem contrato → **Melhor Envio** (cota Correios sem contrato e gera etiquetas) | 22 |
| D04 | **Provedor de e-mail** e domínio de envio | Amazon SES ou Resend, com domínio próprio | 23 |
| D05 | Exigir **e-mail confirmado** para finalizar a compra? | Sim | 01, 23 |
| D06 | **Fotos**: upload pelo painel (com CDN) ou só links externos? SEO pesa a favor do upload | Upload (S3/Cloudflare R2 + CDN) | 05, 07, 24 |
| D07 | Parcelamento no cartão: quantas vezes e com ou sem juros? | Até [__]x sem juros acima de R$ [__] | 21 |
| D08 | Primeira regra de frete: frete grátis acima de qual valor? Regiões com desconto? | Grátis no PAC acima de R$ [__] | 22 |
| D09 | Volume esperado (produtos, pedidos/dia) | ~1.000 produtos, ~100 pedidos/dia | 02, 12 |
| D10 | **Código de barras**: o botão serve só para **ler** (achar produto, dar entrada no estoque) ou também para **gerar** códigos e **imprimir etiquetas**? Os produtos já vêm com EAN do fornecedor? | Ler + cadastrar EAN no início; gerar/imprimir depois | 01, 07, 16 |
| Q06 | Admin pode comprar com a mesma conta? | Não | 09 |
| Q07 | Subcategorias? | 1 nível no início | 04, 05 |

## 3. Em aberto — técnicas (entre nós)

| # | Pergunta | Sugestão | Impacta |
|---|----------|----------|---------|
| T01 | Mono-repo ou repos separados? | Separados, tipos via OpenAPI | 00, 18 |
| T03 | PBKDF2 (`PasswordHasher`) ou Argon2id? | PBKDF2 | 08 |
| T04 | Redis desde o início? | Não (HybridCache L1) | 12 |
| T07 | Hospedagem? | Front na **Vercel**; API + Postgres em cloud gerenciada (Azure Container Apps / AWS / Railway) | 18 |
| T08 | Destino de logs/traces | OTLP genérico; Grafana Cloud | 14 |
| T09 | Biblioteca de UI | Tailwind + shadcn/ui | 16 |
| T11 | Minimal APIs ou Controllers? | Minimal APIs | 03 |
| T12 | Idioma do código | Inglês no código, pt-BR nas mensagens | todos |
| T13 | Jobs: `BackgroundService` próprio ou Hangfire? | Próprio no início | 25 |

# 20 — Decisões

**Status:** Decidido (21/10/2026), exceto parcelamento e domínio (seção 4), que não bloqueiam o desenvolvimento.

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

## 2. Decididas em 21/10/2026 — negócio

| # | Pergunta | Decisão | Consequência | Specs |
|---|----------|---------|--------------|-------|
| D02 | Boleto? | **Não.** Só **PIX e cartão** | Nenhum conflito com o cancelamento em 48 h; `method = 'Boleto'` fica fora do MVP | 21, 25 |
| D03 | Contrato com os Correios? | **Sem contrato** → **Melhor Envio** (cota PAC/SEDEX sem contrato, gera etiqueta depois) | Adapter `MelhorEnvioQuoteProvider` (ADR-010); sem custo fixo, a loja paga só as etiquetas | 22 |
| D04 | Provedor de e-mail | **Resend**, plano gratuito (3.000/mês, 100/dia), com domínio próprio | Precisa do **domínio da loja** e de SPF/DKIM/DMARC; troca para Amazon SES (US$ 0,10/1.000) se passar do limite. `IEmailSender` mantém o provedor trocável | 23 |
| D05 | Exigir e-mail confirmado para comprar? | **Sim** (`422 Identity.EmailNotConfirmed`) | RF-009 CA2 entra na Fase 2/4 | 01, 23 |
| D06 | Fotos | **Upload pelo painel** para **Cloudflare R2** (S3 compatível, sem custo de saída) | Custo **R$ 0** até 10 GB (~4.000 fotos otimizadas); acima disso ~US$ 0,015/GB/mês. `next/image` gera os tamanhos | 05, 07, 24 |
| D09 | Volume | **Baixo**: ~10 pedidos/semana no início, com potencial de crescer | 1 instância, cache só em memória, sem Redis, VM única (T07) | 02, 12 |
| D10 | Código de barras | **Ler e cadastrar** EAN (busca, vínculo à variação, entrada de estoque). Gerar/imprimir etiquetas fica fora | RF-059 sem o item de geração | 01, 07, 16 |
| D11 | Produto que sai de linha | **Redireciona para a categoria** (sem página "não existe mais") | Já implementado na Fase 1 | 24 |
| D12 | Fotos da vitrine de demonstração | **Ilustrativas** até o upload (D06) | — | 24 |
| Q06 | Admin compra com a mesma conta? | **Não** | Policy `Customer` exclui `Admin` | 09 |
| Q07 | Subcategorias? | **1 nível** (categoria → subcategoria) | Nova coluna `catalog.categories.parent_id` (migration na Fase 2, junto com o admin de categorias) | 04, 05 |

## 3. Decididas em 21/10/2026 — técnicas

| # | Pergunta | Decisão | Specs |
|---|----------|---------|-------|
| T01 | Mono-repo ou repos separados? | **Repos separados**, tipos via OpenAPI | 00, 18 |
| T03 | PBKDF2 ou Argon2id? | **PBKDF2** (`PasswordHasher<T>`) | 08 |
| T04 | Redis? | **Não.** HybridCache só L1 (memória), sem custo | 12 |
| T07 | Hospedagem | **Uma VM** com Docker Compose (Caddy + Next.js + API + PostgreSQL), **Cloudflare grátis** na frente (DNS, TLS, CDN, proteção) e backup diário do banco no R2. Detalhes no arquivo 18 §7 | 18 |
| T08 | Logs/traces | **O mais barato:** Serilog em JSON nos logs do Docker (com rotação) + **Grafana Cloud** no plano gratuito via OTLP, quando precisarmos de busca e alertas | 14 |
| T09 | Biblioteca de UI | **Tailwind + primitivos headless** (Radix) com identidade visual própria, **sem** o visual padrão do shadcn/ui. Direção de arte definida com a skill **impeccable** antes das telas da Fase 2 | 16 |
| T11 | Minimal APIs ou Controllers? | **Minimal APIs** | 03 |
| T12 | Idioma | **Inglês no código, pt-BR nas mensagens** | todos |
| T13 | Jobs | **`BackgroundService` próprio** | 25 |

## 3b. Decididas em 21/10/2026 (segunda rodada)

| # | Pergunta | Decisão | Consequência | Specs |
|---|----------|---------|--------------|-------|
| D01 | Plataforma de pagamento | **Mercado Pago** (Checkout Transparente com Bricks): PIX 0,99% com dinheiro na hora; cartão 3,99% (30 dias) ou ~4,98% (na hora) | Adapter `MercadoPagoGateway` atrás de `IPaymentGateway` (ADR-010); sandbox em dev/staging. Abrir a conta antes da Fase 4 | 21 |
| D08 | Regras de frete | **Dinâmicas**: o frete sai do preço do Melhor Envio e o admin cria/edita regras (grátis, desconto, acréscimo, por UF/CEP/valor) quando quiser (RF-058). Lança **sem nenhuma regra** | Nenhuma regra fixa no código nem no seed | 22 |

## 4. Pendências

| # | Pergunta | Recomendação | Quando precisa estar decidido |
|---|----------|--------------|-------------------------------|
| D07 | Parcelamento | Deixar **configurável** (máx. de parcelas, parcelas sem juros, valor mínimo da parcela). Sugestão inicial: até **3x sem juros** acima de R$ 150; acima disso com juros pagos pelo comprador | Só antes do **lançamento**: não bloqueia o desenvolvimento |
| — | Domínio da loja | Registrar o `.com.br` (registro.br, cerca de R$ 40/ano). **Não** é preciso para desenvolver: em dev os e-mails vão para o Mailpit e a loja roda em localhost | Antes do **staging** com acesso externo ou de enviar e-mail a pessoas reais (TLS, cookies entre `loja` e `api`, Resend, Search Console) |

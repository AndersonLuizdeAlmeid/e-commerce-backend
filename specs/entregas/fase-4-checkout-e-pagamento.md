# Entrega — Fase 4: Checkout e pagamento

**Status:** Planejada em 08/11/2026. **As cinco fatias estão implementadas e validadas com banco, API em container e as telas no navegador em 08/11/2026** (seção 8). Pagamento, cartão e estorno foram validados com a plataforma falsa; **falta o sandbox do Mercado Pago**, que depende das credenciais e do Funnel na VM, e falta aprovar o visual do checkout com a loja.
**Repositórios:** `e-commerce-backend` e `e-commerce-frontend` (branch `feat/fase-4` nos dois).

## 1. Contexto

Depois da Fase 3, o cliente monta a sacola e vê o frete real, mas "Finalizar compra" para na mensagem de que a compra online chega na próxima etapa (P13 da Fase 3). A Fase 4 fecha a venda: endereço, pedido com baixa de estoque, pagamento por PIX e cartão no Mercado Pago, confirmação automática pelo webhook, cancelamento em 48 h e estorno.

A conta do Mercado Pago e a aplicação (Checkout Transparente) foram criadas em 08/11/2026, com as credenciais de teste.

A entrega é feita em **cinco fatias**, cada uma com build verde e checklist próprio.

| Fatia | Conteúdo | Requisitos | Depende de |
|-------|----------|------------|------------|
| **4.1 — Endereços** | Caderno de endereços com ViaCEP, em `/conta/enderecos` e reaproveitado no checkout | RF-007 | — |
| **4.2 — Pedido** | Checkout sem pagamento: pedido com baixa de estoque, presente, meus pedidos, cancelamento pelo cliente, jobs de 24 h e 48 h, e-mails EM-04, EM-05 e EM-10 | RF-040..043, RF-046, RF-047 | 4.1 |
| **4.3 — PIX e webhook** | Contexto Payments, Mercado Pago e gateway falso, QR na tela, webhook assinado, reconciliação, EM-06 e EM-07 | RF-044, RF-045 | 4.2 |
| **4.4 — Cartão** | Card Payment Brick, parcelas, recusa com nova tentativa | RF-044 CA3 | 4.3 |
| **4.5 — Estornos** | Cancelar pedido pago, pagamento depois do cancelamento, segundo pagamento aprovado, falha de estorno | RF-043 CA2, RN-101, RN-102 | 4.3 |

4.4 e 4.5 são independentes entre si. Decisões que a entrega aplica: Q03 (sem compra como visitante), Q05 (cancelamento em 48 h), Q06 (admin não compra), D01 (Mercado Pago), D02 (só PIX e cartão), D05 (e-mail confirmado para comprar), D-F02 (presente), arquivo 21 inteiro e arquivo 26 §4.6 (checkout).

## 2. Objetivos

1. O cliente **compra pelo celular sem falar com ninguém**: endereço, frete, presente, revisão e pagamento, um passo por tela.
2. **Nenhuma peça é vendida duas vezes**: o pedido baixa o estoque na mesma transação, e duas compras da última unidade resultam em um pedido só.
3. **O valor cobrado é sempre o calculado no servidor**, e o pedido só é confirmado depois de consultar o pagamento na plataforma, nunca pelo retorno do navegador.
4. O PIX aparece **na hora**, e a tela muda sozinha quando o pagamento cai.
5. Pedido não pago **se cancela sozinho em 48 h** e devolve o estoque; o cliente recebe lembrete com 24 h.
6. **Nenhum dinheiro fica preso**: pagamento de pedido cancelado é estornado sozinho, e o estorno que falha chega à loja por e-mail.
7. A loja **nunca vê dados de cartão** (SAQ-A, arquivo 21 §2).

## 3. Decisões que esta fase precisa fechar

| # | Ponto | Decisão | Motivo |
|---|-------|---------|--------|
| P1 | O DDL de `ordering.orders` (arquivo 05) foi escrito antes do RF-047 e não tem os campos de presente | Colunas `is_gift`, `gift_wrap`, `gift_wrap_price` e `gift_message` no pedido. O valor da embalagem fica numa tabela de configuração da loja (`ordering.settings`), editável no painel, começando em **R$ 0** até a D-F08 | Congela o valor no checkout (RF-047 CA2) sem esperar a decisão de preço |
| P2 | O Mercado Pago precisa alcançar o webhook pela internet, e o ambiente de dev só existe dentro do Tailscale | Na VM, **Tailscale Funnel só para o caminho do webhook**, numa porta própria (10000): `/api/v1/webhooks/payments/` vai para a API, todo o resto continua privado. Na máquina do dev, sem webhook: a reconciliação (§5.5 do arquivo 21) e o gateway falso cobrem o fluxo | Testar o caminho real sem abrir a loja, o painel nem o Mailpit |
| P3 | Sem credenciais, ninguém consegue testar o fluxo de ponta a ponta na própria máquina | `Payments:Provider` = `MercadoPago` ou `Fake`, como no `Shipping:Provider`. O falso gera um QR de mentira e, só em Development, a tela do PIX mostra "Simular pagamento aprovado", que dispara o mesmo caminho do webhook | Testes funcionais e demonstração sem depender do sandbox |
| P4 | O sandbox do Mercado Pago, pelo que a documentação indica, não oferece um jeito de aprovar um PIX de teste | O caminho de aprovação é validado com **cartão de teste** no sandbox (mesmo webhook e mesma consulta), com o gateway falso e com respostas gravadas no WireMock. O PIX real aprovado fica para a **homologação com valor baixo** da Fase 6, que já está no roadmap | Não travar a fase num limite da plataforma. Confirmar na prática na 4.3 |
| P5 | O QR do PIX tem prazo próprio na plataforma (24 h por padrão), e o pedido vence em 48 h | `date_of_expiration` do PIX = `payment_due_at` do pedido. Pedir o PIX de novo **devolve o mesmo QR** enquanto ele estiver pendente | O QR não vence antes do pedido, e o cliente não junta vários PIX abertos para o mesmo pedido |
| P6 | O arquivo 21 tem status próprios, e o Mercado Pago tem outros | `approved` → Approved; `pending`, `in_process`, `authorized` → Pending; `rejected` → Rejected; `cancelled` com motivo de expiração → Expired, os outros → Cancelled; `refunded` → Refunded. `charged_back` e `in_mediation` **não mudam o pedido**: registram o evento e avisam a loja por e-mail | Contestação é tratada à mão no MVP |
| P7 | O arquivo 21 diz "valida a assinatura" sem detalhar | Cabeçalho `x-signature` (`ts` e `v1`) conferido com HMAC-SHA256 sobre `id:{data.id};request-id:{x-request-id};ts:{ts};` e o segredo do webhook, comparação em tempo constante. **Sem janela de tempo** (revisto na 4.3): a reentrega legítima chega horas depois, e reenviar uma notificação capturada não faz mal, porque o evento é deduplicado em `payment_events` pelo id da notificação e o pagamento é sempre consultado na plataforma | Formato documentado pelo Mercado Pago |
| P8 | `POST /orders/{id}/payments` recebe `Idempotency-Key`, mas `payments.payments` não tem onde guardar | Coluna `idempotency_key` com `UNIQUE (order_id, idempotency_key)`. Para o Mercado Pago, a chave enviada em `X-Idempotency-Key` é o `PaymentId` | Duplo clique em "Pagar" não gera duas cobranças, nem do nosso lado nem do lado da plataforma |
| P9 | A chave pública muda entre sandbox e produção, mas a imagem da loja é a mesma em todos os ambientes | A chave pública é lida **em tempo de execução** pelo servidor do Next (`MERCADOPAGO_PUBLIC_KEY`) e passada ao componente do Brick, sem `NEXT_PUBLIC_` | Uma imagem só, promovida de dev para produção (arquivo 18) |
| P10 | O Mercado Pago pede dados do pagador, e o CPF é opcional no cadastro (RF-005) | PIX: e-mail e nome da conta, CPF só se o cliente tiver preenchido. Cartão: o próprio Brick pede o CPF do titular. **O CPF não vira obrigatório no cadastro** | Não aumentar o atrito; confirmar no sandbox se o PIX aceita pagador sem CPF |
| P11 | Parcelamento ainda não foi decidido (D07) | `Payments:MaxInstallments` na configuração, começando em **1** (à vista). Juros e parcelas sem juros são configurados na conta do Mercado Pago, não no código | A D07 só é necessária antes do lançamento; o Brick já mostra o que a conta permitir |
| P12 | O e-mail EM-07 vai para a loja, mas não há endereço configurado para isso | `Email:StoreAddress`, obrigatório quando o provedor de e-mail é real | Sem ele o pedido pago não avisa ninguém |
| P13 | Frete fora do ar durante o checkout | O checkout aceita o preço de contingência e grava `shipping_is_fallback = true` (P4 da Fase 3). O serviço escolhido que deixou de existir na nova cotação responde `409 Ordering.ShippingServiceUnavailable` | O cliente não perde a venda porque o Melhor Envio caiu |
| P14 | O cliente pode cancelar pelo site (RF-043), mas o cancelamento pelo admin é da Fase 5 | 4.2 cancela pedido **não pago**; 4.5 libera o cancelamento de pedido pago com estorno. Até a Fase 5, a loja cancela pedido pago pelo painel do Mercado Pago, e o webhook de estorno atualiza o pedido | Cada fatia entrega algo que funciona sozinho |

## 4. Escopo planejado

### 4.1 Banco

| Script | Conteúdo | Fatia |
|--------|----------|-------|
| `V0017__customer_addresses.sql` | `customers.addresses` e índices (arquivo 05) | 4.1 |
| `V0018__ordering.sql` | Schema `ordering`, sequência, `orders` (com o presente, P1), `order_items`, `order_status_history`, `settings` | 4.2 |
| `V0019__payments.sql` | Schema `payments`, `payments` (com `idempotency_key`, P8) e `payment_events` | 4.3 |
| Seed `S0006__example_orders.sql` | Configuração com embalagem a R$ 0 e, só em desenvolvimento, um pedido de cada status para a tela de pedidos | 4.2 |

Cada script novo entra no `database/gerar-scripts.sh`, que gera um arquivo por fatia (`database/fase-4-1-schema.sql`, e assim por diante). A baixa de estoque usa os tipos `OrderPlaced` e `OrderCancelled`, que a V0013 já prevê.

### 4.2 Domain e Application

- **`Address`** dentro de `Customer`, com no máximo um padrão. CEP pelo `IPostalCodeLookup` da Fase 3.
- **`Order`** com a máquina de estados do arquivo 04 (`PendingPayment` → `PaymentConfirmed` → … / `Cancelled`), histórico em toda transição e concorrência otimista por `version`.
- **`PlaceOrder`** numa transação: trava os saldos em ordem de `variant_id` (evita deadlock), confere estoque e variação ativa (`409` com a lista), recalcula o frete, congela itens, endereço, frete e presente, baixa o estoque com movimentação `OrderPlaced`, esvazia a sacola e grava o EM-04 no Outbox. Exige e-mail confirmado (`422 Identity.EmailNotConfirmed`, D05).
- **`Payment`** com as transições do arquivo 21 §4 e a regra de um único aprovado (RN-102, índice parcial como última barreira).
- **`IPaymentGateway`** com `MercadoPagoGateway` (HTTP com `Microsoft.Extensions.Http.Resilience`, como no frete) e `FakePaymentGateway` (P3).
- **`ConfirmPayment`**, chamado pelo webhook, pela reconciliação e pelo "Simular pagamento": consulta a plataforma, confere valor e pedido e aplica `Payment.Approve()` e `Order.ConfirmPayment()` juntos. Pedido já cancelado → estorno pelo Outbox (RN-101).
- **Jobs** com `PeriodicTimer` e advisory lock, como o `MaintenanceJob`: cancelamento em 48 h (5 min), lembrete de 24 h (15 min) e reconciliação (15 min). Cancelar devolve o estoque (`OrderCancelled`), tenta cancelar o PIX pendente na plataforma (falha não impede) e grava o EM-10.
- **Estorno** como mensagem do Outbox, idempotente: 5 falhas → `RefundFailed` e e-mail para a loja.

### 4.3 API (`/api/v1`)

| Grupo | Rotas | Acesso e limite | Fatia |
|-------|-------|-----------------|-------|
| `me/addresses` | `GET`, `POST`, `PUT {id}`, `DELETE {id}`, `PUT {id}/default` | Customer, `customer-write` | 4.1 |
| `orders` | `POST /` (com `Idempotency-Key`), `GET /`, `GET {id}`, `POST {id}/cancel`, `GET settings` (valor da embalagem) | Customer, `customer-write` | 4.2 |
| `orders/{id}/payments` | `POST` (com `Idempotency-Key`), `GET current` | Customer, `customer-write` | 4.3 |
| `orders/{id}/payments/simulate-approval` | `POST`, **só em Development e com o gateway falso** | Customer | 4.3 |
| `webhooks/payments/{provider}` | `POST` | Anônimo, sem CORS, `public-read`, assinatura obrigatória (P7), responde `200` rápido | 4.3 |
| `admin/ordering/settings` | `GET`, `PUT` (valor da embalagem) | Admin, `admin-write` | 4.2 |

O `GET current` é consultado pela tela do PIX a cada poucos segundos; ele lê do banco e só vai à plataforma pela reconciliação. Pedido de outro cliente responde `404` em todas as rotas.

### 4.4 Frontend

**Loja:**
- `/conta/enderecos`: lista, novo, editar, remover e padrão, com o CEP preenchendo o resto.
- `/checkout` com `noindex`, um passo por tela (arquivo 26 §4.6): endereço → frete → presente e revisão → pagamento. A sacola continua sendo a fonte; o servidor recalcula tudo na hora de criar o pedido.
- **PIX:** QR grande, "Copiar código" com confirmação, contagem até o vencimento e troca automática para "Pagamento confirmado".
- **Cartão:** Card Payment Brick com a chave pública lida em tempo de execução (P9), recusa com a mensagem da plataforma e o botão para tentar de novo ou pagar com PIX.
- `/conta/pedidos` e `/conta/pedidos/[numero]`: lista, detalhe com status, pagamento, frete, presente e histórico; botão de cancelar quando permitido; botão para voltar ao pagamento quando o pedido ainda está aguardando.
- A tela de "compra online chega na próxima etapa" da Fase 3 sai.

**Painel:**
- Só o valor da embalagem para presente nesta fase. Pedidos, estornos pendentes e dashboard são da Fase 5.

O checkout segue o arquivo 26 §4.6, com uma versão rodando na loja para aprovação; se não agradar, as alternativas são mostradas do mesmo jeito.

**Fora desta fase, mas registrado:** a loja ainda não tem Content Security Policy. Quando entrar (Fase 6), ela precisa liberar os domínios do SDK e dos iframes do Mercado Pago.

### 4.5 Testes a escrever

| Projeto | Cobertura |
|---------|-----------|
| `Domain.UnitTests` | Endereço padrão único, máquina de estados do pedido, totais com presente, transições do pagamento, mapeamento de status do Mercado Pago (P6) |
| `Application.UnitTests` | Validators de endereço, checkout, dedicatória (200 caracteres e 6 linhas) e pagamento |
| `Api.FunctionalTests` | Duas compras simultâneas da última unidade (um pedido só); estoque insuficiente sem nada gravado; mesmo `Idempotency-Key` devolve o mesmo pedido; e-mail não confirmado → 422; admin → 403; pedido de outro cliente → 404; webhook com assinatura inválida → 401; webhook duplicado e fora de ordem; pagamento aprovado depois do cancelamento → estorno; segundo pagamento aprovado → estorno; job de 48 h e lembrete de 24 h com `FakeTimeProvider` |
| Adapter | WireMock.Net com criação de PIX e cartão, consulta, cancelamento, estorno, timeout e erro. Respostas gravadas do sandbox |
| `ArchitectureTests` | Domain sem dependência do Mercado Pago nem de HTTP |
| Frontend (Vitest) | Passos do checkout, máscara de CEP, cópia do código PIX, consulta que para quando o pagamento cai, estados de recusa |

## 5. Fora do escopo

- Painel de pedidos, mudança de status, rastreio, estornos pendentes e dashboard → Fase 5.
- E-mails EM-08 e EM-09 (enviado e entregue) → Fase 5, junto com as telas que mudam esses status.
- Desconto no PIX (D-F04): o componente de preço já está preparado, mas sem decisão a fase não muda o total.
- Boleto (D02), carteira do Mercado Pago, cartão salvo e compra como visitante (Q03).
- Credenciais de produção e pagamento real → homologação da Fase 6.
- Content Security Policy → Fase 6 (ver §4.4).

## 6. O que depende de você

| # | O que falta | Quando | Impacto se não chegar |
|---|-------------|--------|-----------------------|
| 1 | ✅ **Conta e aplicação no Mercado Pago**, com credenciais de teste | Feito em 08/11/2026 | — |
| 2 | **Contas de teste** no painel de desenvolvedores: uma compradora | Antes de validar a 4.3 | O pagamento real no sandbox fica sem validar |
| 3 | **Colocar as credenciais de teste** no `/opt/mo/.env` da VM e nos user-secrets da sua máquina. O roteiro mostra o comando; **o Access Token nunca vem para o chat** | Na 4.3 | A VM segue com o gateway falso |
| 4 | **Ligar o Funnel** na política do Tailscale (P2), cadastrar a URL do webhook na aplicação do Mercado Pago, com o evento "Pagamentos", e guardar a assinatura secreta no `.env` | Na 4.3 | Sem webhook, a confirmação depende da reconciliação a cada 15 min |
| 5 | **E-mail da loja** para receber o aviso de pedido pago (P12) | Na 4.3 | Em dev vai para o Mailpit; em produção é obrigatório |
| 6 | **D-F08**: a embalagem para presente é cobrada? Quanto? | Antes do lançamento | Começa grátis, editável no painel |
| 7 | **D07**: parcelamento | Antes do lançamento | Começa à vista (P11) |
| 8 | **Token do Melhor Envio** (pendente da Fase 3) | Antes de validar o checkout com frete real | O checkout funciona com o provedor falso |

Nada disso impede o início: 4.1 e 4.2 não dependem de nenhum item.

## 7. Como validar

Primeiro só build (A), depois banco pelo DBeaver (B) e por último execução (C). Cada fatia preenche as linhas dela. **A fase só fecha depois de rodar com banco real, API em container, build de produção e um pagamento de cartão aprovado no sandbox chegando pelo webhook na VM.**

### Critério de aceite

| # | Fatia | Verificação | Esperado |
|---|-------|-------------|----------|
| A1 | todas | `dotnet build` e `dotnet format --verify-no-changes` | Sem erro nem aviso |
| A2 | todas | `npm run lint`, `typecheck`, `test` e `build` na loja | Verde |
| B1 | todas | Scripts `fase-4-*-schema.sql` no DBeaver | Aplicados e registrados em `schemaversions` |
| C1 | 4.1 | CEP válido, inválido e de outro cliente | Campos preenchidos; erro no campo; `404` |
| C2 | 4.2 | Compra completa com o provedor de frete falso | Pedido `PED-…` em aguardando pagamento, estoque baixado com movimentação, sacola vazia, EM-04 no Mailpit |
| C3 | 4.2 | Duas abas comprando a última unidade | Um pedido; a outra aba vê `409` com a peça |
| C4 | 4.2 | Presente com embalagem e dedicatória | Valor no total, dedicatória no pedido e no EM-04, fora dos logs |
| C5 | 4.2 | Pedido com o prazo vencido (data ajustada no banco) | Cancelado em até 5 min, estoque de volta, EM-10 |
| C6 | 4.3 | PIX com o gateway falso e "Simular pagamento" | Tela muda sozinha; pedido confirmado; EM-06 e EM-07 |
| C7 | 4.3 | PIX no sandbox | QR e código válidos, vencimento igual ao do pedido |
| C8 | 4.3 | Webhook com assinatura errada e repetido | `401`; o repetido não muda nada |
| C9 | 4.4 | Cartão de teste aprovado no sandbox, pela VM | Pedido confirmado pelo webhook que entrou pelo Funnel |
| C10 | 4.4 | Cartão de teste recusado, depois PIX | Mensagem de recusa; o PIX funciona no mesmo pedido |
| C11 | 4.5 | Cliente cancela pedido pago com cartão de teste | Estorno no sandbox, pagamento `Refunded`, EM-10 com o estorno |
| C12 | 4.5 | Aprovação chegando depois do cancelamento | Estorno automático (RN-101) |
| C13 | todas | Celular de verdade pelo Tailscale | Checkout inteiro com o dedo, cópia do código PIX funcionando |

## 8. Situação por fatia

| Fatia | Situação |
|-------|----------|
| 4.1 — Endereços | Implementada e validada em 08/11/2026 |
| 4.2 — Pedido | Implementada e validada em 08/11/2026; falta aprovar o visual do checkout |
| 4.3 — PIX e webhook | Implementada e validada com a plataforma falsa em 08/11/2026; falta o sandbox (credenciais e Funnel na VM) |
| 4.4 — Cartão | Implementada e validada com o cartão de teste em 08/11/2026; o Brick de verdade depende da chave pública (C9, C10) |
| 4.5 — Estornos | Implementada e validada com a plataforma falsa em 08/11/2026; falta o estorno no sandbox (C11) |

### 4.1 — Validação (08/11/2026)

**Regras adotadas na fatia** (complementam o RF-007):
- Até **10 endereços** por cliente (`422 Customers.TooManyAddresses`).
- Sempre exatamente um padrão: o primeiro já nasce padrão, `isDefault` no cadastro troca o padrão, e apagar o padrão passa o posto para o endereço **mais recente**.
- A **UF precisa bater com a faixa do CEP** (a tabela fixa da Fase 3, P6). É a validação do CA2 sem chamada externa: o ViaCEP só preenche a tela, e salvar não depende dele.
- As rotas ficam em `/api/v1/me/addresses`, como no arquivo 07 §3.
- O caderno trava a linha do cliente durante a gravação; o índice parcial de padrão único é a última barreira.

| Item | Resultado |
|------|-----------|
| A1 | ✅ `dotnet build` com 0 avisos; `dotnet format` sem apontamentos além do fim de linha CRLF que o checkout no Windows já produz nos arquivos existentes |
| A2 | ✅ lint, typecheck, 183 testes da loja e build de produção contra o stub. Os tipos da API foram regenerados (`npm run gen:api`): estavam defasados desde a Fase 3 |
| Testes | ✅ domínio 177, aplicação 32, arquitetura 8, funcionais **245** (9 novos de endereço, incluindo dois padrões criados ao mesmo tempo) |
| B1 | ✅ `fase-4-1-schema.sql` aplicado pelo `psql` no banco local; a segunda execução recusa com "Script já aplicado"; o migrator do container vê o script como aplicado |
| C1 | ✅ API em container: cadastro 201 com padrão automático; UF errada → 400 com o erro em `state`; consulta de CEP 200. Na loja: CEP de Belo Horizonte preencheu rua, bairro, cidade e UF e levou o cursor ao número; novo endereço como padrão; apagar o padrão passou o posto ao outro; tela de 375 px sem rolagem lateral |
| Celular de verdade | ⏳ fica para o C13, junto com o checkout |

### 4.2 — Validação (08/11/2026)

**Ajustes ao plano feitos na fatia:**
- `shipping_days` aceita nulo: o preço de contingência não tem prazo (P13).
- O pedido guarda também `product_id`, `image_url` e a ordem dos itens, para a lista e o detalhe mostrarem a foto sem depender do catálogo atual.
- O total tem `CHECK (total = subtotal + shipping_fee + gift_wrap_price)` no banco, além do cálculo no domínio (RN-051).
- A configuração da embalagem nasce na própria migration (R$ 0), não no seed: produção também precisa da linha.
- Sem seed de pedidos de exemplo: o seed de desenvolvimento não tem cliente, e um pedido feito pela loja cobre a demonstração.
- Os dois jobs (48 h e 24 h) rodam juntos a cada 5 min, com `FOR UPDATE SKIP LOCKED`; o lembrete fica no máximo 5 min mais cedo que os 15 da spec.
- O valor da embalagem é lido pela cliente em `GET /orders/settings` (com login) e editado em `/admin/ordering/settings`. A tela do painel fica para a Fase 5, junto com os pedidos.
- `itemCount` da lista conta peças diferentes ("Anel Liso e mais 2"), não unidades.
- `StockMovement` passou a aceitar autor nulo (sistema) e o id do pedido; o painel de estoque continua aceitando só entrada, saída e ajuste.

| Item | Resultado |
|------|-----------|
| A1 | ✅ build com 0 avisos; `dotnet format` sem apontamentos além do CRLF |
| A2 | ✅ lint, typecheck, 187 testes da loja, build de produção contra o stub (`/checkout`, `/conta/pedidos`, `/conta/pedidos/[id]`) |
| Testes | ✅ domínio 186, aplicação 32, arquitetura 8, funcionais **259** (14 novos de checkout) |
| B1 | ✅ `fase-4-2-schema.sql` aplicado pelo `psql`; o migrator do container vê o script como aplicado |
| C2 | ✅ Na loja: sacola → checkout → endereço padrão → SEDEX → presente com embalagem e dedicatória → PED-000001, R$ 179,80 + R$ 35,96. Estoque 6 → 4 com movimentação `OrderPlaced`, sacola vazia, EM-04 no Mailpit |
| C3 | ✅ por teste funcional: duas clientes, última unidade, um pedido só (201 e 409) |
| C4 | ✅ dedicatória no pedido e no EM-04; valor da embalagem congelado (mudar depois não altera o pedido) |
| C5 | ✅ por teste funcional: prazo vencido → cancelado pelo job com `PaymentTimeout`, estoque de volta, EM-10. Lembrete de 24 h uma vez só |
| Cancelamento pela cliente | ✅ na loja: estoque de volta, histórico com as duas mudanças, botão some |
| API em container | ✅ pedido 201 e a mesma `Idempotency-Key` → 200 com o mesmo pedido; e-mail com horário de Brasília (o tzdata da imagem *chiseled-extra* funciona) |
| Celular | ✅ detalhe do pedido em 375 px sem rolagem lateral; ⏳ toque de verdade fica para o C13 |

**Observação:** o log da API em container mostra `libgssapi_krb5.so.2: cannot open shared object file`. É o Npgsql procurando a biblioteca de Kerberos, que a imagem *chiseled* não tem; não usamos Kerberos e as conexões funcionam. Não é desta fatia.

**Falta na 4.2:** aprovar o visual do checkout com a loja (uma versão, arquivo 26 §4.6). A tela de pagamento entra na 4.3; até lá, o pedido mostra o prazo e pede para falar com a MO.

### 4.3 — Validação (08/11/2026)

**Como ficou:**
- A notificação é conferida (P7), gravada em `payment_events` (a repetida é ignorada) e só então vira uma mensagem `payments.sync` do Outbox, que consulta a plataforma e aplica. Se a notificação chega antes do commit de quem criou o pagamento, o Outbox tenta de novo depois.
- A reconciliação roda no mesmo job do pedido, a cada 5 min, para pendentes sem mudança há mais de 10 min.
- Cancelar um pedido com PIX aberto manda cancelar o PIX na plataforma (`payments.cancel`, por cortesia: se falhar, ele vence sozinho).
- **RN-101 e RN-102 nesta fatia só avisam a loja** (EM-12, "Atenção: pagamento do pedido…"): pagamento aprovado depois do cancelamento, segundo pagamento aprovado ou valor diferente do total. O estorno automático é a 4.5.
- Contestação e chargeback (`in_mediation`, `charged_back`) não mudam nada e avisam a loja (P6).
- A chave enviada ao Mercado Pago em `X-Idempotency-Key` é `pedido + Idempotency-Key da cliente`: a mesma tentativa repetida não cobra duas vezes nem se a primeira resposta se perdeu (P8).
- O EM-07 e o EM-12 vão para `Email:StoreAddress` (P12). Em desenvolvimento é `loja@mo.local`, no Mailpit; sem o endereço, o aviso é descartado com log.
- O cartão já funciona na API (o Brick fica para a 4.4): token, meio, emissor e parcelas, limitadas por `Payments:MaxInstallments` (1 por padrão, P11).
- A VM continua com a plataforma falsa até as credenciais entrarem no `.env`; o passo a passo está em `deploy/dev/README.md` ("Pagamentos com o Mercado Pago"), junto com a política do Tailscale que libera o Funnel só para a VM.

| Item | Resultado |
|------|-----------|
| A1 | ✅ build com 0 avisos; `dotnet format` sem apontamentos além do CRLF |
| A2 | ✅ lint, typecheck, 187 testes da loja, build de produção contra o stub |
| Testes | ✅ domínio 191, aplicação 32, arquitetura 8, funcionais **283** (11 de pagamento com a plataforma falsa e 13 do adaptador do Mercado Pago contra WireMock, incluindo a assinatura do webhook) |
| B1 | ✅ `fase-4-3-schema.sql` aplicado pelo `psql`; o migrator do container vê o script como aplicado |
| C6 | ✅ Na loja: "Pagar com PIX" → QR, copia e cola e prazo → "Simular pagamento aprovado" → a tela muda sozinha para "Pagamento confirmado"; EM-06 para a cliente e EM-07 para `loja@mo.local` no Mailpit |
| C7 | ⏳ Falta o sandbox: QR válido e vencimento igual ao do pedido. Com a plataforma falsa, o vencimento já sai igual ao `payment_due_at` |
| C8 | ✅ Webhook sem assinatura → 401 (teste e container); assinatura de outro segredo ou de outro pagamento → recusada; repetido → não muda nada |
| API em container | ✅ PIX gerado e webhook sem assinatura com 401 |

**Para fechar a 4.3:** credenciais de teste e assinatura secreta no `/opt/mo/.env`, Funnel ligado, URL cadastrada no Mercado Pago (roteiro no `deploy/dev/README.md`). Depois, conferir C7 e C9 e gravar respostas reais do sandbox nos testes do adaptador.

### 4.5 — Validação (08/11/2026)

**Como ficou:**
- A cliente cancela o pedido pago (RF-043 CA1); em separação, só a loja (Fase 5). O estoque volta na hora e o estorno vai para o Outbox (`payments.refund`), que tenta de novo se a plataforma estiver fora.
- **RN-101 e RN-102 agora estornam sozinhos** (na 4.3 só avisavam): a sincronização estorna direto, sem gravar o pagamento como "aprovado", porque o índice de um aprovado por pedido não deixaria. Valor pago diferente do total continua só avisando a loja.
- Estorno aceito → pagamento `Refunded` e EM-13 para a cliente ("Estorno do pedido…"). Recusado pela plataforma → `RefundFailed` e EM-12 para a loja estornar pelo painel do Mercado Pago. O EM-10 do pedido pago cancelado avisa que o valor será estornado.
- **Limite conhecido:** se a plataforma ficar fora durante todas as tentativas do Outbox (5, com espera crescente), a mensagem fica com `failed_at` e o pagamento continua `Approved`, sem aviso por e-mail. A lista de estornos pendentes do painel (Fase 5) e o alerta de `failed_at` (Fase 6) cobrem esse caso.

| Item | Resultado |
|------|-----------|
| A1 | ✅ build com 0 avisos; `dotnet format` sem apontamentos além do CRLF |
| A2 | ✅ lint, typecheck, 187 testes da loja, build de produção contra o stub |
| Testes | ✅ domínio 191, aplicação 32, arquitetura 8, funcionais **287** (5 novos de estorno: pedido pago cancelado, RN-101, RN-102, estorno recusado, plataforma fora e nova tentativa) |
| C11 | ✅ com a plataforma falsa, na API em container: cartão aprovado → cancelamento → pagamento `Refunded`, EM-10 e EM-13 no Mailpit. ⏳ falta no sandbox |
| C12 | ✅ por teste funcional: aprovação depois do cancelamento → estorno, pedido continua cancelado |

### 4.4 — Validação (08/11/2026)

**Como ficou:**
- O pedido tem as abas **PIX** e **Cartão de crédito**. Com `MERCADOPAGO_PUBLIC_KEY` na loja, o cartão é o **Card Payment Brick** do Mercado Pago, carregado só quando a cliente escolhe cartão; número e CVV nunca passam pela loja (SAQ-A).
- A chave pública é lida **a cada requisição** pela página do pedido (P9): a mesma imagem serve sandbox e produção. No VM ela entra no `.env` junto com as outras (roteiro no `deploy/dev/README.md`).
- Sem a chave e com a plataforma falsa, aparece o **cartão de teste** ("aprovado" e "recusado"), só em desenvolvimento. Sem a chave e com o Mercado Pago de verdade, o cartão aparece como indisponível.
- O documento do titular vem do Brick e vai para o Mercado Pago no lugar do CPF do cadastro (P10); a API confere 11 dígitos (CPF) ou 14 (CNPJ).
- `GET /api/v1/payments/options` diz quantas parcelas o Brick oferece (`Payments:MaxInstallments`, 1 até a D07) e se há simulação.
- Recusa mostra o motivo em português (`card-messages.ts`, a partir do `status_detail`) e deixa tentar de novo, com cartão ou PIX (CA3). Cartão em análise (`in_process`) mostra o aviso e a tela consulta até a aprovação.

| Item | Resultado |
|------|-----------|
| A1, A2 | ✅ build, format, lint, typecheck; 189 testes da loja; build de produção contra a API local |
| Testes | ✅ funcionais de pagamento 30 (opções, documento do titular inválido e válido) e adaptador 14 (corpo do cartão com token, parcelas, emissor e documento) |
| C10 | ✅ com o cartão de teste, na loja: recusado → "O cartão não tem limite para esta compra" e as abas continuam; aprovado → "Pagamento confirmado" na hora |
| C9 | ⏳ cartão de teste do Mercado Pago no sandbox, pela VM, com a confirmação chegando pelo webhook do Funnel |
| Primeira carga | ✅ com o build de produção, abrir o link do pedido direto vai para o pedido. O desvio para a home visto duas vezes era da primeira compilação do servidor de desenvolvimento |

## 9. Situação da fase (08/11/2026)

Código das cinco fatias pronto, testado e commitado em `feat/fase-4` nos dois repositórios, **sem merge na `main`** (o merge publica na VM). O que falta não é código:

| # | O que falta | Quem |
|---|-------------|------|
| 1 | Credenciais de teste, assinatura secreta e chave pública no `/opt/mo/.env`; Funnel e URL do webhook no Mercado Pago (roteiro no `deploy/dev/README.md`) | Você |
| 2 | Conta compradora de teste no Mercado Pago, para C9 e C11 | Você |
| 3 | Rodar C7, C9 e C11 no sandbox e gravar respostas reais nos testes do adaptador | Eu, depois do 1 e do 2 |
| 4 | Aprovar o visual do checkout e do pagamento | Loja |
| 5 | Celular de verdade (C13): checkout com o dedo e cópia do código PIX | Você |
| 6 | Rodar `fase-4-1-schema.sql` a `fase-4-3-schema.sql` no DBeaver, se for usar o banco local por ele (o migrator do container já aplica sozinho) | Você |

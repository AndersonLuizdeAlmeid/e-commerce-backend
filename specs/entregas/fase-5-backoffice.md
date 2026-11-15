# Entrega — Fase 5: Backoffice de pedidos e usuários

**Status:** Planejada e implementada em 15/11/2026. **As quatro fatias estão validadas com banco real, API em container, e-mails no Mailpit e as telas no navegador** (seção 8). Falta aprovar as telas com a loja, definir o link de rastreio (§6) e testar no celular de verdade.
**Repositórios:** `e-commerce-backend` e `e-commerce-frontend` (branch `feat/fase-5` nos dois).

## 1. Contexto

Depois da Fase 4, a cliente compra e paga sozinha, mas a loja não enxerga os pedidos: o aviso de pedido pago (EM-07) chega por e-mail e o resto é feito no painel do Mercado Pago. A Fase 5 dá à administradora o caminho do pedido pago até a entrega (separar, enviar com rastreio, entregar), o cancelamento com estorno pela loja, a fila de estornos que deram errado, a gestão de usuários e um dashboard para começar o dia.

A entrega é feita em **quatro fatias**, cada uma com build verde e checklist próprio.

| Fatia | Conteúdo | Requisitos | Depende de |
|-------|----------|------------|------------|
| **5.1 — Pedidos** | Lista com filtros, detalhe com histórico e pagamentos, separar → enviar (rastreio) → entregar, cancelar pela loja com estorno, confirmação manual com motivo, correção da dedicatória, e-mails EM-08 e EM-09, valor da embalagem no painel | RF-053, RF-047 CA3 | — |
| **5.2 — Estornos pendentes** | Fila de dinheiro preso: estorno recusado, estorno que o Outbox desistiu e pagamento confirmado à mão em pedido cancelado; tentar de novo, conferir na plataforma e marcar devolução feita fora do sistema | RF-055 (estornos pendentes), arquivo 21 §5.4 | 5.1 |
| **5.3 — Usuários** | Lista com busca, ativar/desativar e promover/rebaixar, sem a administradora se trancar fora e sempre com uma administradora ativa | RF-054 | — |
| **5.4 — Dashboard** | Pedidos por situação, faturamento, ticket médio, o que fazer hoje, estoque baixo e estornos pendentes, por período | RF-055 | 5.1, 5.2 |

5.1 e 5.3 são independentes. Decisões que a entrega aplica: Q06 (admin não compra), arquivo 04 §4.8 (máquina de estados), arquivo 07 §9, arquivo 21 §5.4, arquivo 23 (EM-08, EM-09) e arquivo 26 §4.8 (painel em modo *Operate*).

## 2. Objetivos

1. A administradora **separa e envia um pedido pelo celular** sem abrir outro sistema: vê o que foi pago, a dedicatória em destaque, marca a separação e cola o rastreio.
2. **A cliente fica sabendo de cada passo** por e-mail: enviado com o rastreio e entregue.
3. **Nenhuma transição inválida passa** (`422`), e toda mudança registra quem fez, quando e por quê.
4. **Nenhum dinheiro fica preso sem a loja saber**: todo pagamento aprovado de pedido cancelado aparece numa fila até ser devolvido.
5. **A loja nunca fica sem administradora** e ninguém se tranca fora do painel.
6. O dashboard responde, numa olhada, **"quanto vendi" e "o que preciso fazer hoje"**.

## 3. Decisões que esta fase precisa fechar

| # | Ponto | Decisão | Motivo |
|---|-------|---------|--------|
| P1 | A confirmação manual (RF-053 CA1) muda o pedido, mas não existe pagamento que a sustente: o dashboard, o estorno e a regra de um aprovado por pedido (RN-102) ficariam cegos | Confirmar à mão grava um **pagamento `Manual`** aprovado com o valor do pedido e o motivo no `status_detail` (método novo, provedor `manual`). PIX pendente do mesmo pedido é cancelado na plataforma; se ele for aprovado mesmo assim, cai na regra do segundo aprovado e é estornado sozinho | Uma fonte só para "o que foi pago", e as regras da Fase 4 continuam valendo sem exceção |
| P2 | Cancelar um pedido confirmado à mão pede estorno de um pagamento que não passou pela plataforma | O pagamento `Manual` fica na fila de estornos pendentes (5.2) com a ação **"Já devolvi"**, que pede a nota de como foi devolvido. A cliente recebe o EM-13 nesse momento | O sistema não finge que devolveu um dinheiro que não controla |
| P3 | Formato do código de rastreio: Correios usa `AA123456789BR`, mas o Melhor Envio também despacha por outras transportadoras | Aceita de **8 a 40 letras e números**, sem espaços, gravado em maiúsculas. Corrigir o rastreio de pedido já enviado é permitido e entra no histórico, sem novo e-mail | Não travar o envio por uma transportadora diferente |
| P4 | O EM-08 pede "link de rastreio", e o link depende da transportadora | `Ordering:TrackingUrlTemplate` na configuração (ex.: `https://...?{code}`). **Vazio**: o e-mail e a página do pedido mostram o código e a transportadora, sem link | O padrão de link é decisão da loja (§6) e não pode ser chutado |
| P5 | O motivo do cancelamento pela loja é texto livre e pode ter informação interna | O motivo vai para o **histórico** (visível só no painel). O EM-10 diz "pela loja", como já faz | Nota interna não vaza para a cliente |
| P6 | "Estornos pendentes" (arquivo 07) cobre só a falha, mas há três jeitos de dinheiro ficar preso | A fila junta: pagamento `RefundFailed`; pagamento `Approved` de pedido **cancelado há mais de 30 min** (o Outbox desistiu ou ainda não terminou); e pagamento `Manual` aprovado de pedido cancelado (P2). Ações: **tentar de novo** (nova mensagem de estorno), **conferir na plataforma** (sincroniza; se o estorno foi feito pelo painel do Mercado Pago, o pagamento vira `Refunded`) e **já devolvi** (só para `Manual`) | A plataforma é a fonte da verdade; marcação à mão só onde ela não existe |
| P7 | Desativar ou rebaixar alguém precisa valer na hora, não no fim do token | A mudança troca o **security stamp** e revoga todos os refresh tokens: o próximo request do token antigo já recebe `401`/`403` | O mecanismo da Fase 2A já confere o stamp em toda rota protegida |
| P8 | RF-054 CA2 ("sempre ≥ 1 admin ativo") sob concorrência: duas administradoras se rebaixando ao mesmo tempo | A conferência trava as linhas das administradoras ativas (`FOR UPDATE`) antes de contar | Sem isso, as duas passam e a loja fica sem ninguém |
| P9 | A administradora criada pelo seed não tem cadastro de cliente; rebaixada, ela não conseguiria usar a conta | Rebaixar cria o cadastro de cliente que faltar, com o nome tirado do e-mail (editável em Minha conta) | Rebaixar não pode quebrar a conta |
| P10 | Faturamento: por data do pedido ou do pagamento? E o que foi estornado? | Faturamento = soma dos pagamentos **aprovados no período** (`approved_at`) e que continuam aprovados; o que foi estornado aparece à parte. Ticket médio = faturamento ÷ pedidos pagos. Dias no fuso de **São Paulo**; padrão: últimos 30 dias | É o dinheiro que entrou de fato, conferível com o extrato do Mercado Pago |
| P11 | A home do painel hoje cai em Produtos | `/admin` passa a ser o **dashboard** | É a primeira coisa que a administradora precisa ver ao abrir o painel |

## 4. Escopo planejado

### 4.1 Banco

| Script | Conteúdo | Fatia |
|--------|----------|-------|
| `V0020__backoffice.sql` | Método `Manual` no `CHECK` de `payments.payments`; índice de pagamentos aprovados por `approved_at` (dashboard); índice de usuários por papel e data | 5.1 |

O script entra no `database/gerar-scripts.sh` como `database/fase-5-schema.sql`. O rastreio já tem coluna (`tracking_code`, V0018) e o histórico já tem `changed_by` e `note`.

### 4.2 Domain e Application

- **`Order`**: `StartPreparation`, `Ship(trackingCode)`, `UpdateTrackingCode`, `Deliver`, `CorrectGiftMessage` (até enviado) e `ConfirmPayment` manual com nota obrigatória. Cada um gera histórico com autor e nota; transição inválida → `422 Ordering.InvalidTransition`.
- **`Payment.RecordManual`** (P1) e `MarkRefundedManually` (P2).
- **Cancelamento pela loja** reaproveita o `OrderCancellation` da Fase 4 (estoque de volta, PIX cancelado, estorno pelo Outbox, EM-10), com motivo `AdminRequest` e a nota no histórico.
- **`User`**: `Activate`, `Deactivate`, `ChangeRole`, todos trocando o stamp (P7). Regras CA1 e CA2 no handler, com a trava de P8.
- **Consultas** do painel em SQL direto (Dapper), como as listas da Fase 3.

### 4.3 API (`/api/v1/admin`, Admin)

| Rotas | Limite | Fatia |
|-------|--------|-------|
| `GET orders?status&from&to&search&paymentMethod&gift&page`, `GET orders/{id}` | — | 5.1 |
| `POST orders/{id}/start-preparation`, `ship` `{ trackingCode }`, `tracking` `{ trackingCode }`, `deliver`, `cancel` `{ reason }`, `confirm-payment` `{ reason }`, `PUT orders/{id}/gift-message` `{ message }` | `admin-write` | 5.1 |
| `GET payments/refunds/pending`, `POST payments/{id}/refund`, `POST payments/{id}/sync`, `POST payments/{id}/mark-refunded` `{ note }` | `admin-write` nas escritas | 5.2 |
| `GET users?role&isActive&search&page`, `PATCH users/{id}/status` `{ isActive }`, `PATCH users/{id}/role` `{ role }` | `admin-write` nas escritas | 5.3 |
| `GET dashboard?from&to` | — | 5.4 |

`search` nos pedidos procura por número (`PED-000123` ou `123`), nome ou e-mail da cliente. Todas as escritas respondem o pedido (ou usuário) atualizado, para a tela não precisar consultar de novo.

### 4.4 Frontend (painel)

- **`/admin`**: dashboard (P11), com o período em atalhos (hoje, 7 dias, 30 dias, mês) e cartões que levam à lista filtrada.
- **`/admin/pedidos`**: tabela densa com filtros visíveis (situação em abas com contagem, período, busca, forma de pagamento, só presentes), pedido de presente marcado na linha, paginação. No celular, cada pedido vira um cartão.
- **`/admin/pedidos/[id]`**: o que fazer agora no topo (uma ação principal por situação), **dedicatória em destaque** (arquivo 26 §4.8), peças com foto, SKU e variação, endereço com botão de copiar, pagamentos e histórico com autor e nota. Cancelar e confirmar à mão pedem o motivo num diálogo. Versão de impressão para separar o pedido.
- **`/admin/estornos`**: a fila do P6 com as ações por linha.
- **`/admin/usuarios`**: busca, filtro por papel e situação, ações por linha com confirmação; a própria linha sem ações (CA1).
- **Configurações do pedido**: o valor da embalagem para presente (pendente da Fase 4) numa seção de `/admin/pedidos`.
- Menu do painel com Pedidos e Estornos (com contagem do que espera ação) e Usuários.

### 4.5 Testes a escrever

| Projeto | Cobertura |
|---------|-----------|
| `Domain.UnitTests` | Todas as transições permitidas e proibidas da tabela do arquivo 04, rastreio (formato, maiúsculas), dedicatória até enviado, pagamento manual, regras do usuário |
| `Application.UnitTests` | Validators de motivo, rastreio, filtros e período |
| `Api.FunctionalTests` | Fluxo completo pago → separar → enviar → entregar com EM-08 e EM-09; enviar sem rastreio → 400; pular etapa → 422; cancelar em separação com estorno; confirmação manual com e sem PIX aberto; fila de estornos com as três origens; rebaixar a si mesma → 422; última admin → 422; duas admins se rebaixando ao mesmo tempo → uma passa; token antigo de usuário desativado → 401; cliente → 403 em todas as rotas; dashboard com pagamentos dentro e fora do período |
| Frontend (Vitest) | Filtros na URL, ação principal por situação, formato do rastreio, valores do dashboard |

## 5. Fora do escopo

- Etiqueta de envio pela API e atualização automática pela transportadora → backlog pós-lançamento.
- Estorno parcial e troca de peça → tratados à mão no MVP.
- EM-11 (resumo diário de estoque baixo) → Fase 6, junto com os alertas; o dashboard já mostra o estoque baixo.
- Exportar pedidos (CSV) e gráficos de série → backlog.
- Editar dados da cliente pelo painel (LGPD: a cliente edita os próprios dados) → fora.

## 6. O que depende de você

| # | O que falta | Quando | Impacto se não chegar |
|---|-------------|--------|-----------------------|
| 1 | **Link de rastreio** (P4): que transportadoras a MO vai usar e qual página de rastreio mandar para a cliente | Antes do lançamento | O e-mail e a página mostram só o código |
| 2 | Aprovar as telas do painel com a loja (uma versão) | Ao fim da 5.1 | — |

Nada disso impede o início.

## 7. Como validar

Primeiro só build (A), depois banco (B) e por último execução (C). **A fase só fecha depois de rodar com banco real, API em container e o build de produção da loja.**

### Critério de aceite

| # | Fatia | Verificação | Esperado |
|---|-------|-------------|----------|
| A1 | todas | `dotnet build` e `dotnet format --verify-no-changes` | Sem erro nem aviso |
| A2 | todas | `npm run lint`, `typecheck`, `test` e `build` na loja | Verde |
| B1 | 5.1 | `fase-5-schema.sql` no banco | Aplicado e registrado em `schemaversions` |
| C1 | 5.1 | Pedido pago → separar → enviar com rastreio → entregar | Histórico com autor; EM-08 com o código e EM-09 no Mailpit; a cliente vê tudo em Meus pedidos |
| C2 | 5.1 | Pular etapa e enviar sem rastreio | `422` e `400` com erro no campo |
| C3 | 5.1 | Cancelar pedido em separação | Estoque de volta, estorno pelo Outbox, EM-10 "pela loja", motivo só no painel |
| C4 | 5.1 | Confirmar à mão pedido com PIX aberto | Pedido confirmado, pagamento `Manual`, PIX cancelado, EM-06 (o EM-07 não sai: foi a própria loja que confirmou) |
| C5 | 5.1 | Corrigir a dedicatória antes e depois do envio | Antes: muda. Depois: `422` |
| C6 | 5.2 | Estorno recusado (plataforma falsa) e "tentar de novo" | Aparece na fila; some quando o estorno passa |
| C7 | 5.2 | Pedido confirmado à mão e depois cancelado → "Já devolvi" | Na fila até a marcação; EM-13 na marcação |
| C8 | 5.3 | Rebaixar a si mesma; rebaixar a última admin; desativar uma cliente logada | `422`; `422`; a sessão da cliente cai no próximo request |
| C9 | 5.4 | Dashboard com pagamentos em dias diferentes | Faturamento e ticket batem com a soma manual; estornado à parte |
| C10 | todas | Painel no celular (375 px) | Separar e enviar um pedido sem rolagem lateral |

## 8. Situação por fatia

| Fatia | Situação |
|-------|----------|
| 5.1 — Pedidos | Implementada e validada em 15/11/2026; falta aprovar as telas e o celular de verdade (C10) |
| 5.2 — Estornos pendentes | Implementada e validada em 15/11/2026 (C6 só nos testes funcionais: a plataforma falsa do container não recusa estorno) |
| 5.3 — Usuários | Implementada e validada em 15/11/2026 |
| 5.4 — Dashboard | Implementada e validada em 15/11/2026 |

### Ajustes ao plano feitos na implementação

- **A policy de cliente passou a conferir o security stamp**, como já fazia a de admin (arquivo 08 §2, RN-092). Sem isso, a cliente desativada continuava comprando até o token vencer (15 min). O painel apaga o stamp em cache logo depois da mudança, então o próximo request já cai (`403`).
- **Cancelar em separação estorna**: o cancelamento compartilhado da Fase 4 só estornava a partir de "pagamento confirmado", porque a cliente não cancela em separação. Com a loja cancelando nessa situação, "em separação" também conta como pago.
- **Estorno recusado pode ser pedido de novo**: a mensagem `payments.refund` aceita também `RefundFailed`, que é o "tentar de novo" da fila.
- **Confirmação manual não manda o EM-07** (aviso de pedido pago à loja): quem confirmou foi a própria loja.
- **Rastreio corrigido e dedicatória corrigida** entram no histórico como linha sem troca de situação; a dedicatória corrigida não vai para a nota (RF-047 CA4).
- O detalhe do pedido da cliente (`GET /orders/{id}`) ganhou `shipping.trackingUrl`, e a página "Meus pedidos" mostra o código e o link.
- **Tipos da loja regenerados** (`npm run gen:api`) com a API em container.

### Validação (15/11/2026)

| Item | Resultado |
|------|-----------|
| A1 | ✅ `dotnet build` com 0 avisos; `dotnet format` sem apontamentos além do fim de linha CRLF, como nas fases anteriores |
| A2 | ✅ lint, typecheck, 202 testes da loja (8 novos) e build de produção contra a API em container |
| Testes | ✅ domínio 211 (22 novos), aplicação 32, arquitetura 8, funcionais **306 de 307** (15 novos, todos verdes). A falha foi `MercadoPagoGatewayTests.Get_MapsTheStatus(charged_back)`, um timeout de 15 s do WireMock com a suíte inteira rodando; sozinho, o adaptador passa nos 16 testes. Não foi mexido nesta fase |
| B1 | ✅ `fase-0` a `fase-5-schema.sql` aplicados em ordem por `psql` num banco vazio; a segunda execução do `fase-5` recusa com "Script já aplicado"; o migrator do container aplicou a V0020 no banco local |
| C1 | ✅ API em container: pago → separar → enviar (`qb123456789br` gravado como `QB123456789BR`) → entregar; EM-08 com o rastreio e EM-09 no Mailpit; histórico com a administradora como autora; a cliente vê "Entregue" e o rastreio. Pela tela: separar e enviar o PED-000006, com o erro do rastreio inválido aparecendo no campo |
| C2 | ✅ pular etapa → `422 Ordering.InvalidTransition`; rastreio `12 34` → `400` em `trackingCode` |
| C3 | ✅ cancelado em separação: estoque de volta, pagamento `Refunded`, EM-10 com "pela loja" e sem o motivo |
| C4 | ✅ sem motivo → `400`; com motivo, pagamento `Manual` aprovado, EM-06 no Mailpit |
| C5 | ✅ dedicatória corrigida antes do envio; depois → `422` |
| C6 | ✅ nos testes funcionais (recusa → fila → nova tentativa → `Refunded` → sai da fila) |
| C7 | ✅ manual + cancelado aparece como `ManualPayment`; "Já devolvi" → `204`, sai da fila, EM-13 no Mailpit |
| C8 | ✅ rebaixar a si mesma → `422`; cliente desativada → `403` no request seguinte e login `401`. Duas administradoras se rebaixando ao mesmo tempo → uma só passa (teste funcional) |
| C9 | ✅ faturamento R$ 888,88 e ticket R$ 111,11 iguais à soma direta no banco; estornado à parte |
| C10 | ✅ detalhe do pedido em 375 px sem rolagem lateral, ação principal no topo; lista vira cartão abaixo de 1280 px. ⏳ celular de verdade |

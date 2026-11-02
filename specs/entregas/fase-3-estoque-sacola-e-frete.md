# Entrega — Fase 3: Estoque, sacola e frete

**Status:** Planejada em 02/11/2026. **Fatias 3.1 e 3.2 implementadas e validadas com banco, API em container e build de produção em 02/11/2026** (seção 8); na 3.1 faltam a câmera e o leitor USB de verdade. **Fatia 3.3 implementada com provedor falso, sem validação contra o Melhor Envio de verdade**, que depende do token de sandbox. **Fatia 3.4 implementada e validada com banco, API em container e build de produção.**
**Repositórios:** `e-commerce-backend` e `e-commerce-frontend` (branch `feat/fase-3` nos dois).

## 1. Contexto

Depois da 2B, a loja mostra e cadastra peças, mas ainda não dá para comprar: toda peça criada no painel nasce com estoque zero e o botão "Adicionar à sacola" está desligado. A Fase 3 leva até o ponto anterior ao pagamento: a loja controla o estoque, o cliente monta a sacola e vê o frete real. O pedido e o pagamento são da Fase 4.

A entrega é feita em **quatro fatias**, cada uma com build verde e checklist próprio.

| Fatia | Conteúdo | Requisitos | Depende de |
|-------|----------|------------|------------|
| **3.1 — Estoque** | Movimentações, tela de estoque e entrada por leitura de código de barras | RF-052, RF-059 | — |
| **3.2 — Sacola** | Sacola do visitante e do cliente, mescla no login, tela `/carrinho`, botão de adicionar | RF-030, RF-031 | 3.1 (saldo real para os alertas) |
| **3.3 — Cotação de frete** | Melhor Envio, configuração da loja, contingência, simulador no produto e na sacola | RF-032, parte do RF-058 | 3.2 (usa os itens da sacola) |
| **3.4 — Regras de frete** | Regras do admin aplicadas sobre a cotação e aviso de frete grátis | RF-058 | 3.3 |

Decisões que a entrega aplica: D03 (Melhor Envio), D08 (regras dinâmicas, loja lança sem nenhuma), D10 (só ler e cadastrar EAN), RN-040 (até 50 itens, 1 a 99 por item) e arquivo 26 §4.5 (sacola).

## 2. Objetivos

1. A administradora **dá entrada no estoque sozinha**, digitando ou lendo o código de barras, e cada mudança fica registrada com motivo e autor.
2. O saldo **nunca fica negativo**, nem com duas operações ao mesmo tempo.
3. Qualquer pessoa **monta a sacola sem conta**, e os itens não se perdem ao entrar.
4. A sacola mostra **sempre o preço atual** e avisa quando um item esgotou, sem reservar estoque.
5. O cliente **vê PAC e SEDEX com preço e prazo** pelo CEP, no produto e na sacola, mesmo com o provedor fora do ar.
6. A administradora **cria promoções de frete sem ajuda técnica**, e o valor exibido nunca depende de dados vindos do navegador.

## 3. Decisões que esta fase precisa fechar

As specs deixam estes pontos abertos ou contraditórios. Cada linha traz a decisão adotada; as specs citadas são ajustadas junto com a fatia.

| # | Ponto | Decisão | Motivo |
|---|-------|---------|--------|
| P1 | O visitante guarda só `variantId` e quantidade, e `GET /cart` exige login. Não há como ele ver preço e foto | Rota nova e pública: `POST /catalog/variants/lookup` `{ items: [{ variantId, quantity }] }`, até 50, com a mesma resposta da sacola. A quantidade vai junto porque decide o aviso de estoque insuficiente | Arquivo 16 §6 exige preço e disponibilidade vindos da API. Sem isso, a sacola do visitante não funciona |
| P2 | `Policies.Customer` exige o papel Customer, então admin logado receberia 403 em `/cart` | Admin usa a sacola local, como visitante, sem chamar `/cart` nem `/cart/merge` | Evita erro depois do login e não muda a autorização |
| P3 | A chave de cache da cotação (arquivo 12) é `cep + pacotes`, e a regra de frete grátis depende do subtotal | **Só a resposta do provedor é cacheada**, com o valor declarado na chave. As regras são aplicadas depois do cache, a cada chamada | Com a chave da spec, dois carrinhos com o mesmo pacote e subtotais diferentes receberiam o mesmo preço |
| P4 | Contingência foi descrita como regra (arquivo 22 §3.3), mas os valores estão em `shipping.settings` | Fica só em `settings`. As regras também se aplicam sobre a contingência, e a resposta traz `isFallback: true`. Contingência **não vai para o cache** | Uma falha de segundos não pode fixar preço de contingência por 30 min, e a promessa de frete grátis continua valendo |
| P5 | "Ocultar serviço" aparece na tabela e no DDL, mas não na lista de efeitos do arquivo 22 | `HideService` é efeito válido | O DDL já prevê |
| P6 | Regra por UF precisaria consultar o ViaCEP dentro da cotação | UF sai de uma **tabela fixa de faixas de CEP** no Domain, sem chamada externa | Regra de preço não pode falhar porque outro serviço caiu |
| P7 | `Adjustment` no DDL é delta com sinal, mas quem conta a prateleira sabe o total | Na API, `In` e `Out` recebem quantidade positiva; `Adjustment` recebe o **novo saldo**. O servidor calcula o delta com a linha travada e grava o delta | Evita conta de cabeça e erro quando o saldo mudou durante a contagem |
| P8 | Falta rota para ver o histórico que o RF-052 manda registrar | Rota nova: `GET /admin/stock/{variantId}/movements` | Sem ela, o registro não tem utilidade para a loja |
| P9 | Spec prevê caixa padrão, mas o DDL não tem as colunas, e bolsa não cabe em caixa de anel | `settings` ganha peso e medidas da caixa. O pacote soma os pesos com o da caixa; cada medida é a maior entre a caixa e a maior peça, e a altura soma as peças que não cabem na caixa padrão | Estimativa simples, sem subcotar frete de bolsa. Ver riscos |
| P10 | Sem CEP de origem não há cotação, e o DDL não aceita linha incompleta | Sem configuração, a cotação responde `503 Shipping.NotConfigured` e a loja mostra "Fale com a MO para saber o frete". O seed de desenvolvimento cria a linha; produção é configurada pelo painel | Não inventa CEP de origem nem preço em produção |
| P11 | Várias regras podem valer ao mesmo tempo | Aplicadas por `priority` crescente e acumuladas, com piso em R$ 0. `FreeShipping` e `HideService` encerram a cadeia daquele serviço. A resposta troca `appliedRule` por `appliedRules[]` | Resultado previsível e testável |
| P12 | Toda baixa de saldo revalidando páginas sobrecarregaria o Outbox numa sequência de leituras | `CatalogChanged` só quando a variação **cruza o zero** (esgota ou volta) | O detalhe já lê a disponibilidade sem cache; a página só muda nesses dois momentos |
| P13 | Não existe checkout até a Fase 4 | "Finalizar compra" leva ao login e volta para `/carrinho` com a sacola mesclada. Logado, mostra que a compra online chega na próxima etapa | Testa o fluxo do arquivo 16 §5 sem prometer o que não existe |

## 4. Escopo planejado

### 4.1 Banco

| Script | Conteúdo | Fatia |
|--------|----------|-------|
| `V0013__inventory_stock_movements.sql` | `inventory.stock_movements` e índice (arquivo 05) | 3.1 |
| `V0014__carts.sql` | Schema `carts`, `carts.carts` e `carts.cart_items` | 3.2 |
| `V0015__shipping_settings.sql` | Schema `shipping` e `settings` (com peso e medidas da caixa, P9) | 3.3 |
| `V0016__shipping_rules.sql` | `shipping.rules` | 3.4 |
| Seed `S0005__example_shipping.sql` | Linha de `settings` só para desenvolvimento, **sem nenhuma regra** (D08) | 3.3 |

Cada script novo entra no `database/gerar-scripts.sh`, que gera um arquivo por fatia (`database/fase-3-1-schema.sql`, e assim por diante), para quem já rodou o primeiro não travar em "Script já aplicado". O saldo que já existe não ganha movimentação retroativa: o histórico começa nesta fase. A V0013 também garante uma linha de saldo para toda variação.

### 4.2 Domain e Application

- **`StockItem`** com `Increase`, `Decrease` e `Adjust`, cada um gerando um `StockMovement`. Saldo negativo vira `422 Inventory.InsufficientStock`, com o `CHECK` do banco como última barreira. A operação trava a linha e grava saldo e movimentação na mesma transação.
- **`Cart`** com as regras da RN-040; não guarda preço. A mescla soma as quantidades com limite de 99, ignora variação inativa e devolve o que não entrou.
- **`ShippingRule`** e **`ShippingPricingService`** no Domain, sem I/O, com vigência, subtotal, UF, faixa de CEP e serviço.
- **`PackagingService`** monta o pacote conforme P9, com o peso e as medidas da variação ou, na falta delas, do produto.
- **`IShippingQuoteProvider`** com `MelhorEnvioQuoteProvider` e um provedor falso para desenvolvimento e testes, escolhidos por `Shipping:Provider`, como no `Email:Provider`.
- **Resiliência** com `Microsoft.Extensions.Http.Resilience`, pacote novo: 3 s por tentativa, retry e circuit breaker (arquivo 22 §5). Esgotou → contingência.
- **`IPostalCodeLookup`** com ViaCEP e cache de 7 dias.
- O preço e o valor declarado vêm sempre do banco.

### 4.3 API (`/api/v1`)

| Grupo | Rotas | Acesso e limite | Fatia |
|-------|-------|-----------------|-------|
| `admin/stock` | `GET ?lowStock&search`, `POST {variantId}/movements`, `GET {variantId}/movements`, `POST scan` | Admin, `admin-write` | 3.1 |
| `cart` | `GET /`, `POST items`, `PUT` e `DELETE items/{variantId}`, `DELETE /`, `POST merge` | Customer, `customer-write` | 3.2 |
| `catalog/variants/lookup` | `POST` (P1), sem cache | Público, `public-read` | 3.2 |
| `shipping` | `POST quotes`, `GET postal-codes/{cep}` | Público, política nova `shipping-quote` (20/min, arquivo 11) | 3.3 |
| `admin/shipping/settings` | `GET`, `PUT` | Admin, `admin-write` | 3.3 |
| `admin/shipping/rules` | `GET`, `POST`, `PUT`, `DELETE` | Admin, `admin-write` | 3.4 |

A leitura de código sem dono responde `404 Catalog.BarcodeNotFound`, e o painel oferece vincular (RF-059 CA1). Dígito errado responde `400` sem alterar nada (CA2). A leitura repetida é intencional e soma de novo; por isso o painel não reenvia sozinho um `POST` que falhou.

Mudança em regras ou configuração invalida o HybridCache pela tag `shipping`.

### 4.4 Frontend

**Loja:**
- Botão "Adicionar à sacola" ligado no produto, na barra fixa do celular (arquivo 26 §4.3, hoje só com preço e WhatsApp) e na peça em destaque da home (§4.1).
- Ícone da `Sacola` (saquinho de juta) e contador no topo. Nenhum dos dois existe hoje, embora a entrega da 2B cite o componente.
- `/carrinho` com `noindex`: cartelas pequenas, "desfazer" ao remover, estados de sacola vazia e de item esgotado (§4.5).
- Uma camada única de sacola com duas origens: `localStorage` para visitante e admin (P2), API para cliente. A cópia local só é apagada depois que a mescla responde sucesso.
- Simulador de frete no produto e na sacola, com o CEP lembrado no navegador e "faltam R$ X para frete grátis" quando houver regra.
- Contador e botão não podem aumentar o peso da vitrine: a meta de Lighthouse continua valendo.

**Painel:**
- `/admin/estoque`: busca, filtro de estoque baixo, entrada, saída e ajuste, histórico por variação.
- Modo de leitura contínua, reaproveitando o `BarcodeScanner`: cada leitura soma 1, mostra a peça lida e permite desfazer com uma saída.
- O editor de produto ganha o saldo e um atalho para o estoque, e passa a avisar quando a peça ativa está zerada.
- `/admin/frete`: configuração da loja e regras (nome, prioridade, vigência, condições, efeito).

A sacola segue o arquivo 26 §4.5, com uma versão rodando na loja para aprovação; se não agradar, as alternativas são mostradas do mesmo jeito.

### 4.5 Testes a escrever

| Projeto | Cobertura |
|---------|-----------|
| `Domain.UnitTests` | `StockItem`, RN-040 e mescla, regras parametrizadas (prioridade, vigência, UF, faixa, combinação, piso em zero), faixas de CEP por UF, montagem do pacote |
| `Application.UnitTests` | Validators de movimentação, sacola, cotação, configuração e regra |
| `Api.FunctionalTests` | Duas saídas simultâneas da última unidade (uma só passa); leitura válida, inválida e sem dono; `CatalogChanged` só ao cruzar o zero; sacola e mescla; admin em `/cart` responde 403; subtotais diferentes com o mesmo pacote recebem preços diferentes (P3); provedor fora → contingência, sem cache; sem configuração → 503; 429 na cotação |
| Adapter | WireMock.Net (pacote novo) com timeout, erro e serviço indisponível. As respostas são gravadas do sandbox quando houver token |
| `ArchitectureTests` | Domain sem dependência de HTTP ou do Melhor Envio |
| Frontend (Vitest) | Sacola local, mescla que falha sem perder itens, sacola do admin, máscara de CEP, formulários do painel, aviso de frete grátis |

## 5. Fora do escopo

- Checkout, pedido, baixa de estoque na compra, endereços e presente → Fase 4.
- Etiqueta e atualização automática de rastreio → pós-lançamento.
- Reserva de estoque na sacola (RF-030 CA4).
- Editar o limite de estoque baixo por variação: fica em 3, o padrão do banco.
- Mini Envios e outras transportadoras: o RF-032 cobre PAC e SEDEX. Vale comparar quando houver cotação real, porque semijoia é leve.

## 6. O que depende da loja

| # | O que falta | Impacto se não chegar |
|---|-------------|-----------------------|
| 1 | **Conta e token de sandbox do Melhor Envio** | O código é feito com o provedor falso e o WireMock. A cotação real fica sem validar, e **a 3.3 não fecha** |
| 2 | **CEP de origem** | Bloqueia só a cotação real; em desenvolvimento usa o valor do seed |
| 3 | **Peso e medidas da caixa padrão** | Valor provisório, editável no painel |
| 4 | **Valores de contingência de PAC e SEDEX** | Valor provisório; precisa estar definido antes do lançamento |

Nada disso impede o início: 3.1 e 3.2 não dependem de nenhum item.

## 7. Como validar

Primeiro só build (A), depois banco pelo DBeaver (B) e por último execução (C). Cada fatia preenche as linhas dela. **A fase só fecha depois de rodar com banco real, API em container e build de produção.**

### Etapa A — build (a cada fatia)

| # | Comando | Esperado |
|---|---------|----------|
| A1 | `dotnet build ECommerce.sln` | 0 erros, 0 avisos |
| A2 | `dotnet format ECommerce.sln --verify-no-changes` | sem alterações |
| A3 | `npm run lint && npm run typecheck` | sem erros |
| A4 | `npm test` | verdes |
| A5 | `npm run build` contra o stub (`scripts/ci-api-stub.mjs` atualizado) | conclui |

### Etapa B — banco pelo DBeaver

| # | Passo | Esperado |
|---|-------|----------|
| B1 | Rodar os `database/fase-3-*-schema.sql`, em ordem, num banco com a 2B | `COMMIT`; V0013–V0016 em `public.schemaversions` |
| B2 | Rodar B1 de novo | para com "Script já aplicado" |
| B3 | Rodar `database/fase-3-3-dados-exemplo.sql` | uma linha em `shipping.settings`, nenhuma regra |
| B4 | Rodar `ECommerce.Migrator` depois | "No new scripts" |

### Etapa C — execução

| # | Fatia | Verificação | Esperado |
|---|-------|-------------|----------|
| C1 | todas | `docker compose up --build api` e `dotnet test` | API no ar pelo container; todos verdes |
| C2 | 3.1 | Criar peça no painel e dar entrada de 5 | a loja passa de "Esgotado" para "Disponível" em segundos |
| C3 | 3.1 | Saída maior que o saldo | recusada com mensagem; saldo igual |
| C4 | 3.1 | Ajuste para o total contado | saldo igual ao total; histórico com delta, motivo e autor |
| C5 | 3.1 | Ler EAN conhecido, EAN sem dono e EAN com dígito errado, com leitor USB e câmera do celular | soma 1; oferece vincular; erro sem alterar nada |
| C6 | 3.2 | Visitante adiciona, recarrega e altera a quantidade | sacola mantida, com preço e foto vindos da API |
| C7 | 3.2 | Mudar o preço no painel com a peça na sacola | sacola mostra o preço novo |
| C8 | 3.2 | Esgotar uma peça que está na sacola | furo vazio e aviso, sem seguir com ela |
| C9 | 3.2 | Visitante com itens faz login em conta que já tem a mesma peça | quantidades somadas, limite de 99; local esvaziado |
| C10 | 3.2 | Admin entra e usa a sacola | funciona, sem erro 403 no console |
| C11 | 3.3 | CEP válido no sandbox, no produto e na sacola | PAC e SEDEX com preço e prazo somando os dias de manuseio |
| C12 | 3.3 | CEP inválido | 400 com mensagem no campo |
| C13 | 3.3 | Derrubar o provedor (token ou URL inválidos) | contingência; ao voltar, cotação real sem esperar 30 min |
| C14 | 3.3 | Bolsa e anel na mesma cotação | pacote maior que a caixa padrão; preço coerente com o balcão |
| C15 | 3.4 | Regra de grátis acima de um valor, abaixo e acima dele, com o mesmo CEP | preço muda; "faltam R$ X" abaixo; `appliedRules` acima |
| C16 | 3.4 | Regra por UF, por faixa de CEP, com data futura e `HideService` | só vale onde deve; serviço some |
| C17 | todas | Build de produção da loja com a API em container, Lighthouse no produto e na home | ≥ 90 performance/acessibilidade, ≥ 95 SEO |

**Critério de aceite:** A1–A5, B1–B4 e C1–C17 passando.

## 8. Situação por fatia

| Fatia | Situação |
|-------|----------|
| 3.1 — Estoque | Implementada e validada em 02/11/2026; falta leitura com aparelho real |
| 3.2 — Sacola | Implementada e validada em 02/11/2026; falta aprovar o visual |
| 3.3 — Cotação de frete | Implementada em 02/11/2026 com provedor falso. **Não fecha sem o token do Melhor Envio** |
| 3.4 — Regras de frete | Implementada e validada em 02/11/2026, sobre o provedor falso |

### 3.1 — Validação (02/11/2026)

| # | Resultado |
|---|-----------|
| A1, A2 | ✅ 0 avisos e 0 erros; formatação sem alterações |
| A3, A4 | ✅ lint, typecheck e 111 testes do frontend |
| A5 | ✅ `npm run build` contra o stub, com as rotas de estoque, e contra a API real |
| B1, B2 | ✅ `database/fase-3-1-schema.sql` aplica no Postgres 17 e, repetido, para com "Script já aplicado" |
| B4 | ✅ O `ECommerce.Migrator` responde "No new scripts" |
| C1 | ✅ API em container (`docker compose up --build api`). .NET: Domain 89, Application 32, Arquitetura 8, Funcionais 170 |
| C2 | ✅ Com o saldo cruzando o zero, a página do produto muda em **2,1 s** ao esgotar e **1,8 s** ao voltar, no build de produção. Medido com peça do seed; a tela de entrada foi exercitada no painel |
| C3 | ✅ Saída maior que o saldo recusada com mensagem; saldo igual. Teste funcional com 8 saídas simultâneas da última unidade: só uma passa |
| C4 | ✅ Ajuste para o total contado; histórico com delta, motivo e autor |
| C5 | ✅ parcial: código conhecido, sem dono e inválido, digitados como faz o leitor USB, no painel real. ⏳ falta leitor USB e câmera do celular de verdade |

**Achado na validação do painel:** depois de um código inválido, a próxima leitura seria digitada em cima do texto antigo. O campo agora fica selecionado, e a leitura seguinte substitui.

**Não verificado:**
- **Câmera:** o intervalo de 2 s que impede a mesma peça de somar várias vezes por segundo não foi exercitado com câmera real. Para contar outra peça igual pela câmera, é preciso tirar o código da frente e esperar esse intervalo, ou ler outro código no meio.
- **Lighthouse:** fica para o fechamento da fase (C17); esta fatia só mudou o painel.

### 3.2 — Validação (02/11/2026)

| # | Resultado |
|---|-----------|
| A1, A2 | ✅ 0 avisos e 0 erros; formatação sem alterações |
| A3, A4 | ✅ lint, typecheck e 125 testes do frontend |
| A5 | ✅ `npm run build` contra o stub e contra a API real |
| B1, B2 | ✅ `database/fase-3-2-schema.sql` aplica no Postgres 17 e, repetido, para com "Script já aplicado" |
| B4 | ✅ O `ECommerce.Migrator` responde "No new scripts" |
| C1 | ✅ API em container. .NET: Domain 99, Application 32, Arquitetura 8, Funcionais 183 |
| C6 | ✅ Visitante adiciona, recarrega e altera a quantidade; preço e foto vêm da API |
| C7 | ✅ Preço alterado aparece na sacola ao reabrir |
| C8 | ✅ Peça esgotada mostra o furo vazio e o aviso, e "Finalizar compra" sai. Estoque insuficiente oferece "Ajustar para N" |
| C9 | ✅ Login com a mesma peça já salva: 3 + 2 = 5; sacola local apagada; volta para `/carrinho`. Limite de 99 coberto por teste funcional |
| C10 | ✅ Admin usa a sacola local, sem nenhuma chamada a `/cart` |
| C17 (parcial) | ✅ Lighthouse com o build de produção e a API real, 1 rodada: 100 nas quatro categorias em home, categoria e produto |

Também conferido: "desfazer" ao remover, foco no seletor ao tocar em "Escolha o aro", barra fixa do celular em 375 px e botão da home sem sair da cartela em 375, 700 e 1280 px, nos dois casos (peça com aro abre a página; peça sem aro entra na sacola ali mesmo).

**Achado na validação:** a frase "Adicionar à sacola" não cabia na coluna de texto da cartela da vez, do tablet em diante, e passava da borda. Nessas larguras o botão mostra "Adicionar", com a frase inteira para o leitor de tela.

**Decisões de implementação:**
- As escritas da sacola devolvem a sacola atualizada, sem exigir um `GET` depois.
- Peça esgotada pode entrar na sacola pela API: o RF-030 só recusa inativa ou inexistente. A loja desliga o botão, e a sacola mostra o aviso.
- O contador e a sacola do visitante não usam TanStack Query, que continua só no painel.
- A home troca "Ver a peça" pelo botão de adicionar; a cartela inteira continua sendo o link da peça.

**Não verificado:** toque em celular de verdade. O visual da sacola segue o arquivo 26 §4.5, com uma versão só; falta a aprovação da loja.

### 3.3 — Validação (02/11/2026)

**A cotação real não foi testada:** não há token do Melhor Envio. A loja rodou com `Shipping:Provider=Fake`, que só existe em Development e nos testes; em produção a API nem sobe com ele. O adaptador real foi exercitado contra um servidor simulado (WireMock), com o pipeline de resiliência de verdade e respostas escritas a partir da [documentação](https://docs.melhorenvio.com.br/reference/calculo-de-fretes-por-produtos).

| # | Resultado |
|---|-----------|
| A1, A2 | ✅ 0 avisos e 0 erros; formatação sem alterações |
| A3, A4 | ✅ lint, typecheck e 148 testes do frontend |
| A5 | ✅ `npm run build` contra o stub e contra a API real |
| B1–B3 | ✅ `fase-3-3-schema.sql` aplica e, repetido, para com "Script já aplicado"; `fase-3-3-dados-exemplo.sql` cria a configuração, sem regras |
| B4 | ✅ O `ECommerce.Migrator` responde "No new scripts" |
| C1 | ✅ API em container. .NET: Domain 112, Application 32, Arquitetura 8, Funcionais 217 |
| C11 | ⏳ Com o provedor falso: PAC e SEDEX com preço e prazo somando o manuseio, no produto e na sacola; a sacola recalcula ao mudar a quantidade. **Falta o sandbox** |
| C12 | ✅ CEP incompleto recusado na tela; inválido responde 400 na API |
| C13 | ⏳ Contingência e retorno sem esperar os 30 min cobertos por teste funcional. Timeout, nova tentativa e erros do provedor cobertos com WireMock. **Falta derrubar o provedor real** |
| C14 | ⏳ A montagem do pacote (bolsa e anel) tem teste de domínio. **Falta comparar o preço com o balcão** |
| ViaCEP | ✅ Chamada real pelo container: CEP 01310-100 devolve Avenida Paulista, São Paulo |
| RF-058 | ✅ Sem configuração, a loja oferece falar com a MO (503 na API). Salvo pelo painel, a cotação volta a funcionar; o formulário recusa valores inválidos |

**Achado na validação:** o aviso "Configuração salva" não aparecia, porque o formulário era remontado ao recarregar os dados.

**Decisões de implementação:**
- `ErrorType.Unavailable` novo, mapeado para 503.
- Contingência não tem prazo: a resposta traz `deliveryDays` nulo, e a loja mostra "Prazo a confirmar" e avisa que o valor é estimado.
- CEP recusado pelo provedor responde 400, sem contingência, para não dar preço a um CEP que não existe.
- As medidas vão arredondadas para cima, porque a API do Melhor Envio pede inteiros.
- Mudar a configuração não precisa limpar cache: origem e pacote estão na chave da cotação, e o manuseio é somado depois dela.
- O provedor exige `Shipping:MelhorEnvio:Token` e `UserAgent` (nome da aplicação e e-mail de contato) para a API subir fora de Development.

### 3.4 — Validação (02/11/2026)

As regras não dependem do provedor: são aplicadas sobre o preço que ele devolver. Rodaram sobre o provedor falso.

| # | Resultado |
|---|-----------|
| A1, A2 | ✅ 0 avisos e 0 erros; formatação sem alterações |
| A3, A4 | ✅ lint, typecheck e 177 testes do frontend |
| A5 | ✅ `npm run build` contra o stub e contra a API real |
| B1, B2 | ✅ `fase-3-4-schema.sql` aplica e, repetido, para com "Script já aplicado" |
| B4 | ✅ O `ECommerce.Migrator` responde "No new scripts" |
| C1 | ✅ API em container. .NET: Domain 161, Application 32, Arquitetura 8, Funcionais 237 |
| C15 | ✅ Regra criada no painel, grátis no PAC a partir de R$ 299. Mesmo CEP: abaixo, "Faltam R$ 9,10"; acima, preço riscado e "Grátis" |
| C16 | ✅ Na API real: UF e `HideService` escondem o SEDEX só em MG; o desconto vale só na faixa de CEP; regra com data futura não vale |
| C17 | ✅ Lighthouse com o build de produção e a API real, 1 rodada: 100 nas quatro categorias em home, categoria e produto |
| RF-032 CA2 | ✅ Regras também valem sobre a contingência (teste funcional) |

**Decisões de implementação:**
- `shipping.rules` usa `date` para a vigência, como as ocasiões, e `text[]` para UFs e faixas, no lugar de `char(2)[]` e `int4range[]` do DDL de referência. Faixa em texto evita perder o zero à esquerda do CEP e mapear tipo de faixa no Dapper.
- Regras e configuração não têm cache: são lidas a cada cotação, que já tem limite próprio, e valem na hora. O arquivo 12 foi ajustado.
- O desconto é calculado sobre o preço já acumulado, e o total é arredondado para centavos só no fim.
- Sem marcar serviço, ou marcando os dois, a regra vale para ambos.
- Com algum serviço já grátis, a loja não mostra "faltam R$ X".

**Não verificado:** "Excluir" pela tela (a confirmação do navegador não foi exercitada; a rota tem teste). Desligar e reabrir a regra no formulário foram conferidos na tela.

### Fechamento da fase (02/11/2026)

| Pendência | Situação |
|-----------|----------|
| Cotação real (C11, C13, C14) | ⏳ Depende do token do Melhor Envio |
| Câmera e leitor USB reais (C5), toque em celular | ⏳ Depende de aparelho |
| Aprovação do visual da sacola | ⏳ Depende da loja |

**Para fechar a 3.3:** configurar o token e o User-Agent, trocar para `MelhorEnvio`, conferir C11, C13 e C14, gravar respostas reais nos testes do adaptador e confirmar como o serviço não atendido vem na resposta, ponto que a documentação não descreve e que hoje é tratado por `error` ou falta de preço.

## 9. Riscos e pontos de atenção

| Risco | Tratamento |
|-------|------------|
| O pacote é uma estimativa (P9): pode divergir do que a loja paga na etiqueta | Conferir C14 com peças reais; ajustar a caixa no painel. Se errar muito, revisar a regra antes da Fase 4 |
| Fila serial do Outbox (risco aberto na 2B): sequências de entrada podem atrasar e-mails | P12 reduz o volume ao mínimo. A revisão segue na Fase 6 |
| A mescla soma: uma resposta perdida seguida de nova tentativa duplicaria as quantidades | Reenvio só no próximo login. Se aparecer em uso real, a mescla ganha `Idempotency-Key` |
| Respostas reais do Melhor Envio podem diferir da documentação | Os testes do adapter usam respostas gravadas assim que houver token, antes de fechar a 3.3 |
| A lista de serviços (PAC e SEDEX) depende de identificadores do provedor | Identificadores em configuração, conferidos no sandbox |

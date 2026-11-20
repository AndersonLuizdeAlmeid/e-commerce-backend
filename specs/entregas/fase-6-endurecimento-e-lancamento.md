# Entrega — Fase 6: Endurecimento e lançamento

**Status:** Planejada em 16/11/2026. **6.1 implementada e validada em 16/11/2026** com testes, scripts por `psql` num banco vazio e API em container com Mailpit. **6.2 implementada e validada em 18/11/2026**, menos a varredura ZAP, que depende do staging (6.5). **6.3 implementada e validada em 20/11/2026** (código, banco, API em container e telas); os textos legais são rascunho até chegarem os dados da loja e a revisão jurídica (§6, itens 4 e 5). Detalhes na seção 8. As outras fatias ainda não começaram.
**Repositórios:** `e-commerce-backend` e `e-commerce-frontend` (branch `feat/fase-6` nos dois para 6.1 e 6.2, já na `main`; `feat/fase-6-3` para a 6.3).

## 1. Contexto

Ao fim da Fase 5, a loja já vende, cobra, entrega e estorna de ponta a ponta. Só que roda apenas no ambiente de dev da VM compartilhada, pela rede privada, com plataformas falsas ou de sandbox. Também faltam peças que as fases anteriores deixaram anotadas para "antes do lançamento":

- a fila do Outbox é serial e segura a transação durante a chamada externa;
- não há alertas;
- a loja não tem Content Security Policy;
- não há nada de LGPD para a cliente;
- não há E2E nem teste de carga;
- não há produção.

A Fase 6 fecha essas lacunas e termina com a loja no ar, num domínio próprio, depois de um pagamento real.

A entrega é feita em **sete fatias**. As de código (6.1 a 6.4 e o feed da 6.6) começam já. A 6.5 depende do domínio e da VM de produção (§6). A 6.7 depende de todas.

| Fatia | Conteúdo | Origem | Depende de |
|-------|----------|--------|------------|
| **6.1 — Outbox, jobs e alertas internos** | Outbox sem transação aberta durante a chamada externa e com processamento em paralelo; EM-11 (estoque baixo diário); vigia de sinais de negócio com e-mail de alerta; limpeza de mensagens antigas | 2B §riscos, Fase 3 §riscos, Fase 4 §4.3, Fase 5 §5, arquivo 25 | — |
| **6.2 — Segurança** | CSP e headers na loja; usuários de banco separados (aplicação × migrations); CodeQL, gitleaks e `npm audit` no CI; revisão ASVS nível 2; varredura ZAP no staging | arquivo 17, Fase 4 §4.4 | ZAP: 6.5 |
| **6.3 — LGPD, páginas legais e devolução** | Exportar dados, excluir conta (anonimização), política de privacidade, termos, trocas e devoluções, identificação da loja no rodapé; devolução de pedido entregue com estorno | arquivo 17 §5, RNF-015 | Textos: §6 |
| **6.4 — E2E e carga** | Playwright nos fluxos críticos (com checagem de acessibilidade) no CI da loja; cenários k6 | arquivo 15, RNF-001, RNF-018 | k6: 6.5 |
| **6.5 — Produção e staging** | Domínio, Cloudflare (DNS, túnel, Access), VM de produção, staging, deploy por tag com aprovação, R2 para fotos e backup, Resend com SPF/DKIM/DMARC, Grafana Cloud e monitor externo | Infra 1 §5, arquivo 18 §5 e §7 | Domínio e VM (§6) |
| **6.6 — Google** | Feed do Merchant Center (`/feeds/google-merchant.xml`), Search Console e Merchant Center configurados | SEO-14, Fase 1 | Feed: —; contas: 6.5 |
| **6.7 — Homologação e lançamento** | Credenciais de produção, pagamento real de valor baixo (PIX e cartão) e estorno, checklist de go-live, celular de verdade, tag `v1.0.0` | Fase 4 §5, roadmap | todas |

6.1, 6.2 (menos a varredura ZAP), 6.3 e 6.4 (menos o k6) são independentes entre si.

**Antes da 6.7 precisam estar fechadas as validações abertas das fases anteriores:**

- 3.3 com o token do Melhor Envio;
- Fase 4 no sandbox do Mercado Pago (C7, C9 e C11);
- aprovação visual do checkout e do painel.

## 2. Objetivos

1. **Nenhuma mensagem fica presa atrás de outra.** Um provedor lento não atrasa e-mails nem estornos, e nenhuma transação fica aberta esperando a internet.
2. **Ninguém descobre um problema pela cliente.** Mensagem que falhou, fila atrasada, webhook que parou de chegar, API fora do ar e 5xx em excesso geram um aviso em minutos.
3. **A loja passa numa revisão de segurança** ASVS nível 2, sem achado alto aberto, com CSP ativa e sem quebrar o pagamento.
4. **A cliente exerce os direitos da LGPD sozinha** (exportar e excluir), e a loja cumpre o que a lei de comércio eletrônico pede no site (identificação, atendimento e arrependimento).
5. **Os fluxos de compra e de pedido são testados de ponta a ponta a cada push**, e a VM aguenta a meta do RNF-001 com folga.
6. **A loja está no ar no domínio próprio**, com staging fechado para a MO, deploy com aprovação, backup restaurável e e-mail que chega na caixa de entrada.
7. **Um pagamento real foi aprovado e estornado** em produção antes da primeira cliente.

## 3. Decisões que esta fase precisa fechar

| # | Ponto | Decisão | Motivo |
|---|-------|---------|--------|
| P1 | O `OutboxProcessor` segura o lote numa transação durante a chamada externa (até 10 s por mensagem) e processa uma mensagem por vez | **Reserva com prazo**. Uma transação curta pega o lote e grava `locked_until` = agora + 5 min. Cada mensagem é processada **fora de transação**, com até **4 em paralelo**, e marcada sozinha no fim. Se a API cair no meio, a reserva vence e a mensagem volta para a fila. Os handlers já são idempotentes (arquivo 25 §1) | Fim da conexão presa e da fila serial. A reentrega já era permitida |
| P2 | Hangfire (arquivo 25 §2) | **Não entra.** São 5 jobs simples, já com advisory lock e `TimeProvider` testável | Um painel de jobs não paga uma dependência e um schema a mais com ~10 pedidos/semana |
| P3 | Onde mora o alerta: um alerta que passa pelo Outbox não avisa quando o Outbox está quebrado | **Três camadas**. (a) `OpsMonitorJob` a cada 5 min confere os sinais de negócio do arquivo 14 §6 e do arquivo 25 §3: qualquer `failed_at`; mensagem pendente há mais de 15 min; pagamento pendente sem webhook há mais de 2 h, quando houve PIX gerado no período; uso do frete de contingência. Ele manda o **EM-14 (alerta técnico)** direto pelo `IEmailSender`, sem Outbox, para `Ops:AlertEmail`, no máximo uma vez a cada 6 h por condição. (b) **Grafana Cloud** recebe métricas e logs por OTLP e alerta 5xx > 1 % e p95 > 1 s. (c) Um **monitor externo** de disponibilidade bate em `/health/ready` e na home | Cada camada cobre a falha da outra. A loja continua vendo o que é dela no painel (estornos pendentes) e no EM-11 |
| P4 | Horário e conteúdo do EM-11 | Diário às **8h de São Paulo**, para o e-mail da loja, **só se houver** variação ativa no limiar ou abaixo dele. Lista peça, cor, tamanho, SKU e saldo, com link para o estoque filtrado | E-mail vazio todo dia vira ruído e é ignorado |
| P5 | CSP com nonce obriga renderização dinâmica no Next.js e acaba com o ISR, que é a base do SEO (Q10) | **CSP sem nonce**: `script-src 'self' 'unsafe-inline'` + domínios do Mercado Pago; `object-src 'none'`, `base-uri 'self'`, `form-action 'self'`, `frame-ancestors 'none'`, `connect-src` = API + Mercado Pago, `img-src` = R2 + Mercado Pago, `frame-src` = Mercado Pago. Entra como **`Report-Only` no staging** e vira obrigatória quando o checkout com cartão e o PIX passarem sem violação no console | Mantém o ISR. Os outros diretivos ainda barram injeção de `<object>`, `<base>` e envio de formulário para fora. A lista do Mercado Pago sai do navegador, não de suposição |
| P6 | A API e o migrator usam o mesmo usuário dono do banco; o arquivo 17 §4 pede privilégio mínimo e SSL | **Dois papéis**: `mo_migrator` (dono dos schemas, DDL) e `mo_app` (só DML, com `ALTER DEFAULT PRIVILEGES` para as tabelas novas). Criados pelo bootstrap da VM, porque dependem de senha. **SSL não** enquanto o Postgres estiver no mesmo host, na rede do Compose e sem porta publicada; passa a ser obrigatório se o banco sair da VM | Uma injeção não derruba nem altera tabela. Na rede interna, o TLS não protege nada que já não esteja protegido |
| P7 | Excluir conta (arquivo 17 §5): os pedidos precisam continuar existindo | `DELETE /me` com a **senha atual**. **Recusado (`422`) com pedido em andamento** (aguardando pagamento até enviado) ou com estorno pendente. Senão: nome, telefone, CPF, endereços e a cópia do destinatário e da dedicatória nos pedidos são substituídos; o e-mail vira `removida-<id>@invalid`; a senha some; refresh tokens e stamp são revogados; a linha ganha `anonymized_at`. Valores, peças e datas ficam. O painel mostra "Cliente removida" | Obrigação fiscal sem guardar dado pessoal sem motivo. Recusar com pedido aberto evita entrega sem destinatário |
| P8 | Exportar dados | `GET /me/data-export` devolve um JSON na hora (perfil, endereços, pedidos com itens e situação, consentimentos), com limite de **3 por dia** por usuária | Cabe numa resposta só, sem job nem link por e-mail |
| P9 | Banner de cookies | **Nenhum**. A loja só usa cookies essenciais (refresh token) e não tem analytics nem pixel. A política de privacidade diz isso. Se entrar analytics, entra consentimento junto | Banner sem escolha real é ruído; a LGPD não exige consentimento para cookie essencial |
| P10 | Retenção | Mensagens processadas do Outbox apagadas depois de **90 dias** e o `email_log` depois de **180 dias**, pelo `MaintenanceJob`. Logs: 14 dias no Grafana Cloud (plano gratuito) e rotação no Docker | Payload e registro de e-mail têm endereço e nome; guardar além disso não serve a ninguém |
| P11 | O direito de arrependimento (7 dias) vale para pedido **entregue**, e hoje entregue é estado final | Nova transição **Entregue → Devolvido** pelo painel, com nota obrigatória. Dispara o mesmo estorno total do cancelamento pelo Outbox e manda o EM-13. O estoque **não** volta sozinho: a peça é conferida e entra por ajuste. O prazo não é travado pelo sistema; quem decide é a loja | Sem isso, a devolução seria feita no painel do Mercado Pago, e o pedido e o dashboard ficariam mentindo |
| P12 | Exposição pública: o arquivo 18 §7 previa Caddy com a porta 443 aberta | **Cloudflare Tunnel** (`cloudflared` no Compose). A VM não abre nenhuma porta; o SSH continua só pelo Tailscale. TLS fica com a Cloudflare, e a API confia no `X-Forwarded-For` só vindo da rede do `cloudflared` | Menos superfície e sem certificado para renovar. Muda o arquivo 18 §7 |
| P13 | Staging | Na **mesma VM de produção**, em outro Compose com banco próprio. Fica atrás do **Cloudflare Access** (código por e-mail, só o dev e a MO), com `noindex`, Mercado Pago em sandbox e Resend com destinatários restritos. O dev continua na VM compartilhada | A MO aprova antes de cada versão, e o Google nunca indexa o staging |
| P14 | Backup de produção (arquivo 18 §7 previa 7 dias) | `pg_dump` diário **criptografado com `age`** e enviado para o R2 em prefixo privado, com **30 dias** de retenção. A chave privada fica fora da VM, com você. Restauração testada antes do lançamento e depois uma vez por mês | O dump tem dados pessoais; o bucket não deve ser a única proteção. 30 dias cobre um erro percebido tarde |
| P15 | Onde rodar o k6 sem afetar cliente | Na **VM de produção antes do lançamento**, pela rede privada, sem passar pela Cloudflare. O banco é recriado depois | É o hardware real. Antes de abrir não há cliente para atrapalhar, e a Cloudflare bloquearia o tráfego como ataque |
| P16 | E2E: contra o quê | No CI da loja, com a pilha em container (imagem da API da `main`, Postgres, Mailpit, plataformas falsas) e o build de produção da loja. O pagamento no **sandbox do Mercado Pago** fica no roteiro manual da 6.7 | E2E que depende de sandbox externo fica instável e bloqueia o push |
| P17 | Como homologar com dinheiro real sem expor um produto de teste | Uma **peça de homologação de R$ 5,00**, ativa só durante o teste, fora de qualquer coleção, comprada pelo link direto e desativada logo depois. Os pedidos ficam com nota "homologação" no histórico | O caminho é o real (webhook público, Brick, PIX), e a vitrine não mostra nada estranho |

## 4. Escopo planejado

### 4.1 Banco

| Script | Conteúdo | Fatia |
|--------|----------|-------|
| `V0021__outbox_lease_and_ops.sql` ✅ | `locked_until` e `lock_token` em `messaging.outbox_messages` (P1); índice de mensagens que desistiram; schema `ops` com `ops.job_marks` (chave, última vez), usado pelo EM-11 e pela regra de 6 h dos alertas (P3, P4) | 6.1 |
| `V0022__returns_and_account_removal.sql` ✅ | `Returned` no `CHECK` da situação do pedido (P11); `anonymized_at` em `identity.users`, com `CHECK` de que conta anonimizada nunca está ativa (P7) | 6.3 |

Cada script entra no `database/gerar-scripts.sh`: a V0021 já está em `database/fase-6-schema.sql`; a V0022 vira `database/fase-6-3-schema.sql`. Os papéis `mo_app` e `mo_migrator` (P6) ficam no bootstrap da VM e numa seção comentada do README do `database/`, para quem usa o banco local pelo DBeaver.

### 4.2 Backend

- **`OutboxProcessor`** reescrito com reserva e paralelismo (P1). Os testes do Outbox da 2A e da 2B continuam valendo.
- **`LowStockDigestJob`** (EM-11, P4) e **`OpsMonitorJob`** (EM-14, P3), no mesmo padrão dos jobs que já existem (`PeriodicTimer` + advisory lock).
- **`MaintenanceJob`** ganha a limpeza do P10.
- **OpenTelemetry**: métricas HTTP e do Outbox (pendentes, idade da mais antiga, falhas por tipo), exportadas por OTLP só quando `Otlp:Endpoint` estiver configurado.
- **Identidade**: `ExportMyData` e `DeleteMyAccount` (P7, P8), com o limite novo `data-export`.
- **Pedido**: `Order.MarkReturned(note)` (P11), reaproveitando o estorno do cancelamento.
- **Feed** `GET /feeds/google-merchant.xml`: peças ativas com estoque, uma entrada por variação agrupada por `item_group_id`, GTIN quando houver código de barras, preço, disponibilidade, foto e link canônico. Cache de 1 h, invalidado pela tag do catálogo.
- **Configuração de produção**: o `ValidateOnStart` recusa subir em Production sem `Ops:AlertEmail`, e-mail da loja e credenciais que não sejam de teste. O `TEST-` do Mercado Pago é recusado em Production.

### 4.3 API

| Rotas | Limite | Fatia |
|-------|--------|-------|
| `GET /me/data-export` | `data-export` (3/dia) | 6.3 |
| `DELETE /me` `{ password }` | `auth-strict` | 6.3 |
| `POST /admin/orders/{id}/return` `{ note }` | `admin-write` | 6.3 |
| `GET /feeds/google-merchant.xml` (API em `/api/v1/feeds/...`; a loja serve no próprio domínio) | `public-read` | 6.6 |

### 4.4 Frontend

- **Headers** no `next.config.ts` (P5): CSP, HSTS, `X-Content-Type-Options`, `Referrer-Policy: strict-origin-when-cross-origin`, `Permissions-Policy` (câmera liberada só no painel, por causa do leitor de código de barras).
- **Minha conta → Privacidade**: baixar meus dados e excluir conta. A exclusão pede a senha e explica o que some e o que fica.
- **Páginas** `/privacidade`, `/termos` e `/trocas-e-devolucoes`. O texto vem da loja (§6); a estrutura e um rascunho saem desta fase.
- **Rodapé** com razão social, CNPJ, endereço, e-mail e telefone de atendimento (Decreto 7.962/2013). Os valores vêm de configuração, não do código.
- **Painel**: ação "Registrar devolução" em pedido entregue (P11); "Cliente removida" em pedido de conta excluída.
- **`noindex`** e `robots.txt` fechado quando `NEXT_PUBLIC_ENV=staging` (P13).

### 4.5 CI e infraestrutura

- **Os dois CIs**: CodeQL, gitleaks e Dependabot. No da loja também `npm audit --audit-level=high` e o job de **Playwright** (P16).
- **`deploy/prod/`**: `compose.yml` (loja, API, migrator, Postgres, `cloudflared`), `compose.staging.yml`, bootstrap (papéis do banco, `age`, backup, endurecimento igual ao da Infra 1), `mo-deploy` aceitando tag `vX.Y.Z` com o ambiente `production` do GitHub e aprovação manual, e README de operação.
- **Cloudflare**:
  - DNS de `loja` e `api`;
  - túnel;
  - Access no staging;
  - regras de cache para estático e foto;
  - WAF gratuito;
  - domínio público do R2.
- **Resend**: domínio verificado com SPF, DKIM e DMARC (`p=quarantine` depois de duas semanas em `p=none` com relatório).
- **Grafana Cloud** (P3b) e monitor externo (P3c).
- **Lighthouse CI** apontando também para o staging.

### 4.6 Testes a escrever

| Projeto | Cobertura |
|---------|-----------|
| `Domain.UnitTests` | Entregue → Devolvido e as transições proibidas novas; anonimização da cliente |
| `Application.UnitTests` | Regras de bloqueio da exclusão; montagem do EM-11; deduplicação de alertas |
| `Api.FunctionalTests` | Provedor lento não atrasa outra mensagem; reserva vencida volta para a fila; duas instâncias não processam a mesma mensagem; `failed_at` → EM-14 uma vez só; EM-11 só com item no limiar; exportação só com dados próprios; exclusão com pedido aberto → `422`; exclusão → login `401` e pedido anonimizado; devolução → estorno e EM-13; feed com GTIN e sem peça inativa; `mo_app` sem permissão de DDL |
| Frontend (Vitest) | Headers do `next.config`; fluxo de exclusão; rodapé lendo a configuração |
| Playwright | (1) cadastro → confirmação pelo Mailpit → sacola → checkout PIX → simular pagamento → pedido pago; (2) checkout com cartão na plataforma falsa; (3) cliente cancela pedido aguardando pagamento; (4) painel: separar → enviar → entregar → devolver; (5) busca → categoria → produto → sacola em 375 px. **axe** em cada página visitada, falhando em violação séria ou crítica |
| k6 | Vitrine (home, categoria, produto, busca) a 100 req/s; cotação de frete; checkout com 20 clientes ao mesmo tempo; disputa pela última unidade |

## 5. Fora do escopo

- 2FA para admin, cupons, avaliações, e-mail marketing, etiqueta pela API e rastreio automático → backlog pós-lançamento (roadmap).
- Estorno parcial e troca de peça → continuam à mão (Fase 5).
- Segunda instância da API, Redis e banco gerenciado → quando o volume pedir (arquivo 18 §7).
- Analytics → só com consentimento (P9), numa entrega própria.
- Painel de jobs (P2).

## 6. O que depende de você

| # | O que falta | Quando | Impacto se não chegar |
|---|-------------|--------|-----------------------|
| 1 | **Domínio `.com.br`** escolhido e registrado (registro.br) e conta na **Cloudflare** com o DNS apontado | Início da 6.5 | Sem produção, staging, Resend, Search Console nem Merchant Center |
| 2 | **VM de produção**: provedor, região São Paulo, 2 vCPU e 4 GB (arquivo 18 §7) | Início da 6.5 | Idem |
| 3 | Contas no **Resend**, no **Grafana Cloud** e no Google (**Search Console** e **Merchant Center**) | Durante a 6.5 e a 6.6 | Sem e-mail real, sem alerta de 5xx e sem Google |
| 4 | **Dados legais da loja**: razão social, CNPJ, endereço, e-mail e telefone de atendimento; política de trocas da MO | Antes de fechar a 6.3 | As páginas ficam com rascunho e o rodapé fica incompleto: **bloqueia o lançamento** |
| 5 | **Revisão dos textos legais** (privacidade, termos, trocas). Recomendo um advogado; o rascunho desta fase é ponto de partida, não parecer | Antes da 6.7 | Risco jurídico seu |
| 6 | **E-mail da loja** (pedido pago, EM-11) e **e-mail de alerta técnico** (EM-14) | 6.1 | Em dev vão para o Mailpit; em produção a API não sobe sem eles |
| 7 | **Credenciais de produção** do Mercado Pago (conta verificada) e **token de produção** do Melhor Envio. Os segredos vão direto para o `.env` da VM, **nunca para o chat** | Início da 6.7 | Sem homologação |
| 8 | Decisões pendentes das fases anteriores: **D07** (parcelamento), **D-F08** (embalagem para presente), **contingência de PAC e SEDEX**, **CEP de origem**, **caixa padrão**, **link de rastreio** | Antes da 6.7 | Lança com os valores provisórios |
| 9 | **Guardar a chave privada do backup** (P14) fora da VM, num gerenciador de senhas | 6.5 | Sem ela, o backup não restaura |
| 10 | **Catálogo real**: fotos e peças cadastradas pelo painel | Antes da 6.7 | Loja no ar sem produto |

Os itens 1 a 3 e 7 travam só a 6.5 e a 6.7. Dá para começar a 6.1 a 6.4 hoje.

## 7. Como validar

Primeiro só build (A), depois banco (B) e por último execução (C). **A fase só fecha depois de rodar com banco real, API em container e o build de produção da loja, e com a homologação em produção.**

### Critério de aceite

| # | Fatia | Verificação | Esperado |
|---|-------|-------------|----------|
| A1 | todas | `dotnet build` e `dotnet format --verify-no-changes` | Sem erro nem aviso |
| A2 | todas | `npm run lint`, `typecheck`, `test` e `build` na loja | Verde |
| A3 | 6.2 | CodeQL, gitleaks, `npm audit` e `dotnet list package --vulnerable` nos dois CIs | Verde, sem achado alto aberto |
| B1 | 6.1, 6.3 | `fase-6-schema.sql` no banco | Aplicado e registrado em `schemaversions` |
| B2 | 6.2 | Conectado como `mo_app`: `CREATE TABLE` e `DROP TABLE` | `permission denied`; `SELECT`, `INSERT`, `UPDATE` e `DELETE` funcionam |
| C1 | 6.1 | Revalidação da loja com 10 s de atraso (WireMock) e um e-mail na fila ao mesmo tempo | E-mail no Mailpit em segundos, sem esperar a revalidação |
| C2 | 6.1 | Derrubar a API no meio de uma mensagem | Depois da reserva vencer, a mensagem é processada uma vez |
| C3 | 6.1 | Mensagem com `failed_at` | EM-14 em até 5 min, e só um em 6 h |
| C4 | 6.1 | EM-11 com uma variação no limiar e outra acima | Só a do limiar no e-mail; sem item, nenhum e-mail |
| C5 | 6.2 | Home, produto, checkout com cartão (Brick) e PIX com a CSP obrigatória | Nenhuma violação no console; pagamento conclui |
| C6 | 6.2 | Varredura ZAP baseline no staging | Sem alerta alto; médios explicados ou corrigidos |
| C7 | 6.3 | Exportar dados | JSON com perfil, endereços e pedidos só da própria conta |
| C8 | 6.3 | Excluir conta com pedido aguardando pagamento; depois sem pedido aberto | `422`; depois login `401`, pedido no painel como "Cliente removida", sem nome, CPF ou endereço no banco |
| C9 | 6.3 | Pedido entregue → registrar devolução | Situação Devolvido, estorno pelo Outbox, EM-13, estoque igual até o ajuste |
| C10 | 6.3 | Rodapé e páginas legais | Dados da loja visíveis em todas as páginas; arrependimento explicado em trocas |
| C11 | 6.4 | Playwright no CI | Os 5 fluxos verdes, sem violação séria do axe |
| C12 | 6.4 | k6 na VM de produção (P15) | Catálogo p95 ≤ 200 ms a 100 req/s; checkout p95 ≤ 500 ms; última unidade vendida uma vez só; estoque nunca negativo |
| C13 | 6.5 | Push na `main` e tag `v0.9.0` | Staging atualiza sozinho; produção espera aprovação |
| C14 | 6.5 | Tag com versão quebrada de propósito | Volta sozinha para a anterior |
| C15 | 6.5 | Restaurar o backup do R2 num banco vazio com a chave `age` | Banco igual, conferido por contagem e por um pedido |
| C16 | 6.5 | E-mail real para Gmail e Outlook | SPF, DKIM e DMARC `pass` nos headers; caixa de entrada; nota ≥ 9 no mail-tester |
| C17 | 6.5 | Parar a API; forçar 5xx | Monitor externo e Grafana avisam |
| C18 | 6.5 | Staging sem login do Access; `robots.txt` do staging | Bloqueado; `Disallow: /` |
| C19 | 6.6 | Feed no Merchant Center | Aceito, com GTIN onde há código de barras |
| C20 | 6.6 | Search Console | Domínio verificado e sitemap enviado sem erro |
| C21 | 6.7 | PIX real e cartão real na peça de homologação (P17), e estorno dos dois | Aprovados pelo webhook público; estornos `Refunded`; valores conferidos no extrato do Mercado Pago |
| C22 | 6.7 | Lighthouse em produção (home, categoria, produto) | SEO ≥ 95; Core Web Vitals no verde (RNF-019) |
| C23 | 6.7 | Celular de verdade: compra completa (PIX copiado) e painel (separar e enviar) | Sem rolagem lateral; fecha as pendências de celular da 2B à 5 |

## 8. Situação por fatia

| Fatia | Situação |
|-------|----------|
| 6.1 — Outbox, jobs e alertas internos | ✅ Implementada e validada em 16/11/2026. As métricas OTLP do §4.2 ficaram para a 6.5, junto com o Grafana Cloud |
| 6.2 — Segurança | ✅ Implementada e validada em 18/11/2026. Falta a varredura ZAP (C6), que depende do staging, e conferir a CSP com o cartão no sandbox (C5). Duas decisões de senha e login esperam você (abaixo) |
| 6.3 — LGPD, páginas legais e devolução | ✅ Implementada e validada em 20/11/2026. Os textos de `/privacidade`, `/termos` e `/trocas-e-devolucoes` são rascunho: avisam isso no topo até a identificação da loja estar completa, e marcam em vermelho os pontos que a MO precisa decidir (§6, itens 4 e 5) |
| 6.4 — E2E e carga | 🟡 Implementada e validada localmente em 20/11/2026: 5 fluxos Playwright com axe (job `e2e` no CI da loja) e cenários k6 prontos. Falta o job rodar pela primeira vez no GitHub (depende da tag `main` das imagens do backend, publicada no próximo push na `main`) e o k6 de verdade na VM de produção (C12, 6.5) |
| 6.5 — Produção e staging | Planejada; depende do domínio e da VM |
| 6.6 — Google | 🟡 Feed implementado e validado em 20/11/2026. Falta configurar as contas (Search Console e Merchant Center), que dependem do domínio (6.5), e conferir C19 e C20 |
| 6.7 — Homologação e lançamento | Planejada |

### Ajustes ao plano feitos na 6.1

- **Vagas contínuas, não lotes em paralelo.** A primeira versão processava cada lote em paralelo, mas o lote seguinte só começava quando o anterior terminava; uma chamada lenta ainda segurava quem chegasse depois. O processador agora mantém até 4 vagas: assim que uma libera, a próxima mensagem é reservada (C1).
- **Marcação com o token da reserva.** Um processamento que passou da reserva não grava o resultado por cima de quem pegou a mensagem depois.
- **Uma tabela de marcas só** (`ops.job_marks`) no lugar de `ops.alerts_sent`: guarda a última vez do EM-11 e de cada alerta. O EM-11 é garantido uma vez por dia porque só uma instância consegue avançar a marca para o horário de hoje.
- **Alerta sem repetir nem pular**: cada condição conta só o que aconteceu desde o último alerta (falhas, PIX sem webhook, frete de contingência); a fila atrasada é um estado e se repete a cada 6 h enquanto durar. Na primeira vez, olha as últimas 24 h.
- **PIX sem webhook** (P3) virou um sinal preciso: PIX do Mercado Pago aprovado há mais de 10 min sem nenhuma notificação registrada, ou seja, quem confirmou foi a reconciliação. Cartão fica de fora, porque é aprovado na hora, sem esperar webhook. A plataforma falsa também fica de fora.
- **Sem `Ops:AlertEmail`** (desenvolvimento sem configurar), o alerta vai só para o log, e a marca é gravada do mesmo jeito para não repetir a cada 5 min. Em Production, a API não sobe sem `Ops:AlertEmail` nem sem `Email:StoreAddress`.
- **Limpeza (P10)**: o Outbox processado já era apagado com 90 dias desde a 2A; entrou o `email_log` com 180.
- **Os testes desligam a rodada de 5 min** (`Ops:ScheduledJobsEnabled = false`) e chamam o EM-11 e o vigia direto, para nenhuma rodada cair no meio de outro teste.

### Validação da 6.1 (16/11/2026)

| Item | Resultado |
|------|-----------|
| A1 | ✅ `dotnet build` com 0 avisos; `dotnet format` só aponta o fim de linha CRLF e 5 espaços no `AuthExtensions.cs`, que não foi mexido |
| Testes | ✅ aplicação 36 (4 novos), domínio 211, arquitetura 8, funcionais **317 de 318** (11 novos, todos verdes). A falha foi de novo `MercadoPagoGatewayTests.Get_MapsTheStatus(charged_back)`, o timeout do WireMock com a suíte inteira rodando já registrado na Fase 5; sozinho, o adaptador passa nos 16 |
| B1 | ✅ `fase-0` a `fase-6-schema.sql` aplicados em ordem por `psql` num Postgres 17 vazio; a segunda execução do `fase-6` recusa com "Script já aplicado"; o migrator do container aplicou a V0021 no banco local |
| C1 | ✅ Teste funcional: com uma mensagem levando 8 s no provedor, a seguinte chega em menos de 5 s. Outra conexão escreve na linha durante o processamento (antes ela ficava travada na transação do lote) |
| C2 | ✅ Teste funcional: reserva vencida é processada de novo; reserva em vigor, não |
| C3 | ✅ API em container: as 2 mensagens que desistiram no banco local geraram o EM-14 "mensagens que desistiram" no Mailpit, com tipo, horário de São Paulo e erro; a fila atrasada gerou outro EM-14. A segunda rodada dentro de 6 h não repete (teste funcional) |
| C4 | ✅ API em container: EM-11 com as 6 variações no limiar ("esgotada" ou "1 em estoque"), horário das 8h de São Paulo calculado certo no Linux; uma vez por dia e nada sem item no limiar (testes funcionais) |

**Achado na validação:** o banco local tem **188 `payments.sync` que desistiram** desde 08/11, todos do mesmo PIX da plataforma falsa (`fake-1-023d…`). A plataforma falsa guarda os pagamentos em memória e os perde quando a API reinicia; a reconciliação continua pedindo a sincronização a cada 15 min. Só acontece com o gateway falso, mas em desenvolvimento o EM-14 vai avisar disso a cada 6 h até o pagamento sair de "pendente".

### Ajustes ao plano feitos na 6.2

- **Papel da API criado pelo migrator, não pelo bootstrap (P6).** Com `Database:AppRole` e `Database:AppPassword`, o migrator cria ou atualiza o `mo_app` e reaplica as permissões a cada deploy, então schema e tabela novos já nascem liberados para a API. A senha fica no `.env` como os outros segredos. Na VM é compatível com o que está no ar: sem `DB_APP_*` no `.env`, a API segue com o usuário dono. O bootstrap só acrescenta as duas variáveis (roteiro no `deploy/dev/README.md`). O dono continua sendo o `ecommerce`, superusuário da imagem do Postgres, usado só pelo migrator.
- **Os testes funcionais rodam a API como `mo_app`**: nenhuma rota precisou de mais do que ler e gravar dados.
- **CSP no `next.config`, não num proxy.** Os endereços (`NEXT_PUBLIC_*`) já são fixados no build de cada ambiente, e o `CSP_MODE` vem junto. O ISR continua (`x-nextjs-cache: HIT`).
- **zod sem JIT.** O zod 4 testa `Function("")` para compilar validadores, o que gerava violação a cada abertura do painel. Ele cai sozinho no caminho sem JIT, mas o ruído esconderia violação de verdade. Agora o `z` vem de `@/shared/lib/zod`, com `jitless`, e o lint barra importar `zod` direto.
- **gitleaks pela imagem oficial fixada por digest**, no lugar da action. Os dois achados do histórico eram a linha do CI que remonta a chave de deploy a partir do segredo (só o texto BEGIN/END); estão no `.gitleaksignore`. A imagem só é publicada se o histórico estiver limpo.
- **`npm audit --audit-level=high` inclui as ferramentas de build**, não só o runtime.
- **`CSP_MODE` no Dockerfile da loja** (build arg): a imagem é gerada por ambiente, e o modo vai junto. Conferido no build da imagem: `Report-Only` e HSTS gravados no manifesto de rotas.

### Achados da revisão ASVS nível 2 (18/11/2026)

Os testes já cobriam IDOR, autorização do `/admin`, rotas anônimas, CORS, rate limit, rotação de refresh token e webhook assinado. A revisão olhou o resto: JWT (algoritmo, emissor, público, validade), lockout, cookie (`HttpOnly; Secure; SameSite=Strict; Path=/api/v1/auth`), detalhes do readiness, limite de corpo, uploads, log de requisição (sem query string) e links dos e-mails (vêm da configuração, não do `Host`).

| # | Achado | ASVS | Situação |
|---|--------|------|----------|
| S1 | Respostas autenticadas da API (perfil, pedidos, painel) saíam **sem `Cache-Control`**: CDN ou navegador compartilhado podiam guardar dado pessoal | V8.2.1 | ✅ Corrigido: tudo que não declara cache sai com `no-store`; o catálogo público continua com `public, max-age`. Teste novo |
| S2 | **Redirect aberto depois do login**: `/login?next=/%09/site.com` passava no filtro e o navegador, que apaga tab e quebra de linha, levava a cliente para outro site | V5.1.5 | ✅ Corrigido: o destino é resolvido como o navegador resolve e precisa ter a mesma origem. Testes novos e conferido no navegador |
| S3 | **zod 4.1.12** com vulnerabilidade alta (memória ao validar coleções grandes, GHSA-mrhx-c7xc-m3w5) e **Next 16.4.2** com duas moderadas de cache (GHSA-mfr8-8qww-v76h, GHSA-vc7v-qv4w-wrv8) | V14.2.1 | ✅ Atualizados para zod 4.6.7 e Next 16.4.6. Runtime sem vulnerabilidade; sobram 7 moderadas só em ferramenta de lint (`minimatch`) |
| S4 | A regra de senha **exige maiúscula, minúscula e número**. O ASVS pede o contrário: nenhuma regra de composição, senha longa e bloqueio de senhas vazadas ou muito comuns | V2.1.7, V2.1.9 | ⏳ **Decisão sua**: muda o cadastro de todas as clientes |
| S5 | **Sem segundo fator no painel.** Quem tiver a senha da administradora tem a loja inteira (pedidos, estornos, dados das clientes). O roadmap deixava o 2FA para depois do lançamento | V2.2 / L2 | ⏳ **Decisão sua**: trazer para antes do lançamento ou aceitar o risco |

### Validação da 6.2 (18/11/2026)

| Item | Resultado |
|------|-----------|
| A1 | ✅ `dotnet build` com 0 avisos; `dotnet format` sem apontamento novo (só o fim de linha CRLF e os 5 espaços do `AuthExtensions.cs`, que não foi mexido) |
| A2 | ✅ lint, typecheck, **213 testes** da loja (11 novos) e build de produção contra a API em container |
| A3 | ✅ `npm audit --audit-level=high` sem achado; gitleaks no histórico inteiro dos dois repositórios sem achado (com o `.gitleaksignore`); `actionlint` sem erro nos quatro workflows. CodeQL e Dependabot só rodam no GitHub, no primeiro push |
| Testes | ✅ aplicação 36, domínio 211, arquitetura 8, funcionais **329 de 329** com a API como `mo_app` (11 novos: privilégio do banco e cache). Uma falha intermitente num teste da 6.1, que conferia a marcação do Outbox antes de ela acontecer; o teste agora espera a marcação e passou em três rodadas seguidas |
| B2 | ✅ Como `mo_app`: `CREATE TABLE` (inclusive em `public`), `DROP`, `ALTER`, `TRUNCATE`, `CREATE SCHEMA`, `CREATE ROLE` e `DELETE` no journal → `permission denied`; leitura, escrita e sequências funcionam; o papel não é superusuário nem cria papel ou banco |
| B2 (container) | ✅ O migrator criou o `mo_app` no banco local e a API em container passou a usá-lo (5 conexões `mo_app`, nenhuma da API como dono) |
| C5 | 🟡 Loja em produção: headers certos em vitrine e painel, câmera liberada só no `/admin`, HSTS só com https. **Nenhuma violação** em home, produto, categoria, sacola, login, Meus pedidos e painel (dashboard, pedidos, produtos, estoque, frete). Uma violação forjada chegou ao `/api/csp-report`, então o relatório funciona. ⏳ Falta o cartão: sem a chave pública do Mercado Pago, a loja local não carrega o SDK. A CSP fica em `Report-Only` até isso |
| C6 | ⏳ Varredura ZAP: depende do staging (6.5) |

**O que fazer no GitHub depois do merge:** em Settings → Code security dos dois repositórios, ligar **Dependabot alerts** e **Dependabot security updates** (o arquivo `.github/dependabot.yml` cuida só das atualizações semanais), e conferir se o **secret scanning** com push protection está ligado.

### Ajustes ao plano feitos na 6.3

- **Exclusão de conta**:
  - O e-mail vira `removida-<id>@invalid` (domínio reservado), e o endereço original fica livre para um cadastro novo.
  - A senha vira um valor que nenhum verificador aceita.
  - A conta fica inativa, com `CHECK` no banco.
  - O painel recusa reativar ou promover a conta (`422 Identity.AccountRemoved`) e mostra a linha sem ações.
  - O cache do stamp é apagado na hora, então o access token que ainda estiver valendo para no próximo request (`403`). Antes da correção ele passava por até 1 min.
- **O que fica no pedido**:
  - Ficam peças, valores, datas, frete, cidade e UF.
  - Destinatário, rua, número, complemento e bairro viram "Cliente removida"/"Removido", e o CEP vira `00000000`.
  - Dedicatória e observação somem.
  - As notas do histórico são da loja e ficam.
  - Endereços, sacola e links de e-mail pendentes são apagados.
- **Bloqueios da exclusão** (P7): pedido em aguardando pagamento, pago, em separação ou enviado → `422 Identity.AccountHasOpenOrders`; estorno recusado, ou pagamento aprovado em pedido cancelado ou devolvido → `422 Identity.AccountHasPendingRefund`. A senha errada é `400 Identity.WrongCurrentPassword`, sob o limite `auth-strict`.
- **Exportação** (P8):
  - Vai junto uma lista `notes` explicando o que a loja não guarda: cartão, cookie além do de sessão e marketing. Não há tabela de consentimentos, porque a loja não pede nenhum (P9).
  - O limite de 3 por dia fica em memória: reiniciar a API zera a contagem. Com uma instância só, isso basta.
- **Devolução** (P11):
  - O estorno sai pelo Outbox só para pagamento da plataforma. Pagamento confirmado à mão entra na fila de estornos pendentes, que agora aceita pedido `Returned` e mostra se o pedido foi cancelado ou devolvido.
  - O EM-13 é o mesmo do estorno, mandado quando o estorno é aceito.
  - Entregue passa a ter a ação `Return`, e o teste da Fase 5 que esperava "nenhuma ação" foi atualizado.
- **Identificação da loja**:
  - Vem de `NEXT_PUBLIC_STORE_LEGAL_NAME`, `_CNPJ`, `_ADDRESS`, `_EMAIL` e `_PHONE`, fixadas no build como os outros endereços.
  - No CI saem das variáveis `STORE_*` do repositório da loja no GitHub, e o Dockerfile já recebe os `ARG`.
  - O que não estiver configurado não aparece no rodapé, e as páginas legais avisam que são rascunho.
- **`noindex` do staging** ficou para a 6.5, junto com o staging.

### Validação da 6.3 (20/11/2026)

| Item | Resultado |
|------|-----------|
| A1 | ✅ `dotnet build` com 0 avisos |
| A2 | ✅ lint, typecheck, **222 testes** da loja (9 novos: identificação da loja e regras da tela de privacidade) e build de produção; as 4 páginas novas saem estáticas |
| Testes | ✅ domínio **222** (11 novos: devolução e anonimização), aplicação 36, arquitetura 8, funcionais **337 de 337** (8 novos em `PrivacyTests` e 1 teste da Fase 5 atualizado) |
| B1 | ✅ `fase-0` a `fase-6-3-schema.sql` aplicados em ordem por `psql` num banco vazio (22 migrations); a segunda execução do `fase-6-3` recusa com "Script já aplicado"; o migrator do container aplicou a V0022 no banco local |
| C7 | ✅ API em container: `200` com `Content-Disposition: attachment`, só a própria conta, com dedicatória, histórico e pagamento. Na tela, "Baixar arquivo" gera o JSON sem erro no console |
| C8 | ✅ API em container e tela: com pedido aguardando pagamento, `422` com a mensagem na tela; senha errada, "A senha não confere."; depois de cancelar, `204`, tela de confirmação e sessão encerrada; login `401`; token antigo `403`; no banco, nome "Cliente removida", sem telefone, CPF, endereços nem dedicatória; painel com "Cliente removida" e sem ações na lista de usuários |
| C9 | ✅ API em container: Devolvido, estorno `Refunded` pelo Outbox, EM-13 no Mailpit, estoque igual. Na tela: botão "Registrar devolução" no pedido entregue, motivo obrigatório, aviso de que o estoque não volta sozinho, aba "Devolvidos" na lista |
| C10 | 🟡 Rodapé com os links legais em todas as páginas da loja, sem rolagem lateral em 375 px. ⏳ **Dados da loja**: dependem do §6 item 4; enquanto faltarem, o rodapé mostra só "MO Acessórios" |

**O que falta para fechar a 6.3 de vez (não é código):** razão social, CNPJ, endereço, e-mail e telefone de atendimento da loja; as decisões marcadas nas páginas (envio da devolução, prazo e frete da troca, cobertura da garantia do banho, parcelamento); e a revisão jurídica dos três textos.

### Feed do Merchant Center (6.6, 20/11/2026)

- **Onde**:
  - A API monta o XML em `GET /api/v1/feeds/google-merchant.xml` (RSS 2.0 com `g:`).
  - A loja serve o mesmo conteúdo em `/feeds/google-merchant.xml`, que é o endereço a cadastrar no Merchant Center, no domínio verificado.
  - A rota da loja é dinâmica, porque o build da imagem roda contra a API falsa. Ela guarda a resposta por 1 h com as tags `products`, `categories` e `collections`, e cai junto com a revalidação do catálogo.
  - Se a API falhar, a loja responde `503`, não um feed vazio, para as peças não saírem do Google.
- **O que entra**:
  - Uma entrada por variação ativa de peça ativa, em categoria ativa e com foto. O Google recusa item sem foto: a peça entra quando ganhar uma.
  - `g:id` é o SKU, e `item_group_id` é o id da peça.
  - O título é "peça - cor - tamanho", e o link é o canônico com `?cor=&tamanho=`.
  - A foto é a da cor primeiro e depois as gerais (até 10 adicionais).
  - O preço é o da variação, ou o da peça quando a variação não tem.
  - `product_type` é "categoria > subcategoria".
- **Esgotada continua no feed** como `out_of_stock`, em vez de sair. Assim o Google mantém o histórico do item, e ele volta sozinho quando o estoque volta.
- **GTIN** só com o dígito verificador certo (8, 12, 13 ou 14 dígitos); sem ele, `identifier_exists=no`. Marca "MO Acessórios", `condition=new`, `age_group=adult`.
- **Gênero**: `female` por padrão e `male` para peças da coleção `eles`. É exigido pelo Google para acessórios no Brasil e é ajustável em `MerchantFeed:DefaultGender` e `MerchantFeed:MaleCollections`.
- **Links**: usam `MerchantFeed:StoreUrl`, ou, sem ele, o `Email:FrontendBaseUrl` que os e-mails já usam. Em produção é o domínio da loja.
- **Cache na API**: 1 h, apagado por qualquer escrita de peça, estoque, categoria ou coleção.

| Item | Resultado |
|------|-----------|
| A1/A2 | ✅ `dotnet build` sem aviso; lint, typecheck, **224 testes** da loja (2 novos da rota) e build de produção, com `/feeds/google-merchant.xml` dinâmica |
| Testes | ✅ domínio 222, aplicação **47** (11 novos: GTIN e XML), arquitetura 8, funcionais **340 de 340**: 3 novos (`MerchantFeedTests`: variações agrupadas com GTIN, preço, disponibilidade e link; peça inativa, rascunho e sem foto fora; feed vazio público), com a variação desativada saindo na leitura seguinte; a rota entrou na lista de rotas anônimas do teste de segurança |
| Container | ✅ API em container com o banco de desenvolvimento: 40 itens, o mesmo número de variações elegíveis contadas por SQL; XML válido; `application/xml` e `Cache-Control: public, max-age=3600`. Os 24 códigos de barras do banco de exemplo estão em peças inativas, então nenhum GTIN aparece ali, como deveria |
| Loja | ✅ build de produção contra a API em container: a loja serve o mesmo XML, byte a byte. Ao tirar uma peça da loja pelo painel, o feed da loja cai de 40 para 34 itens em 3 s pela revalidação, e volta para 40 em 4 s ao reativar |
| C19, C20 | ⏳ Dependem das contas do Google e do domínio (6.5) |

### E2E e carga (6.4, 20/11/2026)

- **Onde**: os testes ficam em `e-commerce-frontend/e2e/`, com instruções no README. A pilha `e2e/compose.yml` sobe Postgres sem volume, com o seed de desenvolvimento, Mailpit, migrator e API das imagens, com pagamento e frete falsos. A loja roda com o build de produção na porta 3100.
- **Os 5 fluxos**:
  - compra com PIX, com o cadastro e a confirmação pelo link do Mailpit;
  - compra com cartão de teste;
  - cancelamento pela cliente, com o e-mail;
  - painel do pagamento à devolução, com os e-mails EM-08, EM-09 e EM-13;
  - busca → categoria → produto → sacola em 375 px, sem rolagem lateral.
- **axe** roda em cada página visitada (WCAG 2.1 A e AA) e falha em violação séria ou crítica. **Nenhuma apareceu**: cadastro, conta, confirmação, produto, sacola, os três passos do checkout, pagamento PIX e cartão, Meus pedidos, painel e a vitrine no celular.
- **CI da loja**: novo job `e2e` com as imagens `:main` da API e do migrator, ou outras pelas variáveis `E2E_API_IMAGE` e `E2E_MIGRATOR_IMAGE`. A publicação da imagem da loja passa a esperar o E2E verde. Se o job falhar, o relatório, os traces e o log da pilha ficam como artefato por 7 dias.
- **CI do backend**: as imagens da API e do migrator ganham também a tag móvel `main`, além da `sha-…`, só para o E2E da loja. O deploy continua pelo digest.
- **Ajustes de teste**:
  - o link de confirmação de e-mail pede um clique, e o teste clica como a cliente;
  - no celular, o "Adicionar à sacola" existe duas vezes (o fixo no rodapé e o do conteúdo), e o teste usa o visível;
  - o passo de endereço do checkout foi feito determinístico depois de uma corrida no próprio teste.
- **k6** (`e-commerce-backend/tests/load/`): vitrine a 100 req/s, frete a 10/s, checkout com 20 clientes e disputa pela última unidade. As metas do RNF-001 são limites do script, que sai com erro se alguma falhar. O README explica a rodada na VM (P15): Mailpit como SMTP e limites por IP altos só durante o teste, e o banco recriado depois.

| Item | Resultado |
|------|-----------|
| A2 | ✅ lint, typecheck (inclui `e2e/`), 224 testes da loja; `actionlint` sem erro nos workflows dos dois repositórios; `npm audit --audit-level=high` sem achado com o Playwright e o axe |
| C11 (local) | ✅ **5 de 5** fluxos verdes, duas rodadas seguidas no mesmo banco (1,3 min cada), contra a API em container gerada do backend local e o build de produção da loja; nenhuma violação séria do axe |
| C11 (CI) | ⏳ Primeira rodada no GitHub depois do merge do backend na `main` (tag `main` das imagens) |
| k6 (rodada curta) | ✅ Contra a pilha de E2E: todos os limites verdes. A disputa criou **1** pedido e recusou os outros com `409 Ordering.OutOfStock`, e o saldo terminou em 0 |
| C12 | ⏳ Na VM de produção (6.5) |

### Preparo da 6.5 feito sem o domínio (20/11/2026)

- **Configuração de produção** (§4.2), que completa o que a 6.1 começou:
  - Em `Production`, a API recusa subir com a credencial de teste do Mercado Pago (`TEST-...`), sem a assinatura do webhook ou com o Melhor Envio apontando para o sandbox.
  - Fora de Development, os provedores falsos e a chave JWT de desenvolvimento já eram recusados.
  - O staging (`Staging`) continua aceitando credencial de teste e sandbox.
  - Testes novos: `PaymentConfigurationTests` (4) e um caso a mais em `ShippingConfigurationTests`.
- **Staging fechado para buscadores** (P13, C18), ligado por `NEXT_PUBLIC_ENV=staging` no build:
  - `robots.txt` com `Disallow: /` e sem sitemap;
  - `X-Robots-Tag: noindex, nofollow` em toda resposta, inclusive fotos e feed.
  - Conferido com um build de staging e um padrão, que não mudou. O Dockerfile da loja já recebe o `ARG`.
- **Suíte completa**: domínio 222, aplicação 47, arquitetura 8, funcionais 344 de 345. A única falha foi de novo o `MercadoPagoGatewayTests.Get_MapsTheStatus(charged_back)`, o timeout do WireMock com a suíte inteira já registrado na 6.1; sozinho, o adaptador passa nos 16. Na loja, 228 testes.

### Riscos

| Risco | Mitigação |
|-------|-----------|
| A CSP quebra o Brick do Mercado Pago em algum caminho raro (3DS, outra bandeira) | `Report-Only` primeiro no staging (P5); C5 com cartão de teste que pede 3DS |
| O Mercado Pago demora para liberar a conta de produção (verificação de identidade) | Pedir as credenciais no início da 6.5, não na 6.7 |
| E-mail novo cai no spam nas primeiras semanas | DMARC em `p=none` com relatório primeiro; volume baixo e só transacional; C16 antes do lançamento |
| A VM única é ponto único de falha (risco aceito no arquivo 18 §7) | Monitor externo, backup criptografado fora da VM e restauração ensaiada (C15) |
| Os textos legais não chegam a tempo | Bloqueiam a 6.7; o resto avança |

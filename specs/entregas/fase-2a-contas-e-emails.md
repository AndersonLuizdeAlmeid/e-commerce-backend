# Entrega — Fase 2A: Contas e e-mails

**Status:** Implementada em 26/10/2026, compilando. **Validação em andamento.**
**Repositórios:** `e-commerce-backend` e `e-commerce-frontend`.

## 1. Contexto

A Fase 2 do roadmap foi dividida em duas entregas (decisão de 22/10/2026):

| Entrega | Conteúdo |
|---------|----------|
| **2A — Contas e e-mails** (esta) | Cadastro, login, sessão, senha, confirmação de e-mail, perfil, admin inicial, e-mails pelo Outbox |
| 2B — Painel de catálogo | Admin de categorias (com subcategoria), cores, tamanhos, produtos, grade, código de barras, upload de fotos, invalidação de cache e revalidação das páginas |

Decisões que esta entrega aplica (arquivo 20): D04 (Resend, com Mailpit em dev), D05 (checkout exigirá e-mail confirmado), Q06 (admin não usa a área de cliente), T03 (PBKDF2), T11 (Minimal APIs), T12 e T13.

## 2. Objetivos

1. O visitante **cria conta** e já sai logado; recebe o e-mail de boas-vindas com o link de confirmação.
2. O cliente **entra, sai e continua logado** entre visitas sem guardar token no navegador (access token em memória, refresh em cookie HttpOnly com rotação).
3. O cliente **recupera a senha** por e-mail, **troca a senha** e **confirma o e-mail**; cada troca encerra as outras sessões e avisa por e-mail.
4. O cliente **edita os próprios dados** (nome, celular, CPF).
5. A API fica **protegida por padrão**: todo endpoint exige token, salvo os declarados públicos; limites de requisição contra força bruta; headers de segurança.
6. **Nenhum e-mail se perde nem sai por engano**: e-mails vão para o Outbox na mesma transação do caso de uso e um processador em segundo plano envia com novas tentativas.
7. O admin inicial existe sem precisar de script com senha.

## 3. Escopo entregue

### Banco

| Script | Conteúdo |
|--------|----------|
| `V0006__identity.sql` | `identity.users`, `refresh_tokens` (com expiração absoluta da família), `user_tokens` |
| `V0007__customers.sql` | `customers.customers` (endereços ficam para a Fase 4) |
| `V0008__messaging_outbox_and_email_log.sql` | `messaging.outbox_messages`, `notifications.email_log` |

Para o DBeaver: `database/fase-2a-schema.sql`. Não há dados de exemplo nesta fase: o admin é criado pela API (abaixo).

### API (`/api/v1`)

| Endpoint | Requisito | Acesso |
|----------|-----------|--------|
| `POST /auth/register` → 201 + cookie | RF-001 CA1–CA5 | público, `auth-strict` |
| `POST /auth/login` | RF-002 CA1–CA4 | público, `auth-strict` |
| `POST /auth/refresh` (cookie + `X-Requested-With`) | RF-003 CA1–CA2 | público, `auth-refresh` |
| `POST /auth/logout` (cookie + `X-Requested-With`) → 204 | RF-004 | público* |
| `POST /auth/change-password` → nova sessão | RF-006 | logado |
| `POST /auth/forgot-password` → sempre 202 | RF-008 CA1 | público, `auth-strict` |
| `POST /auth/reset-password` → 204 | RF-008 | público, `auth-strict` |
| `POST /auth/confirm-email` → 204 | RF-009 | público |
| `POST /auth/resend-confirmation` → 202 | RF-009 CA1 | logado |
| `GET /auth/me` | — | logado |
| `GET/PUT /me/profile` | RF-005 | **Customer** |

\* O logout funciona pelo cookie mesmo com o access token vencido (mudança em relação ao arquivo 07, que pedia token).

Peças transversais que entraram agora: `ICommandHandler` com decorators de **validação** e **transação** (`IUnitOfWork`), marcador `IPersistOnFailure` (login errado e reuso de refresh precisam gravar mesmo devolvendo erro), blocos do Domain (`Entity`, `AggregateRoot`, VOs `Email`, `Cpf`, `Phone`), JWT HS256 com `sstamp`, policies com **fallback autenticado** e `ActiveSecurityStamp` no Admin, **rate limiter** (global, `auth-strict`, `auth-refresh`, `public-read`, `customer-write`, partição interna para o servidor do Next.js), headers de segurança, `ForwardedHeaders`, **Outbox** com `FOR UPDATE SKIP LOCKED`, backoff e remoção dos tokens do payload depois do envio, `IEmailSender` com SMTP (Mailpit) e Resend, templates EM-01/02/03, job diário de limpeza, seed do admin (RF-060) na subida da API.

### Frontend

| Página | O que faz |
|--------|-----------|
| `/login` | Entrar; volta para `?next=` (só caminhos internos) |
| `/cadastro` | Criar conta e já entrar |
| `/recuperar-senha` | Pedir o link (mensagem igual exista ou não a conta) |
| `/redefinir-senha?token=` | Nova senha |
| `/confirmar-email?token=` | Confirma com um clique (não ao abrir, por causa de antivírus que visitam links) |
| `/conta` | Dados, status do e-mail com reenvio, troca de senha, sair |

Todas com `noindex`. Cabeçalho mostra "Entrar" ou "Minha conta". Sessão: access token em memória, refresh silencioso ao abrir a página só se já houve login no navegador, renovação única compartilhada (single-flight) e repetição automática em 401.

### Testes escritos

| Projeto | Cobertura |
|---------|-----------|
| `ECommerce.Domain.UnitTests` (novo) | Lockout (RN-002), troca de senha e stamp (RN-003), rotação e limite absoluto do refresh, validade dos tokens de e-mail, VOs Email/CPF/Telefone/nome |
| `ECommerce.Application.UnitTests` | Validators de cadastro e troca de senha |
| `ECommerce.Api.FunctionalTests` | RF-001 a RF-009, RF-060, Q06; CSRF do refresh; varredura de endpoints anônimos; rate limit CA1/CA3; headers; Outbox processa, remove segredos, registra log mascarado e tenta de novo após falha |
| `ECommerce.ArchitectureTests` | Command handlers selados e internos; entidades sem setter público |
| Frontend (Vitest) | Regras dos formulários (senha, CPF, telefone, e-mail) e proteção contra open redirect no `?next=` |

## 4. Fora do escopo

- Endereços (RF-007) → Fase 4, com o checkout.
- Painel administrativo → 2B. Um admin que entra hoje só vê a mensagem de que o painel vem na próxima etapa.
- Envio real pelo Resend → depende do **domínio** (pendência do arquivo 20). Até lá, Mailpit.
- Bloqueio do checkout sem e-mail confirmado (D05) → aplicado na Fase 4, quando o checkout existir.
- Identidade visual definitiva (`/impeccable`) → antes das telas da 2B.

## 5. Como validar

### Etapa A — build

| # | Comando | Esperado | Situação |
|---|---------|----------|----------|
| A1 | `dotnet build ECommerce.sln` | 0 erros, 0 avisos | ✅ 26/10/2026 (com `--artifacts-path`, porque o Visual Studio estava com a API aberta) |
| A2 | `dotnet format ECommerce.sln --verify-no-changes` | sem alterações | ✅ 26/10/2026 |
| A3 | `npm run lint && npm run typecheck` | sem erros | ✅ 26/10/2026 |
| A4 | `npm test` | verdes | ✅ 32 testes, 26/10/2026 |
| A5 | `npm run build` contra o stub | conclui; páginas de conta estáticas | ✅ 26/10/2026 |

### Etapa B — banco pelo DBeaver

| # | Passo | Esperado |
|---|-------|----------|
| B1 | Rodar `database/fase-2a-schema.sql` num banco com a Fase 1 | `COMMIT`; V0006–V0008 em `public.schemaversions` |
| B2 | Rodar B1 de novo | para no início com "Script já aplicado" |

### Etapa C — execução

| # | Verificação | Esperado |
|---|-------------|----------|
| C1 | `docker compose up -d postgres mailpit` e `dotnet test ECommerce.sln` | todos verdes |
| C2 | Subir a API em Development | log "Admin inicial criado (RF-060)" na primeira vez; `admin@loja.local` em `identity.users` |
| C3 | `npm run gen:api` e comparar `schema.d.ts` | sem diferença relevante (tipos novos foram escritos à mão) |
| C4 | `/cadastro` | entra logado em `/conta`; e-mail de boas-vindas no Mailpit (http://localhost:8025) |
| C5 | Clicar no link do e-mail → "Confirmar meu e-mail" | `/conta` mostra e-mail confirmado |
| C6 | Recarregar a página e fechar/abrir o navegador | continua logado (refresh pelo cookie); DevTools não mostra token em localStorage |
| C7 | "Esqueci minha senha" → link → nova senha | outra aba logada perde a sessão; e-mail "Sua senha foi alterada" |
| C8 | 5 senhas erradas e depois a certa | continua recusando por 15 min |
| C9 | 6 logins em 1 min (fora dos testes) | 6º responde 429 com `Retry-After` |
| C10 | Entrar como `admin@loja.local` e abrir `/conta` | mensagem de conta de administrador (403 na API) |
| C11 | `SELECT payload FROM messaging.outbox_messages` | nenhum `secrets` em mensagens processadas; `email_log` com destinatário mascarado |

**Critério de aceite:** A1–A5, B1–B2 e C1–C11 passando.

## 6. Pontos de atenção

| Ponto | Detalhe |
|-------|---------|
| Varredura de endpoints anônimos | A lista esperada inclui as rotas do OpenAPI e do Scalar com os padrões que imagino (`/openapi/{documentName}.json`, `/docs/{documentName?}`); se o primeiro `dotnet test` divergir só nisso, ajustar a lista |
| `schema.d.ts` à mão | C3 regenera e compara |
| Chave JWT de desenvolvimento | Está no `appsettings.Development.json` e contém `DEV-ONLY`; a API **recusa subir** com ela fora de Development/Testing. Em staging/produção: `Jwt__SigningKey` por variável de ambiente (≥ 32 bytes) |
| Cookie `Secure` | Funciona em `http://localhost` nos navegadores; nos testes é desligado por configuração |
| Mailpit | Imagem `axllent/mailpit:v1.27`; se a tag não existir no registry, usar a mais recente estável |
| Duas renovações simultâneas | O frontend compartilha uma única renovação; abas diferentes ainda podem disputar e uma delas perder a sessão (aceito no MVP) |

## 7. Próxima entrega — 2B (prévia)

Antes: rodar o `/impeccable` para a identidade visual. Escopo: `catalog.categories.parent_id` (Q07), agregados `Product`/`Category`/`Color`/`Size` no Domain, RF-050/051/056/057/059, upload para R2 (MinIO em dev), invalidação do HybridCache por tag após o commit, revalidação sob demanda das páginas (`/api/revalidate`) via Outbox, `slug_history` ao trocar slug e telas do painel.

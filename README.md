# Especificações do E-commerce (Spec-Driven Development)

Este diretório é a **fonte da verdade** do projeto. Todo código deve nascer de uma especificação aqui descrita; mudanças de comportamento começam por uma alteração na spec (via PR), e só depois no código.

## Como ler

| # | Arquivo | Assunto |
|---|---------|---------|
| 00 | [00-visao-geral.md](00-visao-geral.md) | Visão do produto, escopo, fora de escopo, glossário |
| 01 | [01-requisitos-funcionais.md](01-requisitos-funcionais.md) | Histórias de usuário e critérios de aceite (Cliente / Admin) |
| 02 | [02-requisitos-nao-funcionais.md](02-requisitos-nao-funcionais.md) | Performance, disponibilidade, segurança, manutenibilidade |
| 03 | [03-arquitetura.md](03-arquitetura.md) | Clean Architecture, camadas, estrutura da solution |
| 04 | [04-dominio-ddd.md](04-dominio-ddd.md) | Bounded contexts, agregados, value objects, eventos, invariantes |
| 05 | [05-banco-de-dados.md](05-banco-de-dados.md) | Modelo PostgreSQL, convenções, migrations |
| 06 | [06-acesso-a-dados-dapper.md](06-acesso-a-dados-dapper.md) | Dapper, repositórios, Unit of Work, transações, concorrência |
| 07 | [07-api-contratos.md](07-api-contratos.md) | Endpoints REST, versionamento, paginação, OpenAPI |
| 08 | [08-autenticacao.md](08-autenticacao.md) | JWT, refresh token, hashing de senha, fluxos |
| 09 | [09-autorizacao.md](09-autorizacao.md) | Perfis Cliente/Administrador, policies, autorização por recurso |
| 10 | [10-cors.md](10-cors.md) | Política de CORS |
| 11 | [11-rate-limiting.md](11-rate-limiting.md) | Rate limiter por IP / usuário / endpoint |
| 12 | [12-cache.md](12-cache.md) | Estratégia de cache e invalidação |
| 13 | [13-validacao-e-erros.md](13-validacao-e-erros.md) | Validação, Result pattern, ProblemDetails |
| 14 | [14-observabilidade.md](14-observabilidade.md) | Logs, tracing, métricas, health checks |
| 15 | [15-testes.md](15-testes.md) | Pirâmide de testes, ferramentas, cobertura |
| 16 | [16-frontend-react.md](16-frontend-react.md) | Arquitetura do frontend (React + Next.js) |
| 17 | [17-seguranca.md](17-seguranca.md) | OWASP, headers, segredos, LGPD |
| 18 | [18-devops-ambiente.md](18-devops-ambiente.md) | Docker, CI/CD, configuração por ambiente |
| 19 | [19-roadmap.md](19-roadmap.md) | Fases de entrega / marcos |
| 20 | [20-decisoes-em-aberto.md](20-decisoes-em-aberto.md) | Decisões tomadas e **perguntas ainda em aberto** |
| 21 | [21-pagamentos.md](21-pagamentos.md) | Pagamento integrado (PIX, cartão), webhooks, estornos |
| 22 | [22-frete-correios.md](22-frete-correios.md) | Cotação Correios, regras de frete, rastreio |
| 23 | [23-emails.md](23-emails.md) | E-mails transacionais, recuperação de senha |
| 24 | [24-seo.md](24-seo.md) | SEO: renderização no servidor, sitemap, dados estruturados |
| 25 | [25-processamento-assincrono.md](25-processamento-assincrono.md) | Outbox, jobs (cancelamento em 48 h, lembretes, reconciliação) |

## Convenções das specs

- **IDs rastreáveis**: `RF-xxx` (funcional), `RNF-xxx` (não funcional), `ADR-xxx` (decisão de arquitetura), `RN-xxx` (regra de negócio).
- **Palavras normativas** (RFC 2119): **DEVE** (obrigatório), **DEVERIA** (recomendado), **PODE** (opcional).
- Cada requisito funcional possui **critérios de aceite** em formato *Dado / Quando / Então*, que viram testes automatizados.
- Status de cada spec: `Rascunho` → `Em revisão` → `Aprovada`. Todas começam como **Rascunho** até fecharmos o arquivo 20.

## Fluxo de trabalho

1. Alterar/criar a spec → PR → revisão → `Aprovada`.
2. Escrever os testes a partir dos critérios de aceite.
3. Implementar até os testes passarem.
4. Referenciar o ID da spec no commit/PR (ex.: `feat(ordering): RF-040 criar pedido`).

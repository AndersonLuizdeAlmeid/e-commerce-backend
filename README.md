# e-commerce-backend

API REST do e-commerce em **.NET 10 (Minimal APIs) + PostgreSQL + Dapper**, em Clean Architecture.

As especificações são a fonte da verdade do projeto e ficam em [`specs/`](specs/README.md).

## Rodando localmente

Pré-requisitos: .NET SDK 10 e Docker.

Stack completa em containers (Postgres, migrations com dados de exemplo e API em http://localhost:5001):

```bash
docker compose up -d --build
```

Para desenvolver a API fora do container, suba só o banco e as migrations e depois rode a API:

```bash
docker compose up -d postgres migrator
```

```bash
dotnet run --project src/ECommerce.Api
```

| URL | O que é |
|-----|---------|
| http://localhost:5001/docs | Documentação interativa (Scalar) |
| http://localhost:5001/openapi/v1.json | Contrato OpenAPI (gera os tipos do frontend) |
| http://localhost:5001/health/ready | Banco acessível e migrations aplicadas |

## Testes

```bash
dotnet test ECommerce.sln
```

Os testes funcionais sobem um PostgreSQL real com Testcontainers, então o Docker precisa estar rodando.

## Estrutura

```
src/
├── ECommerce.Domain/          entidades, value objects, Result/Error
├── ECommerce.Application/     casos de uso (queries/commands) por feature
├── ECommerce.Infrastructure/  Dapper/Npgsql, migrations SQL (DbUp), health checks
├── ECommerce.Api/             endpoints, middlewares, CORS, ProblemDetails
└── ECommerce.Migrator/        aplica as migrations (roda antes do deploy)
database/                      scripts consolidados por fase para rodar no DBeaver
tests/
├── ECommerce.Application.UnitTests/   validators e regras puras da Application
├── ECommerce.ArchitectureTests/
└── ECommerce.Api.FunctionalTests/
```

## Migrations

Scripts SQL em `src/ECommerce.Infrastructure/Persistence/Migrations` (`V0001__...sql`), imutáveis depois de mergeados.
Cada entrega cria só as tabelas de que precisa. Dados de exemplo ficam em `Persistence/Seeds/Development` e só são aplicados em Development.

Para aplicar o banco à mão (DBeaver), use os scripts consolidados por fase em [`database/`](database/README.md). Eles se registram no journal do DbUp, então o migrator não tenta reaplicá-los.

## Entregas

Objetivos, escopo e checklist de aceite de cada fase ficam em [`specs/entregas/`](specs/entregas/) (fase 1, fase 2A).


## Serviços locais

| URL | O que é |
|-----|---------|
| http://localhost:8025 | Mailpit: caixa de entrada dos e-mails de desenvolvimento (`docker compose up -d mailpit`) |

Admin de desenvolvimento, criado pela API na primeira subida: `admin@loja.local` / `Admin@12345`.

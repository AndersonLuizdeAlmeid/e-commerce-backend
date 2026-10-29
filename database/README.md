# Scripts de banco para execução manual (DBeaver)

As migrations oficiais continuam em `src/ECommerce.Infrastructure/Persistence/Migrations` (DbUp) e são as que os testes, o CI e o `docker compose` usam.
Os arquivos desta pasta são **cópias consolidadas** delas, geradas por `gerar-scripts.sh`, para aplicar o banco à mão sem rodar o `ECommerce.Migrator`.

| Ordem | Arquivo | Onde aplicar |
|-------|---------|--------------|
| 1 | `fase-0-schema.sql` | Todos os ambientes (pule se o banco já foi criado pelo migrator) |
| 2 | `fase-0-dados-exemplo.sql` | **Só Development** |
| 3 | `fase-1-schema.sql` | Todos os ambientes |
| 4 | `fase-1-dados-exemplo.sql` | **Só Development** (depende do 2) |
| 5 | `fase-2a-schema.sql` | Todos os ambientes (contas, sessões, Outbox, log de e-mails) |

## Como executar no DBeaver

1. Conecte no banco `ecommerce` (local: `localhost:5432`, usuário e senha `ecommerce`).
2. Abra o arquivo em um SQL Editor e execute como script (**Alt+X**), não como instrução única (Ctrl+Enter).
3. Cada arquivo roda numa transação. Se der erro, nada é aplicado.

## Garantias

- Cada script se **registra** em `public.schemaversions` (estrutura) ou `public.seedversions` (dados de exemplo), com o mesmo nome que o DbUp usaria. Depois disso o `ECommerce.Migrator` não tenta aplicá-lo de novo e o `/health/ready` não acusa migration pendente.
- Rodar o mesmo arquivo duas vezes falha logo no início com `Script já aplicado neste banco`, sem alterar nada.
- Rodar fora de ordem falha com `Pré-requisito ausente`.

## Conferência rápida depois da fase 1

```sql
SELECT scriptname, applied FROM public.schemaversions ORDER BY schemaversionsid;
SELECT count(*) AS cores FROM catalog.colors;          -- 7
SELECT count(*) AS tamanhos FROM catalog.sizes;        -- 14
-- com os dados de exemplo:
SELECT p.slug, count(v.*) AS variacoes
  FROM catalog.products p JOIN catalog.product_variants v ON v.product_id = p.id
 GROUP BY p.slug ORDER BY p.slug;                       -- 9 produtos, camiseta-basica com 24
```

## Depois da fase 2A

O admin inicial (RF-060) não vem em script: a API cria na primeira subida a partir de `AdminSeed:Email` e `AdminSeed:Password` (em Development: `admin@loja.local` / `Admin@12345`), se ainda não existir nenhum admin ativo. Senha só existe como hash PBKDF2 gerado pela aplicação.

```sql
SELECT email, role, email_confirmed_at IS NOT NULL AS confirmado FROM identity.users;
SELECT type, attempts, processed_at, failed_at, last_error FROM messaging.outbox_messages ORDER BY occurred_at DESC LIMIT 20;
SELECT template, recipient_masked, status, created_at FROM notifications.email_log ORDER BY created_at DESC LIMIT 20;
```

## Ao criar uma migration nova

1. Crie o `Vnnnn__descricao.sql` (ou `Snnnn__...` para dados de exemplo) e nunca altere um já mergeado.
2. Inclua o arquivo na fase correspondente em `gerar-scripts.sh` e rode `bash database/gerar-scripts.sh` no Git Bash.
3. Faça commit da migration e dos scripts gerados juntos.

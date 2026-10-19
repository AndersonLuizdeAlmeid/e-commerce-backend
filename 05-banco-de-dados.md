# 05 — Banco de Dados (PostgreSQL)

**Status:** Em revisão

## 1. Versão e extensões

- **PostgreSQL 17** (ou superior).
- Extensões: `citext` (e-mail case-insensitive), `pg_trgm` (busca por similaridade), `unaccent` (busca sem acento).
- Como `unaccent()` não é `IMMUTABLE`, a migration inicial cria um wrapper para uso em índices:
  ```sql
  CREATE FUNCTION public.immutable_unaccent(text) RETURNS text
    LANGUAGE sql IMMUTABLE PARALLEL SAFE STRICT
    AS $$ SELECT public.unaccent('public.unaccent', $1) $$;
  ```

## 2. Convenções

| Item | Convenção |
|------|-----------|
| Nomes | `snake_case`, tabelas no **plural** |
| Schemas | Um por bounded context: `identity`, `customers`, `catalog`, `inventory`, `carts`, `shipping`, `ordering`, `payments`, `notifications`, `messaging` |
| PK | `id uuid primary key` (UUID v7 gerado na aplicação) |
| Datas | `timestamptz` em UTC; `created_at`, `updated_at` |
| Dinheiro | `numeric(12,2)` |
| Enums | `text` + `CHECK` |
| Concorrência | `version int not null default 0` em agregados mutáveis |
| FK | sempre indexadas; `ON DELETE RESTRICT` por padrão |
| Exclusão | lógica (`is_active`) para catálogo |
| Tokens | sempre armazenados como **hash SHA-256**, nunca em texto puro |

## 3. Modelo (DDL de referência)

```sql
-- ===================== identity =====================
CREATE TABLE identity.users (
    id                     uuid PRIMARY KEY,
    email                  citext NOT NULL UNIQUE,
    password_hash          text   NOT NULL,
    role                   text   NOT NULL CHECK (role IN ('Customer','Admin')),
    is_active              boolean NOT NULL DEFAULT true,
    email_confirmed_at     timestamptz NULL,
    failed_login_attempts  int     NOT NULL DEFAULT 0,
    lockout_end_utc        timestamptz NULL,
    security_stamp         uuid    NOT NULL,
    version                int     NOT NULL DEFAULT 0,
    created_at             timestamptz NOT NULL,
    updated_at             timestamptz NOT NULL
);

CREATE TABLE identity.refresh_tokens (
    id               uuid PRIMARY KEY,
    user_id          uuid NOT NULL REFERENCES identity.users(id),
    token_hash       text NOT NULL UNIQUE,
    family_id        uuid NOT NULL,
    expires_at       timestamptz NOT NULL,
    created_at       timestamptz NOT NULL,
    created_by_ip    inet NULL,
    revoked_at       timestamptz NULL,
    replaced_by_id   uuid NULL
);
CREATE INDEX ix_refresh_tokens_user ON identity.refresh_tokens(user_id);

-- confirmação de e-mail e recuperação de senha
CREATE TABLE identity.user_tokens (
    id          uuid PRIMARY KEY,
    user_id     uuid NOT NULL REFERENCES identity.users(id),
    purpose     text NOT NULL CHECK (purpose IN ('EmailConfirmation','PasswordReset')),
    token_hash  text NOT NULL UNIQUE,
    expires_at  timestamptz NOT NULL,
    used_at     timestamptz NULL,
    created_at  timestamptz NOT NULL
);
CREATE INDEX ix_user_tokens_user ON identity.user_tokens(user_id, purpose);

-- ===================== customers =====================
CREATE TABLE customers.customers (
    id          uuid PRIMARY KEY REFERENCES identity.users(id),
    full_name   text NOT NULL,
    phone       text NULL,
    cpf         char(11) NULL UNIQUE,
    created_at  timestamptz NOT NULL,
    updated_at  timestamptz NOT NULL
);

CREATE TABLE customers.addresses (
    id            uuid PRIMARY KEY,
    customer_id   uuid NOT NULL REFERENCES customers.customers(id),
    recipient     text NOT NULL,
    postal_code   char(8) NOT NULL,
    street        text NOT NULL,
    number        text NOT NULL,
    complement    text NULL,
    district      text NOT NULL,
    city          text NOT NULL,
    state         char(2) NOT NULL,
    is_default    boolean NOT NULL DEFAULT false,
    created_at    timestamptz NOT NULL
);
CREATE INDEX ix_addresses_customer ON customers.addresses(customer_id);
CREATE UNIQUE INDEX ux_addresses_default ON customers.addresses(customer_id) WHERE is_default;

-- ===================== catalog =====================
CREATE TABLE catalog.categories (
    id               uuid PRIMARY KEY,
    name             text NOT NULL UNIQUE,
    slug             text NOT NULL UNIQUE,
    description      text NOT NULL DEFAULT '',     -- texto indexável (SEO-10)
    seo_title        text NULL,
    seo_description  text NULL,
    is_active        boolean NOT NULL DEFAULT true,
    created_at       timestamptz NOT NULL,
    updated_at       timestamptz NOT NULL
);

CREATE TABLE catalog.colors (
    id         uuid PRIMARY KEY,
    name       text NOT NULL UNIQUE,              -- "Azul Marinho"
    slug       text NOT NULL UNIQUE,
    hex        char(7) NOT NULL                   -- "#1B2A4A"
);

CREATE TABLE catalog.sizes (
    id          uuid PRIMARY KEY,
    name        text NOT NULL UNIQUE,             -- "M", "38"
    sort_order  smallint NOT NULL
);

CREATE TABLE catalog.products (
    id                uuid PRIMARY KEY,
    name              text NOT NULL,
    slug              text NOT NULL UNIQUE,
    description       text NOT NULL DEFAULT '',
    category_id       uuid NOT NULL REFERENCES catalog.categories(id),
    base_price        numeric(12,2) NOT NULL CHECK (base_price > 0),
    weight_grams      int  NOT NULL CHECK (weight_grams > 0),
    height_cm         numeric(6,1) NOT NULL,
    width_cm          numeric(6,1) NOT NULL,
    length_cm         numeric(6,1) NOT NULL,
    seo_title         text NULL,
    seo_description   text NULL,
    is_active         boolean NOT NULL DEFAULT true,
    version           int NOT NULL DEFAULT 0,
    created_at        timestamptz NOT NULL,
    updated_at        timestamptz NOT NULL
);
CREATE INDEX ix_products_category  ON catalog.products(category_id) WHERE is_active;
CREATE INDEX ix_products_name_trgm ON catalog.products USING gin (immutable_unaccent(lower(name)) gin_trgm_ops);

CREATE TABLE catalog.product_variants (
    id            uuid PRIMARY KEY,
    product_id    uuid NOT NULL REFERENCES catalog.products(id),
    sku           text NOT NULL UNIQUE,
    barcode       varchar(14) NULL UNIQUE,                 -- EAN/GTIN (RF-059)
    color_id      uuid NULL REFERENCES catalog.colors(id),
    size_id       uuid NULL REFERENCES catalog.sizes(id),
    price         numeric(12,2) NULL CHECK (price > 0),   -- NULL = base_price do produto
    weight_grams  int NULL,
    height_cm     numeric(6,1) NULL,
    width_cm      numeric(6,1) NULL,
    length_cm     numeric(6,1) NULL,
    is_active     boolean NOT NULL DEFAULT true,
    created_at    timestamptz NOT NULL,
    updated_at    timestamptz NOT NULL
);
-- RN-021: uma combinação cor+tamanho por produto (NULLS NOT DISTINCT trata "sem cor"/"sem tamanho" como valor)
CREATE UNIQUE INDEX ux_variants_combination ON catalog.product_variants(product_id, color_id, size_id) NULLS NOT DISTINCT;
CREATE INDEX ix_variants_product ON catalog.product_variants(product_id) WHERE is_active;
CREATE INDEX ix_variants_color   ON catalog.product_variants(color_id) WHERE is_active;
CREATE INDEX ix_variants_size    ON catalog.product_variants(size_id)  WHERE is_active;

CREATE TABLE catalog.product_images (
    id          uuid PRIMARY KEY,
    product_id  uuid NOT NULL REFERENCES catalog.products(id),
    color_id    uuid NULL REFERENCES catalog.colors(id),   -- galeria por cor
    url         text NOT NULL,
    alt         text NOT NULL,
    position    smallint NOT NULL,
    UNIQUE (product_id, color_id, position)
);

CREATE TABLE catalog.slug_history (                      -- redirects 301 (SEO-02)
    old_slug     text PRIMARY KEY,
    entity_type  text NOT NULL CHECK (entity_type IN ('Product','Category')),
    entity_id    uuid NOT NULL,
    changed_at   timestamptz NOT NULL
);

-- ===================== inventory =====================
CREATE TABLE inventory.stock_items (
    variant_id        uuid PRIMARY KEY REFERENCES catalog.product_variants(id),
    quantity_on_hand  int NOT NULL CHECK (quantity_on_hand >= 0),
    low_stock_threshold int NOT NULL DEFAULT 3,
    version           int NOT NULL DEFAULT 0,
    updated_at        timestamptz NOT NULL
);

CREATE TABLE inventory.stock_movements (
    id           uuid PRIMARY KEY,
    variant_id   uuid NOT NULL REFERENCES inventory.stock_items(variant_id),
    type         text NOT NULL CHECK (type IN ('In','Out','Adjustment','OrderPlaced','OrderCancelled')),
    quantity     int  NOT NULL,                    -- delta com sinal
    reason       text NULL,
    order_id     uuid NULL,
    performed_by uuid NULL,
    created_at   timestamptz NOT NULL
);
CREATE INDEX ix_stock_movements_variant ON inventory.stock_movements(variant_id, created_at DESC);

-- ===================== carts =====================
CREATE TABLE carts.carts (
    id           uuid PRIMARY KEY,
    customer_id  uuid NOT NULL UNIQUE REFERENCES customers.customers(id),
    updated_at   timestamptz NOT NULL
);
CREATE TABLE carts.cart_items (
    cart_id     uuid NOT NULL REFERENCES carts.carts(id) ON DELETE CASCADE,
    variant_id  uuid NOT NULL REFERENCES catalog.product_variants(id),
    quantity    int  NOT NULL CHECK (quantity BETWEEN 1 AND 99),
    added_at    timestamptz NOT NULL,
    PRIMARY KEY (cart_id, variant_id)
);

-- ===================== shipping =====================
CREATE TABLE shipping.settings (                      -- linha única
    id                     smallint PRIMARY KEY CHECK (id = 1),
    origin_postal_code     char(8) NOT NULL,
    handling_days          smallint NOT NULL DEFAULT 1,
    fallback_pac_price     numeric(12,2) NOT NULL,
    fallback_sedex_price   numeric(12,2) NOT NULL,
    updated_at             timestamptz NOT NULL
);

CREATE TABLE shipping.rules (
    id               uuid PRIMARY KEY,
    name             text NOT NULL,
    priority         int  NOT NULL,
    is_active        boolean NOT NULL DEFAULT true,
    valid_from       timestamptz NULL,
    valid_to         timestamptz NULL,
    min_subtotal     numeric(12,2) NULL,
    states           char(2)[] NULL,
    postal_code_ranges int4range[] NULL,
    service_codes    text[] NULL,
    effect           text NOT NULL CHECK (effect IN ('FreeShipping','PercentDiscount','FixedDiscount','Surcharge','HideService')),
    effect_value     numeric(12,2) NULL,
    created_at       timestamptz NOT NULL,
    updated_at       timestamptz NOT NULL
);

-- ===================== ordering =====================
CREATE SEQUENCE ordering.order_number_seq;

CREATE TABLE ordering.orders (
    id                   uuid PRIMARY KEY,
    number               bigint NOT NULL UNIQUE DEFAULT nextval('ordering.order_number_seq'),
    customer_id          uuid NOT NULL REFERENCES customers.customers(id),
    status               text NOT NULL CHECK (status IN ('PendingPayment','PaymentConfirmed','Preparing','Shipped','Delivered','Cancelled')),
    subtotal             numeric(12,2) NOT NULL,
    shipping_fee         numeric(12,2) NOT NULL,
    total                numeric(12,2) NOT NULL,
    -- snapshot do frete
    shipping_service     text NOT NULL,              -- "PAC", "SEDEX"
    shipping_days        smallint NOT NULL,
    shipping_is_fallback boolean NOT NULL DEFAULT false,
    -- snapshot do endereço
    ship_recipient       text NOT NULL,
    ship_postal_code     char(8) NOT NULL,
    ship_street          text NOT NULL,
    ship_number          text NOT NULL,
    ship_complement      text NULL,
    ship_district        text NOT NULL,
    ship_city            text NOT NULL,
    ship_state           char(2) NOT NULL,
    tracking_code        text NULL,
    cancellation_reason  text NULL CHECK (cancellation_reason IN ('CustomerRequest','AdminRequest','PaymentTimeout','PaymentRejected')),
    notes                text NULL,
    idempotency_key      text NULL,
    payment_due_at       timestamptz NOT NULL,       -- placed_at + 48 h
    payment_reminder_sent_at timestamptz NULL,
    version              int NOT NULL DEFAULT 0,
    placed_at            timestamptz NOT NULL,
    updated_at           timestamptz NOT NULL,
    UNIQUE (customer_id, idempotency_key)
);
CREATE INDEX ix_orders_customer ON ordering.orders(customer_id, placed_at DESC);
CREATE INDEX ix_orders_status   ON ordering.orders(status, placed_at DESC);
CREATE INDEX ix_orders_payment_due ON ordering.orders(payment_due_at) WHERE status = 'PendingPayment';  -- job de 48 h

CREATE TABLE ordering.order_items (
    order_id      uuid NOT NULL REFERENCES ordering.orders(id),
    variant_id    uuid NOT NULL,
    sku           text NOT NULL,
    product_name  text NOT NULL,
    color_name    text NULL,
    size_name     text NULL,
    unit_price    numeric(12,2) NOT NULL,
    quantity      int NOT NULL CHECK (quantity > 0),
    PRIMARY KEY (order_id, variant_id)
);

CREATE TABLE ordering.order_status_history (
    id           uuid PRIMARY KEY,
    order_id     uuid NOT NULL REFERENCES ordering.orders(id),
    from_status  text NULL,
    to_status    text NOT NULL,
    changed_by   uuid NULL,                          -- NULL = sistema (webhook/job)
    changed_at   timestamptz NOT NULL,
    note         text NULL
);
CREATE INDEX ix_order_status_history_order ON ordering.order_status_history(order_id, changed_at);

-- ===================== payments =====================
CREATE TABLE payments.payments (
    id                   uuid PRIMARY KEY,
    order_id             uuid NOT NULL REFERENCES ordering.orders(id),
    provider             text NOT NULL,
    provider_payment_id  text NULL,
    method               text NOT NULL CHECK (method IN ('Pix','CreditCard','Boleto')),
    status               text NOT NULL CHECK (status IN ('Pending','Approved','Rejected','Expired','Cancelled','Refunded','RefundFailed')),
    amount               numeric(12,2) NOT NULL,
    installments         smallint NULL,
    pix_qr_code          text NULL,
    pix_copy_paste       text NULL,
    boleto_url           text NULL,
    expires_at           timestamptz NULL,
    approved_at          timestamptz NULL,
    version              int NOT NULL DEFAULT 0,
    created_at           timestamptz NOT NULL,
    updated_at           timestamptz NOT NULL,
    UNIQUE (provider, provider_payment_id)
);
CREATE INDEX ix_payments_order ON payments.payments(order_id);
CREATE UNIQUE INDEX ux_payments_one_approved ON payments.payments(order_id) WHERE status = 'Approved';  -- RN-102
CREATE INDEX ix_payments_pending ON payments.payments(created_at) WHERE status = 'Pending';

CREATE TABLE payments.payment_events (                  -- log idempotente de webhooks
    provider           text NOT NULL,
    provider_event_id  text NOT NULL,
    payment_id         uuid NULL REFERENCES payments.payments(id),
    payload            jsonb NOT NULL,
    received_at        timestamptz NOT NULL,
    processed_at       timestamptz NULL,
    PRIMARY KEY (provider, provider_event_id)
);

-- ===================== notifications / messaging =====================
CREATE TABLE notifications.email_log (
    id                  uuid PRIMARY KEY,
    template            text NOT NULL,          -- "EM-04"
    recipient_masked    text NOT NULL,
    status              text NOT NULL CHECK (status IN ('Sent','Failed')),
    provider_message_id text NULL,
    related_entity_id   uuid NULL,
    created_at          timestamptz NOT NULL
);

-- messaging.outbox_messages: ver arquivo 25
```

> Os enums são persistidos em **inglês** (valores técnicos estáveis); a tradução para pt-BR é feita na apresentação.

## 4. Migrations

- Ferramenta: **DbUp** (`dbup-postgresql`), scripts `.sql` como *embedded resources* em `Infrastructure/Persistence/Migrations`.
- Nomenclatura: `V0001__create_schemas.sql`, `V0002__identity.sql`, ...
- Scripts são **imutáveis** depois de mergeados; correções = novo script.
- Execução: projeto `ECommerce.Migrator` executado no pipeline **antes** do deploy. A API **não** migra automaticamente em produção.

## 5. Dados de seed

- Admin inicial (RF-060) — pela aplicação.
- Tamanhos padrão (PP, P, M, G, GG, XG; 34–46) e cores básicas — script de seed inicial, editáveis pelo admin.
- Configuração de frete inicial (`shipping.settings`) a partir de variáveis de ambiente.
- Catálogo de exemplo apenas em `Development`.

## 6. Backup e manutenção

- Backup diário + retenção de 7 dias; PITR em produção gerenciada.
- `pg_stat_statements` habilitado.
- Expurgo: `payment_events` e `outbox_messages` processados há mais de 90 dias.

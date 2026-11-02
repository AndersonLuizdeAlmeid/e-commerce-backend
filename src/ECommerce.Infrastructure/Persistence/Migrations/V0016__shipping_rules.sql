-- Fase 3.4 — regras de frete do admin (RF-058, arquivo 22 §3.3). A loja lança sem nenhuma (arquivo 20, D08).
-- Vigência em datas inclusivas, no horário de Brasília, como as ocasiões. Faixas de CEP como texto: '01000000-19999999'.
CREATE TABLE shipping.rules (
    id                  uuid PRIMARY KEY,
    name                text NOT NULL,
    priority            int  NOT NULL,
    is_active           boolean NOT NULL DEFAULT true,
    valid_from          date NULL,
    valid_to            date NULL,
    min_subtotal        numeric(12,2) NULL CHECK (min_subtotal >= 0),
    states              text[] NOT NULL DEFAULT '{}',
    postal_code_ranges  text[] NOT NULL DEFAULT '{}',
    service_codes       text[] NOT NULL DEFAULT '{}',
    effect              text NOT NULL CHECK (effect IN ('FreeShipping','PercentDiscount','FixedDiscount','Surcharge','HideService')),
    effect_value        numeric(12,2) NULL,
    created_at          timestamptz NOT NULL,
    updated_at          timestamptz NOT NULL,
    CONSTRAINT ck_rules_period CHECK (valid_to IS NULL OR valid_from IS NULL OR valid_to >= valid_from)
);
CREATE INDEX ix_rules_active ON shipping.rules(priority) WHERE is_active;

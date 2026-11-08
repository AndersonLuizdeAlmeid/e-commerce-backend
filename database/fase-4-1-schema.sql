-- Fase 4.1 — endereços do cliente
-- GERADO por database/gerar-scripts.sh a partir de src/ECommerce.Infrastructure/Persistence (não edite aqui).
-- Executar no DBeaver como script (Alt+X) conectado ao banco 'ecommerce'. Tudo roda em uma transação:
-- se algo falhar, nada é aplicado. Cada script é registrado em public.schemaversions, então o
-- ECommerce.Migrator e o /health/ready o enxergam como já aplicado.

BEGIN;

CREATE TABLE IF NOT EXISTS public.schemaversions (
    schemaversionsid serial NOT NULL,
    scriptname character varying(255) NOT NULL,
    applied timestamp without time zone NOT NULL,
    CONSTRAINT pk_schemaversions_id PRIMARY KEY (schemaversionsid)
);

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM public.schemaversions WHERE scriptname = 'ECommerce.Infrastructure.Persistence.Migrations.V0016__shipping_rules.sql') THEN
        RAISE EXCEPTION 'Pré-requisito ausente: %. Aplique o script da fase anterior antes.', 'ECommerce.Infrastructure.Persistence.Migrations.V0016__shipping_rules.sql';
    END IF;
    IF EXISTS (SELECT 1 FROM public.schemaversions WHERE scriptname = 'ECommerce.Infrastructure.Persistence.Migrations.V0017__customer_addresses.sql') THEN
        RAISE EXCEPTION 'Script já aplicado neste banco: %', 'V0017__customer_addresses.sql';
    END IF;
END
$$;

-- ======================================================================
-- V0017__customer_addresses.sql
-- ======================================================================
-- Fase 4.1 — caderno de endereços do cliente (RF-007). O pedido copia o endereço na compra, então nenhum pedido
-- aponta para esta tabela e o cliente pode apagar qualquer endereço.
CREATE TABLE customers.addresses (
    id            uuid PRIMARY KEY,
    customer_id   uuid NOT NULL REFERENCES customers.customers(id),
    recipient     text NOT NULL,
    postal_code   char(8) NOT NULL CHECK (postal_code ~ '^[0-9]{8}$'),
    street        text NOT NULL,
    number        text NOT NULL,
    complement    text NULL,
    district      text NOT NULL,
    city          text NOT NULL,
    state         char(2) NOT NULL,
    is_default    boolean NOT NULL DEFAULT false,
    created_at    timestamptz NOT NULL,
    updated_at    timestamptz NOT NULL
);
CREATE INDEX ix_addresses_customer ON customers.addresses(customer_id);
-- Um padrão por cliente; o domínio garante, o índice é a última barreira.
CREATE UNIQUE INDEX ux_addresses_default ON customers.addresses(customer_id) WHERE is_default;

INSERT INTO public.schemaversions (scriptname, applied) VALUES ('ECommerce.Infrastructure.Persistence.Migrations.V0017__customer_addresses.sql', now() AT TIME ZONE 'utc');

COMMIT;

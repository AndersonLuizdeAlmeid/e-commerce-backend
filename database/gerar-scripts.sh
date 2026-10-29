#!/usr/bin/env bash
# Gera os scripts consolidados de database/ a partir das migrations e seeds do DbUp.
# Uso (Git Bash, na raiz do repositório): bash database/gerar-scripts.sh
set -euo pipefail
cd "$(dirname "$0")/.."

M=src/ECommerce.Infrastructure/Persistence/Migrations
S=src/ECommerce.Infrastructure/Persistence/Seeds/Development
P=ECommerce.Infrastructure.Persistence

# gen <saída> <journal> <prefixo do recurso> <descrição> <pré-requisito em schemaversions, ou ""> <arquivos...>
gen() {
  local out=$1 journal=$2 prefix=$3 desc=$4 req=$5
  shift 5
  {
    echo "-- $desc"
    echo "-- GERADO por database/gerar-scripts.sh a partir de src/ECommerce.Infrastructure/Persistence (não edite aqui)."
    echo "-- Executar no DBeaver como script (Alt+X) conectado ao banco 'ecommerce'. Tudo roda em uma transação:"
    echo "-- se algo falhar, nada é aplicado. Cada script é registrado em public.$journal, então o"
    echo "-- ECommerce.Migrator e o /health/ready o enxergam como já aplicado."
    echo
    echo "BEGIN;"
    echo
    echo "CREATE TABLE IF NOT EXISTS public.$journal ("
    echo "    schemaversionsid serial NOT NULL,"
    echo "    scriptname character varying(255) NOT NULL,"
    echo "    applied timestamp without time zone NOT NULL,"
    echo "    CONSTRAINT pk_${journal}_id PRIMARY KEY (schemaversionsid)"
    echo ");"
    echo
    echo "DO \$\$"
    echo "BEGIN"
    if [ -n "$req" ]; then
      echo "    IF NOT EXISTS (SELECT 1 FROM public.schemaversions WHERE scriptname = '$req') THEN"
      echo "        RAISE EXCEPTION 'Pré-requisito ausente: %. Aplique o script da fase anterior antes.', '$req';"
      echo "    END IF;"
    fi
    for f in "$@"; do
      echo "    IF EXISTS (SELECT 1 FROM public.$journal WHERE scriptname = '$prefix.$(basename "$f")') THEN"
      echo "        RAISE EXCEPTION 'Script já aplicado neste banco: %', '$(basename "$f")';"
      echo "    END IF;"
    done
    echo "END"
    echo "\$\$;"
    for f in "$@"; do
      echo
      echo "-- ======================================================================"
      echo "-- $(basename "$f")"
      echo "-- ======================================================================"
      cat "$f"
      echo
      echo "INSERT INTO public.$journal (scriptname, applied) VALUES ('$prefix.$(basename "$f")', now() AT TIME ZONE 'utc');"
    done
    echo
    echo "COMMIT;"
  } > "$out"
  echo "gerado: $out"
}

gen database/fase-0-schema.sql schemaversions "$P.Migrations" \
  "Fase 0 — extensões, schema catalog e categorias" "" \
  $M/V0001__extensions_and_schemas.sql $M/V0002__catalog_categories.sql

gen database/fase-0-dados-exemplo.sql seedversions "$P.Seeds.Development" \
  "Fase 0 — categorias de exemplo (SOMENTE Development)" "$P.Migrations.V0002__catalog_categories.sql" \
  $S/S0001__example_categories.sql

gen database/fase-1-schema.sql schemaversions "$P.Migrations" \
  "Fase 1 — vitrine: cores, tamanhos, produtos, variações, imagens, slugs, saldo de estoque" "$P.Migrations.V0002__catalog_categories.sql" \
  $M/V0003__catalog_products_and_variants.sql $M/V0004__inventory_stock_items.sql $M/V0005__seed_colors_and_sizes.sql

gen database/fase-1-dados-exemplo.sql seedversions "$P.Seeds.Development" \
  "Fase 1 — produtos de exemplo com grade, estoque e imagens (SOMENTE Development; requer fase-0-dados-exemplo)" "$P.Migrations.V0005__seed_colors_and_sizes.sql" \
  $S/S0002__example_products.sql

gen database/fase-2a-schema.sql schemaversions "$P.Migrations" \
  "Fase 2A — contas: identity (usuários, sessões, tokens), customers, outbox e log de e-mails" "$P.Migrations.V0005__seed_colors_and_sizes.sql" \
  $M/V0006__identity.sql $M/V0007__customers.sql $M/V0008__messaging_outbox_and_email_log.sql

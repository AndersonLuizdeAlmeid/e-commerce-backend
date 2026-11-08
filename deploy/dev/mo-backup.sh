#!/usr/bin/env bash
# Backup diário do banco do ambiente de dev (entrega infra-1, I15). Roda pelo cron como root; guarda 7 dias.
# Restaurar: mo-restore (lista) e mo-restore ARQUIVO. Mostra o caminho do arquivo criado.
set -Eeuo pipefail
umask 077

DIR=/opt/mo/backups
mkdir -p "$DIR"
file="$DIR/ecommerce-$(date +%Y%m%d-%H%M).dump"

docker exec mo-dev-postgres-1 pg_dump -U ecommerce -d ecommerce -Fc > "$file.tmp"
mv "$file.tmp" "$file"
find "$DIR" -name 'ecommerce-*.dump' -mtime +7 -delete
echo "$file"

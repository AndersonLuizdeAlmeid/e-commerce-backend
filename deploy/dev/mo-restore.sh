#!/usr/bin/env bash
# Restaura um backup do banco do ambiente de dev (deploy/dev/README.md). Como root:
#   mo-restore                                  lista os backups
#   mo-restore ecommerce-20261108-0330.dump     restaura esse arquivo
# Antes, guarda um backup do estado atual. Para a API e a loja, restaura numa transação só (se falhar, o banco fica
# como estava), roda as migrations que o backup não tiver e sobe tudo de novo, com a loja sem cache de páginas.
# Usa a trava do mo-deploy: nunca roda junto com um deploy. Só o banco: as fotos (RustFS) não entram no backup.
set -Eeuo pipefail

# shellcheck source=/dev/null
source /etc/mo-deploy.conf
: "${MO_HOME:?}"
[[ $EUID -eq 0 ]] || { echo "Rode como root: sudo mo-restore $*" >&2; exit 1; }

DIR="$MO_HOME/backups"
if [[ $# -eq 0 ]]; then
  ls -lh "$DIR"
  exit 0
fi
[[ $1 =~ ^ecommerce-[0-9]{8}-[0-9]{4}\.dump$ ]] || { echo "Use o nome de um arquivo listado por: mo-restore" >&2; exit 1; }
file="$DIR/$1"
[[ -f $file ]] || { echo "Não existe: $file" >&2; exit 1; }

exec 9> "$MO_HOME/state/deploy.lock"
flock -w 600 9 || { echo "Há um deploy em andamento há mais de 10 minutos; tente depois." >&2; exit 1; }

log() { printf '%s %s\n' "$(date -u +%FT%TZ)" "$*" | tee -a "$MO_HOME/state/history.log"; }

# O backup de segurança não pode ter o mesmo nome do arquivo que vai ser restaurado.
while [[ $DIR/ecommerce-$(date +%Y%m%d-%H%M).dump == "$file" ]]; do sleep 5; done
safety=$(/usr/local/bin/mo-backup)
log "restore: estado atual guardado em $(basename "$safety")"

mo-compose stop api web
# Se algo falhar daqui em diante, a API e a loja sobem de novo do jeito que estiverem.
trap 'mo-compose up -d api web' ERR
docker exec -i mo-dev-postgres-1 pg_restore -U ecommerce -d ecommerce --clean --if-exists --single-transaction < "$file"
log "restore: $1 restaurado"

mo-compose up -d api
for _ in $(seq 60); do
  curl -fs -o /dev/null http://127.0.0.1:3101/health/ready && break
  sleep 2
done
if ! curl -fs -o /dev/null http://127.0.0.1:3101/health/ready; then
  mo-compose up -d --no-deps web
  log "restore: a API não ficou saudável; para voltar: mo-restore $(basename "$safety")"
  exit 1
fi
# Recriar o container descarta as páginas guardadas pelo ISR, que ainda mostrariam os dados de antes.
mo-compose up -d --force-recreate --no-deps web
trap - ERR
log "restore: API e loja no ar"

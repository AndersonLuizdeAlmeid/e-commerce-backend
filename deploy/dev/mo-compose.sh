#!/usr/bin/env bash
# Docker Compose do ambiente de dev com os segredos e as versões no ar, para o root operar a VM (deploy/dev/README.md):
#   mo-compose ps
#   mo-compose logs -f api
#   mo-compose run --rm migrator
# Trocar a versão no ar continua sendo com push na main (mo-deploy): este atalho não grava o images.env.
set -Eeuo pipefail

# shellcheck source=/dev/null
source /etc/mo-deploy.conf
: "${MO_HOME:?}"
[[ $EUID -eq 0 ]] || { echo "Rode como root: sudo mo-compose $*" >&2; exit 1; }

dir="$MO_HOME/src/deploy/dev"
exec docker compose --project-directory "$dir" -f "$dir/compose.yml" \
  --env-file "$MO_HOME/.env" --env-file "$MO_HOME/state/images.env" "$@"

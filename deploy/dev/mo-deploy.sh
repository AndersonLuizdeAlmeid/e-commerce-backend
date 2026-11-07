#!/usr/bin/env bash
# Comando forçado da chave de deploy (specs/entregas/infra-1-ambiente-dev.md, I5 e I13). O sshd roda este script no
# lugar de qualquer comando pedido, que chega em SSH_ORIGINAL_COMMAND. Só três pedidos são aceitos:
#   backend <commit> <digest-api> <digest-migrator>
#   web <digest-web>
#   status
# Cada argumento é validado por expressão regular e nunca passa por eval nem por um shell. Instalado pelo
# bootstrap.sh em /usr/local/bin, como root: o usuário deploy não consegue alterá-lo.
set -Eeuo pipefail
umask 027

# Endereços e nomes vêm do arquivo de configuração do root, nunca de quem chama.
CONFIG=${MO_DEPLOY_CONFIG:-/etc/mo-deploy.conf}
# shellcheck source=/dev/null
source "$CONFIG"
: "${MO_HOME:?}" "${REPO_URL:?}" "${BRANCH:?}" "${IMAGE_PREFIX:?}"

SRC="$MO_HOME/src"
STATE="$MO_HOME/state"
IMAGES="$STATE/images.env"
COMPOSE_DIR="$SRC/deploy/dev"
HEALTH_TIMEOUT=120
WEB_PENDING="$IMAGE_PREFIX/ecommerce-web:aguardando-deploy"

COMMIT_RE='^[0-9a-f]{40}$'
DIGEST_RE='^sha256:[0-9a-f]{64}$'

log() { printf '%s %s\n' "$(date -u +%FT%TZ)" "$*" | tee -a "$STATE/history.log" >&2; }
fail() { log "ERRO: $*"; exit 1; }

compose() {
  docker compose --project-directory "$COMPOSE_DIR" -f "$COMPOSE_DIR/compose.yml" \
    --env-file "$MO_HOME/.env" --env-file "$IMAGES" "$@"
}

# Lê uma chave do images.env sem executar o arquivo.
image_of() {
  [[ -f $IMAGES ]] || return 0
  sed -n "s/^$1=//p" "$IMAGES" | tail -1
}

write_images() {
  local tmp
  tmp=$(mktemp "$STATE/images.XXXXXX")
  printf 'API_IMAGE=%s\nMIGRATOR_IMAGE=%s\nWEB_IMAGE=%s\n' "$1" "$2" "$3" > "$tmp"
  mv "$tmp" "$IMAGES"
}

# Só commits que já estão na branch configurada: um SHA de PR ou de fork não sobe, mesmo com a chave.
checkout() {
  git -C "$SRC" fetch --quiet origin "+refs/heads/$BRANCH:refs/remotes/origin/$BRANCH"
  git -C "$SRC" merge-base --is-ancestor "$1" "origin/$BRANCH" 2>/dev/null \
    || fail "commit $1 não está em $BRANCH"
  git -C "$SRC" checkout --quiet --detach "$1"
}

wait_api() {
  local deadline=$((SECONDS + HEALTH_TIMEOUT))
  until curl -fs -o /dev/null http://127.0.0.1:3101/health/ready; do
    ((SECONDS < deadline)) || return 1
    sleep 3
  done
}

wait_web() {
  local deadline=$((SECONDS + HEALTH_TIMEOUT)) status
  while true; do
    status=$(docker inspect --format '{{.State.Health.Status}}' "$(compose ps -q web)" 2>/dev/null || true)
    [[ $status == healthy ]] && return 0
    ((SECONDS < deadline)) || return 1
    sleep 3
  done
}

# Sobe tudo menos a loja enquanto ela não tiver sido publicada uma vez.
services() {
  if [[ $(image_of WEB_IMAGE) == *@sha256:* ]]; then
    echo "postgres mailpit rustfs rustfs-init migrator api web"
  else
    echo "postgres mailpit rustfs rustfs-init migrator api"
  fi
}

deploy_backend() {
  local commit=$1 api="$IMAGE_PREFIX/ecommerce-api@$2" migrator="$IMAGE_PREFIX/ecommerce-migrator@$3"
  local prev_commit prev_api prev_migrator web
  prev_commit=$(git -C "$SRC" rev-parse HEAD)
  prev_api=$(image_of API_IMAGE)
  prev_migrator=$(image_of MIGRATOR_IMAGE)
  web=$(image_of WEB_IMAGE)
  web=${web:-$WEB_PENDING}

  log "backend $commit: início (antes: ${prev_api:-nenhum})"
  checkout "$commit"
  write_images "$api" "$migrator" "$web"

  # shellcheck disable=SC2046 # lista de serviços sem espaços nos nomes
  if compose pull --quiet api migrator && compose up -d --remove-orphans $(services) && wait_api; then
    log "backend $commit: no ar"
    return 0
  fi

  log "backend $commit: falhou; voltando para a versão anterior"
  compose logs --no-color --tail 50 migrator api >&2 || true
  if [[ -n $prev_api ]]; then
    git -C "$SRC" checkout --quiet --detach "$prev_commit"
    write_images "$prev_api" "$prev_migrator" "$web"
    # shellcheck disable=SC2046
    compose up -d --remove-orphans $(services) && wait_api && log "backend: versão anterior de volta"
  fi
  exit 1
}

deploy_web() {
  local web="$IMAGE_PREFIX/ecommerce-web@$1" api migrator prev_web
  api=$(image_of API_IMAGE)
  migrator=$(image_of MIGRATOR_IMAGE)
  prev_web=$(image_of WEB_IMAGE)
  [[ -n $api ]] || fail "o backend ainda não foi publicado nesta VM; a loja sobe depois dele"

  log "web $1: início (antes: ${prev_web:-nenhuma})"
  write_images "$api" "$migrator" "$web"
  if compose pull --quiet web && compose up -d --no-deps web && wait_web; then
    log "web $1: no ar"
    return 0
  fi

  log "web $1: falhou; voltando para a versão anterior"
  compose logs --no-color --tail 50 web >&2 || true
  if [[ $prev_web == *@sha256:* ]]; then
    write_images "$api" "$migrator" "$prev_web"
    compose up -d --no-deps web && wait_web && log "web: versão anterior de volta"
  fi
  exit 1
}

status() {
  echo "commit: $(git -C "$SRC" rev-parse HEAD)"
  cat "$IMAGES" 2>/dev/null || echo "nenhuma versão publicada"
  compose ps --format 'table {{.Service}}\t{{.Status}}' 2>/dev/null || true
}

read -r -a args <<< "${SSH_ORIGINAL_COMMAND:-}"
[[ ${#args[@]} -gt 0 ]] || fail "pedido vazio"

# Um deploy por vez, mesmo que o GitHub mande dois.
exec 9> "$STATE/deploy.lock"
flock -w 600 9 || fail "outro deploy em andamento há mais de 10 minutos"

case "${args[0]}" in
  backend)
    [[ ${#args[@]} -eq 4 && ${args[1]} =~ $COMMIT_RE && ${args[2]} =~ $DIGEST_RE && ${args[3]} =~ $DIGEST_RE ]] \
      || fail "uso: backend <commit> <digest-api> <digest-migrator>"
    deploy_backend "${args[1]}" "${args[2]}" "${args[3]}"
    ;;
  web)
    [[ ${#args[@]} -eq 2 && ${args[1]} =~ $DIGEST_RE ]] || fail "uso: web <digest>"
    deploy_web "${args[1]}"
    ;;
  status)
    [[ ${#args[@]} -eq 1 ]] || fail "uso: status"
    status
    ;;
  *)
    # Só letras do pedido vão para o log, para quem chama não escrever o que quiser nele.
    fail "pedido recusado: ${args[0]//[^a-z]/?}"
    ;;
esac

#!/usr/bin/env bash
# Prepara a VM do ambiente de dev (specs/entregas/infra-1-ambiente-dev.md, fatia I.2). Roda como root e pode rodar de
# novo sem estragar nada: cada passo confere o que já existe. A VM é compartilhada, então o script não reinstala nem
# reinicia o Docker, não mexe em containers de fora do projeto mo-dev e não fecha o SSH público.
#
#   sudo bash bootstrap.sh                   # primeira vez ou para aplicar mudanças deste script
#   sudo bash bootstrap.sh --new-deploy-key  # troca a chave de deploy (a antiga deixa de valer)
#   sudo UFW_ALLOW="80/tcp 443/tcp" bash bootstrap.sh  # portas de programas fora do Docker que seguem públicas
set -Eeuo pipefail

REPO_URL=${REPO_URL:-https://github.com/AndersonLuizdeAlmeid/e-commerce-backend.git}
BRANCH=${BRANCH:-main}
IMAGE_PREFIX=${IMAGE_PREFIX:-ghcr.io/andersonluizdealmeid}
TS_HOSTNAME=${TS_HOSTNAME:-mo-dev}
# Portas que continuam abertas no firewall além da 22, para programas que rodam fora do Docker (ex.: um nginx).
UFW_ALLOW=${UFW_ALLOW:-}
# Só para ensaio fora da VM: não entra no Tailscale e usa um endereço fictício.
SKIP_TAILSCALE_UP=${SKIP_TAILSCALE_UP:-0}
# Faixas de endereço da rede privada do Tailscale (IPv4 e IPv6).
TAILNET_RANGES="100.64.0.0/10,fd7a:115c:a1e0::/48"

MO_HOME=/opt/mo
NEW_DEPLOY_KEY=0
[[ ${1:-} == --new-deploy-key ]] && NEW_DEPLOY_KEY=1

step() { printf '\n\033[1m== %s\033[0m\n' "$*"; }
warn() { printf '\033[33mATENÇÃO:\033[0m %s\n' "$*"; }
die() { printf '\033[31mERRO:\033[0m %s\n' "$*" >&2; exit 1; }

[[ $EUID -eq 0 ]] || die "rode como root (sudo bash bootstrap.sh)"
# shellcheck source=/dev/null
source /etc/os-release
[[ $ID == ubuntu ]] || die "feito para Ubuntu; encontrado $PRETTY_NAME"
for rule in $UFW_ALLOW; do
  [[ $rule =~ ^[0-9]{1,5}/(tcp|udp)$ ]] || die "UFW_ALLOW aceita só porta/protocolo, como 80/tcp; recebido: $rule"
done

step "Conferindo o Docker (não é reinstalado nem reiniciado)"
command -v docker > /dev/null || die "Docker não encontrado"
docker compose version > /dev/null 2>&1 || die "falta o plugin docker compose (apt install docker-compose-plugin)"
docker version --format 'Docker {{.Server.Version}}'
docker compose version

no_restart=$(docker ps --format '{{.Names}}' | while read -r name; do
  policy=$(docker inspect --format '{{.HostConfig.RestartPolicy.Name}}' "$name")
  [[ $policy == no || -z $policy ]] && echo "$name"
done || true)
if [[ -n $no_restart ]]; then
  warn "estes containers NÃO voltam sozinhos depois de reiniciar a VM: $(echo "$no_restart" | tr '\n' ' ')"
fi

step "Pacotes e atualizações automáticas de segurança"
export DEBIAN_FRONTEND=noninteractive
apt-get update -qq
apt-get install -y -qq unattended-upgrades fail2ban ufw git curl jq openssh-server > /dev/null
cat > /etc/apt/apt.conf.d/20auto-upgrades <<'EOF'
APT::Periodic::Update-Package-Lists "1";
APT::Periodic::Unattended-Upgrade "1";
EOF
# Só as atualizações de segurança do Ubuntu (padrão do pacote) e sem reiniciar sozinho: a VM é compartilhada.
cat > /etc/apt/apt.conf.d/52mo-unattended <<'EOF'
Unattended-Upgrade::Automatic-Reboot "false";
EOF

step "Swap"
if [[ -z $(swapon --show) ]]; then
  fallocate -l 2G /swapfile && chmod 600 /swapfile && mkswap /swapfile > /dev/null && swapon /swapfile
  grep -q '^/swapfile ' /etc/fstab || echo '/swapfile none swap sw 0 0' >> /etc/fstab
  echo "swap de 2 GB criado"
else
  echo "swap já existe"
fi

step "Usuário deploy (sem senha, sem sudo, só o comando de deploy)"
id deploy > /dev/null 2>&1 || useradd --create-home --shell /bin/bash deploy
passwd -l deploy > /dev/null
usermod -aG docker deploy
# A chave fica num arquivo do root: o próprio deploy não consegue acrescentar outra.
install -d -o root -g root -m 755 /home/deploy/.ssh /etc/ssh/authorized_keys
rm -f /home/deploy/.ssh/authorized_keys /home/deploy/.ssh/authorized_keys2

step "Pastas, clone do repositório e scripts"
install -d -o root -g deploy -m 750 "$MO_HOME"
install -d -o deploy -g deploy -m 750 "$MO_HOME/state"
install -d -o root -g root -m 700 "$MO_HOME/backups"
if [[ ! -d $MO_HOME/src/.git ]]; then
  install -d -o deploy -g deploy -m 750 "$MO_HOME/src"
  sudo -u deploy git clone --quiet --branch "$BRANCH" "$REPO_URL" "$MO_HOME/src"
fi
chown -R deploy:deploy "$MO_HOME/src"
sudo -u deploy git -C "$MO_HOME/src" fetch --quiet origin "+refs/heads/$BRANCH:refs/remotes/origin/$BRANCH"
# Os scripts instalados vêm da branch publicada e pertencem ao root.
for script in mo-deploy mo-backup; do
  # shellcheck disable=SC2024 # o redirecionamento é do root de propósito: o arquivo novo é dele
  sudo -u deploy git -C "$MO_HOME/src" show "origin/$BRANCH:deploy/dev/$script.sh" > "/usr/local/bin/$script.new"
  install -o root -g root -m 755 "/usr/local/bin/$script.new" "/usr/local/bin/$script"
  rm -f "/usr/local/bin/$script.new"
done
cat > /etc/mo-deploy.conf <<EOF
MO_HOME=$MO_HOME
REPO_URL=$REPO_URL
BRANCH=$BRANCH
IMAGE_PREFIX=$IMAGE_PREFIX
EOF
chmod 644 /etc/mo-deploy.conf
echo "30 3 * * * root /usr/local/bin/mo-backup" > /etc/cron.d/mo-backup

step "Tailscale"
if ! command -v tailscale > /dev/null; then
  curl -fsSL "https://pkgs.tailscale.com/stable/ubuntu/$VERSION_CODENAME.noarmor.gpg" -o /usr/share/keyrings/tailscale-archive-keyring.gpg
  curl -fsSL "https://pkgs.tailscale.com/stable/ubuntu/$VERSION_CODENAME.tailscale-keyring.list" -o /etc/apt/sources.list.d/tailscale.list
  apt-get update -qq && apt-get install -y -qq tailscale > /dev/null
fi
tailscale version | head -1

if [[ $SKIP_TAILSCALE_UP == 1 ]]; then
  ts_name="$TS_HOSTNAME.exemplo.ts.net"
  warn "ensaio: Tailscale instalado, mas fora da rede"
else
  if [[ $(tailscale status --json 2> /dev/null | jq -r .BackendState) != Running ]]; then
    echo "Abra o link abaixo e aprove a VM na sua rede do Tailscale:"
    tailscale up --hostname="$TS_HOSTNAME" --advertise-tags=tag:mo-dev
  fi
  ts_name=$(tailscale status --json | jq -r '.Self.DNSName | rtrimstr(".")')
  # HTTPS com certificado do próprio Tailscale; só quem está na rede privada alcança.
  tailscale serve --bg --https=443 http://127.0.0.1:3100 > /dev/null
  tailscale serve --bg --https=8443 http://127.0.0.1:3101 > /dev/null
  tailscale serve --bg --https=8025 http://127.0.0.1:3103 > /dev/null
  tailscale serve status
fi
STORE_URL="https://$ts_name"
API_URL="https://$ts_name:8443"

step "Segredos da aplicação (gerados aqui, nunca saem da VM)"
if [[ ! -f $MO_HOME/.env ]]; then
  rand() { openssl rand -hex "$1"; }
  cat > "$MO_HOME/.env" <<EOF
POSTGRES_PASSWORD=$(rand 24)
JWT_SIGNING_KEY=$(rand 48)
INTERNAL_KEY=$(rand 24)
REVALIDATE_SECRET=$(rand 24)
RUSTFS_ACCESS_KEY=mo-$(rand 6)
RUSTFS_SECRET_KEY=$(rand 24)
ADMIN_EMAIL=admin@mo.local
ADMIN_PASSWORD=Mo-$(rand 10)!
STORE_URL=$STORE_URL
EOF
  echo "criados em $MO_HOME/.env"
else
  sed -i "s#^STORE_URL=.*#STORE_URL=$STORE_URL#" "$MO_HOME/.env"
  echo "já existiam; mantidos"
fi
chown root:deploy "$MO_HOME/.env"
chmod 640 "$MO_HOME/.env"

step "SSH (a porta 22 continua aberta para os outros deploys)"
password_logins=$( (journalctl -u ssh --since "-30 days" --no-pager 2> /dev/null; zcat -f /var/log/auth.log* 2> /dev/null) \
  | grep -c 'Accepted password' || true)
{
  echo "# Gerado pelo bootstrap.sh do ambiente mo-dev"
  echo "AuthorizedKeysFile .ssh/authorized_keys .ssh/authorized_keys2 /etc/ssh/authorized_keys/%u"
  echo "MaxAuthTries 4"
  echo "LoginGraceTime 30"
  echo "X11Forwarding no"
  if [[ $password_logins -eq 0 ]]; then
    echo "PasswordAuthentication no"
    echo "KbdInteractiveAuthentication no"
    echo "PermitRootLogin prohibit-password"
  fi
} > /etc/ssh/sshd_config.d/10-mo.conf.new
mv /etc/ssh/sshd_config.d/10-mo.conf.new /etc/ssh/sshd_config.d/10-mo.conf
if sshd -t; then
  systemctl reload ssh 2> /dev/null || systemctl reload sshd
else
  rm -f /etc/ssh/sshd_config.d/10-mo.conf
  die "configuração do SSH inválida; nada foi aplicado"
fi
if [[ $password_logins -gt 0 ]]; then
  warn "$password_logins login(s) por SENHA nos últimos 30 dias: a senha continua ligada. Troque esses acessos por chave e rode o script de novo."
else
  echo "login por senha desligado (nenhum nos últimos 30 dias); root só entra com chave"
fi

cat > /etc/fail2ban/jail.d/mo.local <<'EOF'
[sshd]
enabled = true
backend = systemd
maxretry = 5
findtime = 10m
bantime = 1h
ignoreip = 127.0.0.1/8 ::1 100.64.0.0/10
EOF
systemctl enable --now fail2ban > /dev/null 2>&1
systemctl restart fail2ban

step "Firewall (ufw)"
# Programas escutando direto no host ficariam bloqueados, a menos que a porta esteja em UFW_ALLOW; os do Docker não
# passam pelo ufw.
allowed_ports=" 22 $(for rule in $UFW_ALLOW; do printf '%s ' "${rule%%/*}"; done)"
exposed=$(ss -H -tlnp | awk '$4 !~ /^(127\.|\[::1\]|\[?::ffff:127\.)/' \
  | grep -v -E 'users:\(\("(sshd|docker-proxy|tailscaled|systemd-resolve)"' \
  | while read -r line; do
      port=$(awk '{print $4}' <<< "$line" | sed 's/.*://')
      [[ $allowed_ports == *" $port "* ]] || echo "$line"
    done || true)
if [[ -n $exposed && $(ufw status | head -1) != "Status: active" ]]; then
  warn "ufw NÃO foi ligado: há programas fora do Docker escutando na rede e eles seriam bloqueados:"
  echo "$exposed"
  echo "Se essas portas devem continuar públicas, rode de novo com UFW_ALLOW, por exemplo: UFW_ALLOW=\"80/tcp 443/tcp\""
else
  ufw default deny incoming > /dev/null
  ufw default allow outgoing > /dev/null
  ufw allow 22/tcp comment 'SSH (deploys de outras aplicações)' > /dev/null
  for rule in $UFW_ALLOW; do
    ufw allow "$rule" comment 'liberada por UFW_ALLOW' > /dev/null
  done
  ufw allow in on tailscale0 comment 'rede privada' > /dev/null
  ufw --force enable > /dev/null
  ufw status verbose | head -12
fi

step "Chave de deploy"
keyfile=/etc/ssh/authorized_keys/deploy
private_key=""
if [[ ! -s $keyfile || $NEW_DEPLOY_KEY == 1 ]]; then
  tmp=$(mktemp -d)
  ssh-keygen -q -t ed25519 -N "" -C "github-actions-mo-dev" -f "$tmp/key"
  # Só vale vindo da rede privada: pela porta 22 pública, a chave é recusada mesmo se vazar.
  echo "restrict,from=\"$TAILNET_RANGES\",command=\"/usr/local/bin/mo-deploy\" $(cat "$tmp/key.pub")" > "$keyfile"
  chmod 644 "$keyfile"
  private_key=$(cat "$tmp/key")
  shred -u "$tmp/key" && rm -rf "$tmp"
else
  echo "já existe (para trocar: --new-deploy-key)"
  grep -q 'from="' "$keyfile" || warn "a chave atual aceita conexão de qualquer lugar; troque com --new-deploy-key"
fi

step "Pronto"
cat <<EOF
Loja:    $STORE_URL
API:     $API_URL
Mailpit: https://$ts_name:8025
Admin:   veja ADMIN_EMAIL e ADMIN_PASSWORD com: sudo grep ADMIN_ $MO_HOME/.env

No GitHub, ambiente "dev" do e-commerce-frontend, variáveis:
  STORE_URL = $STORE_URL
  API_URL   = $API_URL

Segredo DEPLOY_KNOWN_HOSTS (nos dois repositórios):
$ts_name $(cut -d' ' -f1,2 /etc/ssh/ssh_host_ed25519_key.pub)
EOF
if [[ -n $private_key ]]; then
  cat <<EOF

Segredo DEPLOY_SSH_KEY (nos dois repositórios). Aparece só agora e não fica guardado na VM:
$private_key
EOF
fi

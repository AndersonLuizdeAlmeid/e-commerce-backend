# Ambiente de desenvolvimento na VM

Loja, API e banco numa VM compartilhada, acessíveis só pela rede privada do Tailscale. Plano, decisões e validação em
[specs/entregas/infra-1-ambiente-dev.md](../../specs/entregas/infra-1-ambiente-dev.md).

| Arquivo | Para quê |
|---------|----------|
| `bootstrap.sh` | Prepara a VM. Roda como root e pode rodar de novo |
| `compose.yml` | Os serviços do ambiente (projeto `mo-dev`) |
| `mo-deploy.sh` | Único comando que a chave de deploy executa (`/usr/local/bin/mo-deploy`) |
| `mo-backup.sh` | Backup diário do banco às 03:30, 7 dias (`/usr/local/bin/mo-backup`) |
| `tailscale-policy.hujson` | Política da rede privada |

## Preparar a VM (uma vez)

1. **Snapshot** da VM no painel da Contabo.
2. **Tailscale:** criar a conta (plano pessoal), instalar no seu PC e entrar com a mesma conta.
3. No painel do Tailscale:
   - **Access controls:** colar `tailscale-policy.hujson` no lugar da política padrão e salvar.
   - **DNS:** confirmar que o **MagicDNS** está ligado e ligar **HTTPS Certificates**.
4. Na VM, como root:

   ```bash
   curl -fsSL https://raw.githubusercontent.com/AndersonLuizdeAlmeid/e-commerce-backend/main/deploy/dev/bootstrap.sh -o /root/bootstrap.sh
   bash /root/bootstrap.sh
   ```

   O script para no passo do Tailscale e mostra um link: abra no navegador e aprove a VM. No fim ele mostra os
   endereços, as variáveis e os segredos para o GitHub. **A chave de deploy aparece só nessa hora.**
5. Trocar a **senha do VNC** da Contabo por uma forte e guardá-la: é o acesso de emergência.

## No dia a dia

| Tarefa | Como |
|--------|------|
| Abrir a loja | `https://mo-dev.<sua-rede>.ts.net` com o Tailscale ligado no aparelho |
| Login do admin | `sudo grep ADMIN_ /opt/mo/.env` na VM |
| E-mails | Mailpit em `https://mo-dev.<sua-rede>.ts.net:8025` |
| Versão no ar | `sudo cat /opt/mo/state/images.env` ou o histórico em `/opt/mo/state/history.log` |
| Logs | `cd /opt/mo/src/deploy/dev && sudo docker compose -p mo-dev logs -f api` |
| Banco pelo DBeaver | Conexão com túnel SSH para a VM pelo nome do Tailscale, banco em `127.0.0.1:3104`, senha em `POSTGRES_PASSWORD` |
| Restaurar backup | `docker exec -i mo-dev-postgres-1 pg_restore -U ecommerce -d ecommerce --clean --if-exists < /opt/mo/backups/ARQUIVO.dump` |
| Trocar a chave de deploy | `sudo bash /root/bootstrap.sh --new-deploy-key` e atualizar o segredo no GitHub |
| Aplicar mudanças do `bootstrap.sh`, `mo-deploy.sh` ou `mo-backup.sh` | Baixar de novo e rodar o `bootstrap.sh`: os scripts instalados vêm da `main` |

## Se perder o acesso

O SSH público continua aberto com chave. Se ele falhar (chave perdida, configuração errada), entre pelo **VNC** do
painel da Contabo como root e:

- desfaça o endurecimento do SSH: `rm /etc/ssh/sshd_config.d/10-mo.conf && systemctl reload ssh`;
- se o fail2ban bloqueou seu IP: `fail2ban-client set sshd unbanip SEU_IP`;
- se o firewall atrapalhar: `ufw disable`. Nada do Docker depende dele.

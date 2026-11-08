# Ambiente de desenvolvimento na VM

Loja, API e banco numa VM compartilhada, acessíveis só pela rede privada do Tailscale. Plano, decisões e validação em
[specs/entregas/infra-1-ambiente-dev.md](../../specs/entregas/infra-1-ambiente-dev.md); visão geral dos ambientes no
[arquivo 18](../../specs/18-devops-ambiente.md).

| Arquivo | Para quê |
|---------|----------|
| `bootstrap.sh` | Prepara a VM. Roda como root e pode rodar de novo |
| `compose.yml` | Os serviços do ambiente (projeto `mo-dev`) |
| `mo-deploy.sh` | Único comando que a chave de deploy executa (`/usr/local/bin/mo-deploy`) |
| `mo-compose.sh` | Docker Compose do ambiente com os segredos e as versões no ar (`mo-compose`, só root) |
| `mo-backup.sh` | Backup diário do banco às 03:30, 7 dias (`/usr/local/bin/mo-backup`) |
| `mo-restore.sh` | Restaura um backup com segurança (`mo-restore`, só root) |
| `tailscale-policy.hujson` | Política da rede privada |

Os comandos abaixo são para rodar **na VM, como root**: pelo SSH de sempre (`ssh root@IP-PÚBLICO` ou, com o Tailscale
ligado, `ssh root@mo-dev`) ou pelo VNC da Contabo.

## Preparar a VM (uma vez)

1. **Snapshot** da VM no painel da Contabo.
2. **Tailscale:** criar a conta (plano pessoal), instalar no seu PC e entrar com a mesma conta.
3. No painel do Tailscale:
   - **Access controls:** colar `tailscale-policy.hujson` no lugar da política padrão e salvar.
   - **DNS:** confirmar que o **MagicDNS** está ligado e ligar **HTTPS Certificates**.
4. Na VM, como root:

   ```bash
   curl -fsSL https://raw.githubusercontent.com/AndersonLuizdeAlmeid/e-commerce-backend/main/deploy/dev/bootstrap.sh -o /root/bootstrap.sh
   UFW_ALLOW="80/tcp 443/tcp" bash /root/bootstrap.sh
   ```

   `UFW_ALLOW` lista as portas de programas que rodam fora do Docker e devem continuar públicas (nesta VM, o nginx
   nas 80 e 443). Sem isso, o script não liga o firewall para não derrubá-los.

   O script para no passo do Tailscale e mostra um link: abra no navegador e aprove a VM. No fim ele mostra os
   endereços, as variáveis e os segredos para o GitHub. **A chave de deploy aparece só nessa hora.**
5. No GitHub, ambiente `dev` dos dois repositórios (restrito à `main`):
   - Segredos `DEPLOY_SSH_KEY` (a chave mostrada pelo script, da linha `BEGIN` até a `END`), `DEPLOY_KNOWN_HOSTS`,
     `TS_OAUTH_CLIENT_ID` e `TS_OAUTH_SECRET` (credencial OAuth do Tailscale só com Auth Keys de escrita e `tag:ci`).
   - No frontend, também as variáveis `STORE_URL` (`https://mo-dev.<sua-rede>.ts.net`) e `API_URL` (o mesmo endereço
     com `:8443`), **um endereço em cada**. O CI recusa valor fora desse formato antes do build.
6. Merge na `main` do backend e depois do frontend: o primeiro deploy sobe o banco e a API, e a loja só depois deles.
7. Trocar a **senha do VNC** da Contabo por uma forte e guardá-la: é o acesso de emergência.

## No dia a dia

| Tarefa | Como |
|--------|------|
| Abrir a loja | `https://mo-dev.<sua-rede>.ts.net` com o Tailscale ligado no aparelho |
| Painel | `/admin` na loja. Login: `grep ADMIN_ /opt/mo/.env` |
| E-mails | Todos caem no Mailpit, `https://mo-dev.<sua-rede>.ts.net:8025`; nenhum sai para a internet |
| Publicar uma versão | Push na `main`. O CI testa, publica as imagens e o job `deploy` sobe na VM; se a versão nova não ficar saudável em 2 minutos, a anterior volta sozinha e o job fica vermelho. Mudança que mexe nos dois lados: backend primeiro |
| Versão no ar | `cat /opt/mo/state/images.env`; histórico de deploys e restaurações em `/opt/mo/state/history.log` |
| Situação dos serviços | `mo-compose ps` |
| Logs | `mo-compose logs -f --tail 100 api` (ou `web`, `postgres`, `migrator`) |
| Rodar as migrations | Rodam sozinhas em cada deploy do backend. À mão: `mo-compose run --rm migrator` |
| Banco pelo DBeaver | Conexão com túnel SSH para `mo-dev` (Tailscale ligado), banco em `127.0.0.1:3104`, usuário `ecommerce`, senha em `POSTGRES_PASSWORD` do `/opt/mo/.env` |
| Aplicar mudanças do `bootstrap.sh` ou dos scripts `mo-*` | Baixar de novo e rodar o `bootstrap.sh` (passo 4): os scripts instalados vêm da `main` |

## Pagamentos com o Mercado Pago (Fase 4.3)

Sem configuração nenhuma, a API usa a plataforma falsa: o PIX aparece e a tela mostra "Simular pagamento aprovado".
Para usar o sandbox do Mercado Pago:

1. **Credenciais de teste** (painel de desenvolvedores → sua aplicação → Credenciais de teste). Na VM, edite
   `/opt/mo/.env` como root e acrescente as linhas abaixo, colando os valores **direto no arquivo** (não mande print
   nem cole no chat):

   ```
   PAYMENTS_PROVIDER=MercadoPago
   MERCADOPAGO_ACCESS_TOKEN=TEST-...
   MERCADOPAGO_WEBHOOK_SECRET=...
   ```

   Depois, `mo-compose up -d api`.
2. **Webhook pela internet, só no caminho dele.** Cole a política nova (`tailscale-policy.hujson`, que libera o
   Funnel só para a VM) e rode na VM:

   ```
   tailscale funnel --bg --https=10000 --set-path=/api/v1/webhooks/payments http://127.0.0.1:3101/api/v1/webhooks/payments
   tailscale funnel status
   ```

   A URL pública é `https://<nome-da-vm>.<sua-rede>.ts.net:10000/api/v1/webhooks/payments/mercadopago`. Confira com
   `curl -i -X POST` nela: sem assinatura a resposta é `401`, e qualquer outro caminho da porta 10000 dá `404`.
3. **Cadastrar a URL no Mercado Pago** (aplicação → Webhooks → modo de teste), marcar o evento **Pagamentos** e salvar.
   A **assinatura secreta** que aparece ali é o `MERCADOPAGO_WEBHOOK_SECRET` do passo 1.

Sem o passo 2, os pagamentos continuam sendo confirmados pela reconciliação, que consulta os pendentes a cada 5 minutos.

## Backup e restauração

O `mo-backup` roda todo dia às 03:30 e guarda 7 dias em `/opt/mo/backups`. Cobre **só o banco**: as fotos enviadas pelo
painel ficam no volume do RustFS e não entram no backup (aceitável com dados de exemplo).

```bash
mo-backup                                   # backup agora; mostra o arquivo criado
mo-restore                                  # lista os backups
mo-restore ecommerce-20261108-0330.dump     # restaura
```

O `mo-restore` primeiro guarda o estado atual num backup novo, então para a API e a loja, restaura numa transação só
(se der erro, o banco fica como estava), roda as migrations que o backup não tiver e sobe tudo de novo, com a loja
sem cache de páginas. Espera um deploy em andamento terminar. Se a API não voltar saudável, ele avisa qual arquivo
restaurar para desfazer.

## Trocar um segredo

Os segredos ficam em `/opt/mo/.env` (só o root lê). Para trocar um, gere um valor novo com `openssl rand -hex 32`, edite
o arquivo e rode `mo-compose up -d`: o Compose recria só os serviços que usam aquele valor.

| Segredo | Efeito da troca |
|---------|-----------------|
| `JWT_SIGNING_KEY` | Todo mundo é deslogado, inclusive do painel |
| `REVALIDATE_SECRET`, `INTERNAL_KEY` | Nenhum: API e loja são recriadas juntas |
| `ADMIN_PASSWORD` | Nenhum: só é usado para criar o admin quando não existe nenhum. Troque a senha pelo "Esqueci minha senha" (o e-mail chega no Mailpit) |
| `POSTGRES_PASSWORD` | Só vale na criação do banco. Troque primeiro no banco, depois no arquivo: `docker exec -it mo-dev-postgres-1 psql -U ecommerce -c '\password ecommerce'`, digite a senha nova, coloque a mesma no `.env` e rode `mo-compose up -d` |
| Chave de deploy | `UFW_ALLOW="80/tcp 443/tcp" bash /root/bootstrap.sh --new-deploy-key` e atualizar `DEPLOY_SSH_KEY` nos dois repositórios. A antiga para de valer na hora |

Os segredos do GitHub (`TS_OAUTH_*`) são trocados no painel do Tailscale (nova credencial OAuth) e no ambiente `dev`
dos dois repositórios.

## Se perder o acesso

O SSH público continua aberto. Se ele falhar (chave perdida, configuração errada), entre pelo **VNC** do painel da
Contabo como root e:

- desfaça o endurecimento do SSH: `rm /etc/ssh/sshd_config.d/10-mo.conf && systemctl reload ssh`;
- se o fail2ban bloqueou seu IP: `fail2ban-client set sshd unbanip SEU_IP`;
- se o firewall atrapalhar: `ufw disable`. Nada do Docker depende dele;
- se o Tailscale caiu: `tailscale up --hostname=mo-dev --advertise-tags=tag:mo-dev` e aprove pelo link, se pedir.
  Sem ele o deploy para, mas a loja continua no ar na VM.

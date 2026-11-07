# Entrega — Infra 1: ambiente de desenvolvimento na VM

**Status:** Planejada em 07/11/2026. **Fatia I.1 implementada e validada localmente em 07/11/2026** (§9); a publicação no GHCR só roda no primeiro push na `main`.
**Repositórios:** `e-commerce-backend` e `e-commerce-frontend` (branch `feat/ambiente-dev` nos dois).

## 1. Contexto

Hoje a loja só roda na máquina do desenvolvedor. Esta entrega cria um **ambiente de desenvolvimento** numa VM, atualizado sozinho a cada push na `main`:

```
push na main ──► GitHub Actions: testes ──► imagens no GHCR ──► deploy pela rede privada ──► VM: docker compose
```

A VM é uma **Contabo Cloud VPS 10** (4 vCPU, 8 GB, 150 GB, Ubuntu 22.04 x86, região EU). Folga sobre o mínimo do arquivo 18 §7 (2 vCPU, 4 GB).

**A VM é compartilhada.** Já roda outras aplicações de teste em Docker, que **continuam públicas** e algumas recebem deploy por SSH de fora. A loja convive com elas sem tocar em nada delas: rede, volumes, portas e nomes próprios (projeto Compose `mo-dev`), e a preparação da VM não reinstala nem reinicia o Docker.

**Só o desenvolvedor acessa a loja**, pela rede privada do **Tailscale**. Nenhuma porta da loja fica aberta na internet. Domínio público, Cloudflare e staging aberto à MO ficam para depois (§5).

## 2. Objetivos

1. Push na `main` com testes verdes **chega à VM sozinho**, sem passo manual.
2. **Nenhuma porta da loja aberta** na internet; ela só é alcançável pela rede privada. As aplicações que já estão na VM continuam como estão.
3. Os **segredos da aplicação nunca saem da VM**, nem passam pelo GitHub.
4. Quem rouba a chave de deploy **não ganha um shell**: ela só consegue pedir a troca de uma imagem por outra já publicada.
5. Deploy que quebra a API **volta sozinho** para a versão anterior.
6. A VM se recupera de uma perda de acesso pelo console VNC da Contabo, com roteiro escrito.

## 3. Decisões

| # | Ponto | Decisão | Motivo |
|---|-------|---------|--------|
| I1 | Registro das imagens | **GHCR**, imagens `ecommerce-api`, `ecommerce-migrator` e `ecommerce-web`, **públicas**, sem limpeza automática | Os dois repositórios já são públicos, então a imagem não expõe nada novo. Pacote privado no plano grátis tem 500 MB e 1 GB/mês de download, que alguns deploys estourariam, e exigiria credencial do GHCR guardada na VM. Pacote público não tem limite; apagar versões sem tag automaticamente pode quebrar imagens com SBOM e atestado, que ficam em manifestos sem tag |
| I2 | Qual versão sobe | Tag `sha-<commit>` para leitura humana, mas o deploy usa o **digest** (`imagem@sha256:…`) | Digest não pode ser trocado depois de publicado |
| I3 | Acesso à VM | **Tailscale** na VM e no seu PC. O SSH público **continua aberto**, porque outros deploys dependem dele, mas endurecido: `fail2ban`, e senha desligada **só se** o histórico de logins mostrar que todos entram por chave (o script confere antes e, se não, avisa e não mexe). `ufw` com entrada negada por padrão, liberando só a 22 e a interface do Tailscale; ele não afeta as portas que o Docker publica, então as outras aplicações seguem no ar. O firewall da Contabo não é ligado | Não derrubar o que já roda. Recuperação pelo VNC da Contabo |
| I4 | Como o GitHub chega à VM | Ação oficial do Tailscale com **OAuth client**, nó efêmero com a tag `tag:ci`. A ACL deixa `tag:ci` falar **só** com a porta 22 da VM | O runner entra na rede só durante o deploy e não enxerga mais nada |
| I5 | O que a chave de deploy pode fazer | Usuário `deploy` com **comando forçado** no `authorized_keys` (`mo-deploy`), sem terminal, sem túnel. O script aceita só `backend <commit> <digest-api> <digest-migrator>` e `web <digest>`, valida o formato e recusa o resto. Host da VM conferido por `known_hosts` fixo | Objetivo 4 |
| I6 | Segredos da aplicação | Gerados **na VM** pelo script de preparação (senha do banco, chave do JWT, chave interna, segredo da revalidação, chaves do RustFS), em `/opt/mo/.env` com permissão 600. No GitHub ficam só: OAuth do Tailscale, chave de deploy e `known_hosts` | Objetivo 3 |
| I7 | De onde vem o `compose.yml` da VM | Versionado no backend em `deploy/dev/`. A VM mantém um clone **só leitura** do repositório público, e o deploy do backend faz checkout do mesmo commit da imagem | Configuração e imagem andam juntas, e a chave de deploy não precisa enviar arquivos |
| I8 | Nada de dado da VM no repositório | IP, nome da rede privada e endereços ficam em **variáveis do ambiente `dev`** no GitHub, não em arquivo | Os repositórios são públicos |
| I9 | Ambiente da aplicação | `ASPNETCORE_ENVIRONMENT=Development`: seed de exemplo, provedor de frete falso, Mailpit, RustFS e documentação da API | O acesso já é só seu. Ambiente público (Staging) exige domínio e credenciais reais (§5) |
| I10 | Como você abre a loja | `tailscale serve` com HTTPS automático no nome da VM na rede privada: loja na 443, API na 8443, Mailpit na 8025. Os containers publicam portas **só em 127.0.0.1**, numa faixa livre (loja 3100, API 3101, Mailpit 3103, Postgres 3104), fora das que as outras aplicações usam (3000, 3001, 3003, 5000, 5002, 5341 e 5432) | O Docker passa por cima do `ufw` quando publica em `0.0.0.0`; em 127.0.0.1 isso não acontece |
| I10b | Fotos | O RustFS **não é exposto**. A API grava as URLs com o endereço interno (`http://rustfs:9000/mo-images`) e o navegador recebe as fotos pelo `next/image` da loja, que as busca na rede do Compose. Para isso, a imagem `web` do dev é construída com `IMAGES_ALLOW_LOCAL_IP=true` | O container da loja não resolve nomes do Tailscale. A liberação vale só para o host configurado em `remotePatterns`; a produção (R2 público) não usa |
| I11 | Variáveis `NEXT_PUBLIC_*` | Entram no build da imagem `web`, com os endereços das variáveis `STORE_URL` e `API_URL` do ambiente `dev` | O Next.js grava esses valores no JavaScript no build |
| I11b | Páginas pré-renderizadas | O build da imagem roda com a API falsa do CI e depois **apaga as páginas com ISR** do servidor standalone (`scripts/drop-api-prerenders.mjs`). Cada página é gerada com a API de verdade no primeiro acesso e cacheada como sempre | A API do ambiente não existe durante o build. Sem apagar, o visitante veria os dados falsos até a revalidação |
| I12 | Segurança do pipeline | Ações fixadas por **SHA de commit**, `permissions` mínimas por job, ambiente `dev` restrito à `main`, um deploy por vez (`concurrency`), **Trivy** barrando vulnerabilidade crítica com correção disponível, atestado de origem (provenance) e SBOM nas imagens. O Trivy roda pela imagem oficial fixada por digest, não pela `trivy-action`, que já teve tags sequestradas | Cadeia de fornecimento |
| I13 | Ordem e volta atrás | Backend: puxa as imagens → roda o migrator → sobe a API → espera `/health/ready`. Se a API não ficar saudável em 2 minutos, **volta para o digest anterior**. A migração **não** é desfeita: as migrations seguem só para a frente e precisam ser compatíveis com a versão anterior | Objetivo 5 sem prometer o impossível |
| I14 | Containers | Usuário não root, `cap_drop: ALL`, `no-new-privileges`, limite de memória, rotação de log no próprio Compose e `restart: unless-stopped`. Só leitura: API, migrator e Mailpit. A loja não, porque o Next.js grava o cache das páginas dentro de `.next`; o Postgres não, porque o entrypoint troca de usuário. A imagem da loja sai sem npm e corepack | Arquivo 17. A rotação fica no Compose porque mudar o `daemon.json` exigiria reiniciar o Docker e as outras aplicações |
| I15 | Backup | `pg_dump` diário na própria VM, 7 dias. Snapshot da Contabo antes de mudanças grandes | Dados de exemplo; o backup no R2 (arquivo 18 §7) é da produção |
| I16 | Região EU | Aceita para desenvolvimento | Latência só importa para a loja pública; a escolha da produção (São Paulo, arquivo 18 §7) continua valendo |

## 4. Escopo, em fatias

| Fatia | Conteúdo | Repositório |
|-------|----------|-------------|
| **I.1 — Imagens** | Dockerfile do frontend (saída `standalone`, usuário não root). Publicação das três imagens no GHCR a cada push na `main`, depois dos testes, com Trivy e SBOM. `deploy/dev/compose.yml`, ensaiado no PC antes da VM | os dois |
| **I.2 — VM** | `deploy/dev/bootstrap.sh`, que pode rodar mais de uma vez sem estragar nada (atualizações automáticas de segurança, usuário `deploy`, SSH endurecido sem fechar a porta, `fail2ban`, `ufw`, Tailscale, swap; o Docker já instalado é só conferido), `mo-deploy` (comando forçado), configuração do `tailscale serve`, backup diário | backend |
| **I.3 — Pipeline** | Job de deploy nos dois repositórios, ambiente `dev` no GitHub, volta automática, primeiro deploy | os dois |
| **I.4 — Fechamento** | Arquivo 18 atualizado e roteiro de operação: acessar, ver logs, rodar migração, restaurar backup, trocar segredo, recuperar o acesso pelo VNC | backend |

## 5. Fora do escopo

- Domínio, Cloudflare, Caddy e qualquer acesso público (precisa do `.com.br`, pendente no arquivo 20).
- Staging e produção, deploy por tag `vX.Y.Z` com aprovação manual (arquivo 18 §5).
- Cloudflare R2 e Resend: o ambiente de dev segue com RustFS e Mailpit.
- Grafana Cloud e alertas (T08).

## 6. O que depende de você

Na ordem em que vou pedir:

1. **Snapshot da VM** na Contabo, antes de rodar qualquer coisa nela. Junto, **remover o Seq** (decidido em 07/11/2026).
2. **Reiniciar a VM** quando for conveniente: há 53 atualizações e o Ubuntu pede reinício. As outras aplicações voltam sozinhas se tiverem `restart` configurado; o script mostra quais não têm antes de você reiniciar.
3. **Conta no Tailscale** (plano pessoal grátis) e o Tailscale instalado no seu PC.
4. No Tailscale: colar a ACL que eu preparar e criar um **OAuth client** para o GitHub (eu digo os campos).
5. Rodar **um comando** na VM como root, pelo SSH atual ou pelo VNC. Ele endurece a VM e a coloca na rede privada, sem mexer nas outras aplicações.
6. No GitHub: criar o ambiente `dev` com os segredos, ou fazer `gh auth login` para eu criar.
7. Trocar a **senha do VNC** da Contabo por uma forte e guardar no seu gerenciador de senhas: é a porta de emergência.

## 7. Como validar

| Fatia | Validação |
|-------|-----------|
| I.1 | Build das imagens; Trivy sem crítico; loja completa no `deploy/dev/compose.yml` local (login, foto pelo painel, revalidação, e-mail no Mailpit); CI verde no PR; publicação conferida no primeiro push na `main` |
| I.2 | Do meu lado, pelo seu PC: varredura do IP público mostrando só as portas que já existiam (22 e as das outras aplicações), nenhuma da loja; outras aplicações respondendo como antes; `deploy` sem shell (`ssh deploy@vm bash` recusado); containers da loja sem root e sem porta fora de 127.0.0.1 |
| I.3 | Push na `main` chega à VM sem intervenção; imagem com `/health/ready` quebrado volta sozinha para a anterior; dois pushes seguidos não sobem ao mesmo tempo |
| I.4 | Restauração do backup testada uma vez; roteiro seguido do zero numa leitura |

## 8. Riscos e pontos de atenção

- **O `docker` do usuário `deploy` equivale a root.** O comando forçado é o que segura isso; o script precisa validar cada argumento e nunca repassar texto livre ao shell.
- **VM compartilhada.** Uma falha numa das aplicações públicas é a principal porta de entrada da VM, e o Docker é o mesmo. Aceitável para dados de exemplo; **produção precisa de VM própria**.
- **Seq público na 5341.** Se ele não tiver login, qualquer pessoa lê os logs daquela aplicação. Será removido (§6, passo 1); se o deploy da `clientely` recriar o container, a remoção precisa ir para o Compose dela.
- **Senha no SSH.** Se algum deploy entra por senha, ela fica ligada até você trocar por chave; enquanto isso, o `fail2ban` é a única proteção contra tentativa de senha.
- **Perda de acesso** ao mexer no SSH: o script valida a configuração (`sshd -t`) antes de recarregar e não fecha sessões abertas; o VNC fica como saída.
- **Migração que quebra a versão anterior** impede a volta automática (I13). Mudança destrutiva de esquema exige duas etapas.
- **Tailscale fora do ar** impede o deploy, não a loja.

## 9. Situação por fatia

### I.1 — Validação (07/11/2026)

| Verificação | Resultado |
|---|---|
| Build das imagens `api`, `migrator` e `web` no PC | Ok. `web` com 238 MB |
| Páginas pré-renderizadas com a API falsa | 19 páginas com ISR apagadas no build; a home é gerada com a API de verdade no primeiro acesso (log da API mostra as chamadas) e responde `HIT` no seguinte |
| `deploy/dev/compose.yml` com segredos gerados e as imagens locais | Os 7 serviços sobem; migrator e `rustfs-init` terminam com 0; `/health/ready` 200 |
| Login do admin criado pelo `AdminSeed` com senha gerada | Ok, pelo navegador (CORS e cookie entre as portas da loja e da API) |
| Foto enviada pelo painel | Gravada no RustFS interno e exibida no produto pelo `next/image` (`/_next/image?url=http://rustfs:9000/…`) |
| Revalidação da loja pela API (Outbox) | `POST http://web:3000/api/revalidate` respondeu 200 |
| Cadastro de cliente | E-mail "Confirme seu e-mail" chegou ao Mailpit |
| Usuários dos containers | API 1654, loja `node`, RustFS 10001, Mailpit `nobody` |
| Trivy (alta e crítica com correção) | API e migrator: 0. Loja: 10 altas, todas no npm da imagem base; depois de tirar npm e corepack, 0 |
| `actionlint` nos dois workflows | Sem apontamentos |
| Frontend | Lint, typecheck e 177 testes passando |

**Não validado:** a publicação no GHCR, o SBOM e o atestado, que só rodam no push na `main`; e o `environment` vazio no job `image` do frontend em PR, conferido no primeiro PR.

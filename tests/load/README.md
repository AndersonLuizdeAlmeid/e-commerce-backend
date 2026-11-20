# Teste de carga (k6)

Cenários da entrega 6 (§4.6, C12), com as metas do RNF-001. Pela decisão P15, rodam **na VM de produção antes do lançamento**, pela rede privada (sem passar pela Cloudflare, que bloquearia o tráfego como ataque). Depois, o banco é recriado.

| Cenário | Carga | Meta |
|---------|-------|------|
| `vitrine` | 100 req/s por 2 min: vitrine, listagem por categoria, página de produto e busca | p95 ≤ 200 ms |
| `frete` | 10 cotações/s por 1 min | p95 ≤ 500 ms |
| `checkout` | 20 clientes ao mesmo tempo, 5 pedidos cada, com PIX | p95 ≤ 500 ms; 99 % dos pedidos criados |
| `disputa` | 20 clientes pela última unidade de uma peça | exatamente 1 pedido, os outros `409 Ordering.OutOfStock`; saldo final 0, nunca negativo |

O `setup` repõe o estoque da peça do checkout, deixa a peça da disputa com saldo 1, cadastra as clientes e confirma o e-mail delas pelo Mailpit.

## Como rodar na VM (antes de abrir a loja)

1. **Mailpit como SMTP** durante o teste, para as clientes de carga confirmarem o e-mail sem mandar nada para fora: suba o `mailpit` e aponte `Email__Provider=Smtp` e `Email__Smtp__Host=mailpit`.
2. **Limites por IP** que a carga atravessa (todo o tráfego sai de um IP só): durante o teste, suba `RateLimiting__AuthStrict__PermitLimit`, `RateLimiting__ShippingQuote__PermitLimit` e `RateLimiting__CustomerWrite__TokenLimit` (1000 basta). A vitrine passa pelo `X-Internal-Key`, como as páginas renderizadas pela loja.
3. Pagamento e frete podem ficar nos provedores reais em sandbox ou nos falsos. O PIX do checkout só é gerado, nunca pago.
4. Rode pela rede do Compose, com a imagem fixada:

```bash
docker run --rm --network <rede-do-compose> -v "$PWD/tests/load:/load" grafana/k6:1.8.3 run \
  -e API_URL=http://api:8080 -e MAILPIT_URL=http://mailpit:8025 \
  -e INTERNAL_KEY="$RATE_LIMIT_INTERNAL_KEY" -e ADMIN_EMAIL=... -e ADMIN_PASSWORD=... \
  /load/loja.js
```

5. Volte os limites e o e-mail ao normal e **recrie o banco** (P15).

Saída com código diferente de 0 = alguma meta falhou. O resumo mostra o p95 de cada cenário e quantos pedidos a disputa criou.

## Rodada curta no computador

Contra a pilha de E2E da loja (`e-commerce-frontend/e2e/compose.yml`, já com os limites altos e o Mailpit), em menos de 1 minuto. Serve só para conferir que o script funciona, não para medir:

```powershell
docker run --rm -v "${PWD}\tests\load:/load" --add-host=host.docker.internal:host-gateway grafana/k6:1.8.3 run -e SMOKE=1 -e API_URL=http://host.docker.internal:5101 -e MAILPIT_URL=http://host.docker.internal:8125 /load/loja.js
```

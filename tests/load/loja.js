// Carga da loja (RNF-001, RNF-018; entrega 6 §4.6 e P15). Roda na VM de produção antes do lançamento, pela rede
// privada, sem passar pela Cloudflare. O banco é recriado depois. Ver tests/load/README.md.
//
//   k6 run -e API_URL=http://api:8080 -e INTERNAL_KEY=... -e MAILPIT_URL=http://mailpit:8025 -e ADMIN_PASSWORD=... loja.js
//
// Cenários, um depois do outro:
//   vitrine     100 req/s na vitrine (home, categoria, produto, busca)     p95 ≤ 200 ms
//   frete       cotação de frete por CEP                                   p95 ≤ 500 ms
//   checkout    20 clientes fechando pedido com PIX ao mesmo tempo         p95 ≤ 500 ms
//   disputa     20 clientes pela última unidade de uma peça                exatamente 1 pedido; saldo final 0
import http from "k6/http";
import { check, fail, sleep } from "k6";
import { Counter, Trend } from "k6/metrics";
import exec from "k6/execution";

const API = (__ENV.API_URL || "http://localhost:5101").replace(/\/+$/, "");
const MAILPIT = (__ENV.MAILPIT_URL || "http://localhost:8125").replace(/\/+$/, "");
const INTERNAL_KEY = __ENV.INTERNAL_KEY || "dev-internal-key";
const ADMIN_EMAIL = __ENV.ADMIN_EMAIL || "admin@loja.local";
const ADMIN_PASSWORD = __ENV.ADMIN_PASSWORD || "Admin@12345";
const SMOKE = __ENV.SMOKE === "1";
// Peça para o checkout (tem estoque reposto no setup) e peça da disputa (saldo vira 1 no setup).
const CHECKOUT_PIECE = __ENV.CHECKOUT_PIECE || "brinco-gota-cristal";
const RACE_PIECE = __ENV.RACE_PIECE || "bolsa-shopper-couro";
const CUSTOMERS = SMOKE ? 3 : 20;
const PASSWORD = "Carga-Senha-123";

const catalogDuration = new Trend("catalogo_ms", true);
const quoteDuration = new Trend("frete_ms", true);
const checkoutDuration = new Trend("checkout_ms", true);
const raceWins = new Counter("disputa_pedidos_feitos");
const raceLosses = new Counter("disputa_sem_estoque");

export const options = {
  scenarios: {
    vitrine: {
      executor: "constant-arrival-rate",
      exec: "vitrine",
      rate: SMOKE ? 5 : 100,
      timeUnit: "1s",
      duration: SMOKE ? "10s" : "2m",
      preAllocatedVUs: SMOKE ? 5 : 50,
      maxVUs: SMOKE ? 10 : 200,
    },
    frete: {
      executor: "constant-arrival-rate",
      exec: "frete",
      rate: SMOKE ? 1 : 10,
      timeUnit: "1s",
      duration: SMOKE ? "5s" : "1m",
      preAllocatedVUs: SMOKE ? 2 : 20,
      startTime: SMOKE ? "12s" : "2m10s",
    },
    checkout: {
      executor: "per-vu-iterations",
      exec: "checkout",
      vus: CUSTOMERS,
      iterations: SMOKE ? 1 : 5,
      startTime: SMOKE ? "20s" : "3m20s",
      maxDuration: "3m",
    },
    disputa: {
      executor: "per-vu-iterations",
      exec: "disputa",
      vus: CUSTOMERS,
      iterations: 1,
      startTime: SMOKE ? "50s" : "6m30s",
      maxDuration: "1m",
    },
  },
  thresholds: {
    catalogo_ms: ["p(95)<=200"],
    frete_ms: ["p(95)<=500"],
    checkout_ms: ["p(95)<=500"],
    "disputa_pedidos_feitos": ["count==1"],
    "checks{cenario:disputa}": ["rate==1"],
    "checks{cenario:checkout}": ["rate>=0.99"],
    http_req_failed: ["rate<0.01"],
  },
};

const internal = { headers: { "X-Internal-Key": INTERNAL_KEY } };

function json(method, path, body, token, extraHeaders) {
  const headers = { "Content-Type": "application/json", "X-Internal-Key": INTERNAL_KEY, ...(extraHeaders || {}) };
  if (token) headers.Authorization = `Bearer ${token}`;
  return http.request(method, `${API}${path}`, body === undefined ? null : JSON.stringify(body), { headers, responseCallback: http.expectedStatuses({ min: 200, max: 499 }) });
}

/** Repete enquanto a API pedir para esperar (limite de login e cadastro por IP). */
function patiently(call) {
  for (let attempt = 0; attempt < 10; attempt++) {
    const response = call();
    if (response.status !== 429) return response;
    sleep(Number(response.headers["Retry-After"] || 60) + 1);
  }
  fail("A API continuou respondendo 429. Suba os limites de auth-strict durante o teste (README).");
}

function confirmationToken(email) {
  for (let attempt = 0; attempt < 60; attempt++) {
    const search = http.get(`${MAILPIT}/api/v1/search?query=${encodeURIComponent(`to:"${email}"`)}`);
    const message = (search.json("messages") || []).find((m) => m.Subject.includes("Confirme seu e-mail"));
    if (message) {
      const text = http.get(`${MAILPIT}/api/v1/message/${message.ID}`).json("Text");
      const token = /confirmar-email\?token=([^\s)>\]]+)/.exec(text);
      if (token) return decodeURIComponent(token[1]);
    }
    sleep(1);
  }
  fail(`Sem e-mail de confirmação para ${email}. A carga precisa do Mailpit como SMTP (README).`);
}

function customer(index, runId) {
  const email = `carga-${runId}-${index}@carga.teste`;
  const registered = patiently(() =>
    json("POST", "/api/v1/auth/register", { fullName: `Cliente Carga ${index}`, email, password: PASSWORD, phone: "(11) 98765-4321" }),
  );
  if (registered.status !== 201 && registered.status !== 200) fail(`cadastro ${email}: ${registered.status} ${registered.body}`);
  const token = registered.json("accessToken");
  json("POST", "/api/v1/auth/confirm-email", { token: confirmationToken(email) });
  const address = json("POST", "/api/v1/me/addresses", {
    recipient: `Cliente Carga ${index}`, postalCode: "20040-020", street: "Rua da Assembleia", number: String(index + 1), district: "Centro", city: "Rio de Janeiro", state: "RJ",
  }, token);
  return { token, addressId: address.json("id") };
}

function variantOf(slug) {
  const product = http.get(`${API}/api/v1/catalog/products/${slug}`, internal);
  if (product.status !== 200) fail(`Peça ${slug} não está na loja (${product.status}).`);
  return product.json("variants.0.id");
}

export function setup() {
  const admin = patiently(() => json("POST", "/api/v1/auth/login", { email: ADMIN_EMAIL, password: ADMIN_PASSWORD })).json("accessToken");
  const checkoutVariant = variantOf(CHECKOUT_PIECE);
  const raceVariant = variantOf(RACE_PIECE);
  json("POST", `/api/v1/admin/stock/${checkoutVariant}/movements`, { type: "Adjustment", quantity: 1000, reason: "Teste de carga" }, admin);
  json("POST", `/api/v1/admin/stock/${raceVariant}/movements`, { type: "Adjustment", quantity: 1, reason: "Teste de carga: disputa" }, admin);

  const categories = http.get(`${API}/api/v1/catalog/categories`, internal).json().map((c) => c.slug);
  const products = http.get(`${API}/api/v1/catalog/products?pageSize=24`, internal).json("items").map((p) => p.slug);
  const runId = Date.now().toString(36);
  const customers = [];
  for (let i = 0; i < CUSTOMERS; i++) customers.push(customer(i, runId));
  return { admin, checkoutVariant, raceVariant, categories, products, customers };
}

export function vitrine(data) {
  const pick = (list) => list[Math.floor(Math.random() * list.length)];
  const roll = Math.random();
  const response =
    roll < 0.25 ? http.get(`${API}/api/v1/catalog/showcase`, internal)
    : roll < 0.5 ? http.get(`${API}/api/v1/catalog/products?category=${pick(data.categories)}`, internal)
    : roll < 0.85 ? http.get(`${API}/api/v1/catalog/products/${pick(data.products)}`, internal)
    : http.get(`${API}/api/v1/catalog/products?search=${pick(["anel", "colar", "brinco", "prata"])}`, internal);
  catalogDuration.add(response.timings.duration);
  check(response, { "vitrine 200": (r) => r.status === 200 }, { cenario: "vitrine" });
}

export function frete(data) {
  const response = json("POST", "/api/v1/shipping/quotes", { postalCode: "20040-020", items: [{ variantId: data.checkoutVariant, quantity: 1 }] });
  quoteDuration.add(response.timings.duration);
  check(response, { "frete 200": (r) => r.status === 200 }, { cenario: "frete" });
}

// Cada VU fica com uma cliente só em cada cenário: a primeira iteração de cada VU recebe um número único, e a sacola
// de uma cliente nunca é usada por duas VUs ao mesmo tempo (os ids de VU são globais entre cenários).
const assigned = {};
function myCustomer(data) {
  const scenario = exec.scenario.name;
  if (assigned[scenario] === undefined) {
    assigned[scenario] = exec.scenario.iterationInTest % data.customers.length;
  }
  return data.customers[assigned[scenario]];
}

export function checkout(data) {
  const me = myCustomer(data);
  json("POST", "/api/v1/cart/items", { variantId: data.checkoutVariant, quantity: 1 }, me.token);
  const placed = json("POST", "/api/v1/orders", { addressId: me.addressId, shippingServiceCode: "PAC" }, me.token, { "Idempotency-Key": `${exec.vu.idInTest}-${exec.vu.iterationInScenario}-${Date.now()}` });
  checkoutDuration.add(placed.timings.duration);
  const ok = check(placed, { "pedido 201": (r) => r.status === 201 }, { cenario: "checkout" });
  if (!ok) {
    console.error(`checkout ${placed.status}: ${placed.body}`);
  }
  if (ok) {
    const pix = json("POST", `/api/v1/orders/${placed.json("id")}/payments`, { method: "Pix" }, me.token);
    checkoutDuration.add(pix.timings.duration);
    check(pix, { "PIX gerado": (r) => r.status === 200 || r.status === 201 }, { cenario: "checkout" });
  }
}

export function disputa(data) {
  const me = myCustomer(data);
  json("POST", "/api/v1/cart/items", { variantId: data.raceVariant, quantity: 1 }, me.token);
  // Todos chegam juntos: a disputa é pelo mesmo segundo.
  sleep(Math.max(0, 2 - (Date.now() % 2000) / 1000));
  const placed = json("POST", "/api/v1/orders", { addressId: me.addressId, shippingServiceCode: "PAC" }, me.token, { "Idempotency-Key": `disputa-${exec.vu.idInTest}` });
  if (placed.status === 201) raceWins.add(1);
  else raceLosses.add(1);
  check(placed, { "201 ou 409 sem estoque": (r) => r.status === 201 || (r.status === 409 && r.json("code") === "Ordering.OutOfStock") }, { cenario: "disputa" });
}

export function teardown(data) {
  const stock = json("GET", `/api/v1/admin/stock?search=${encodeURIComponent(RACE_PIECE.replace(/-/g, " "))}`, undefined, data.admin);
  const item = (stock.json("items") || []).find((row) => row.variantId === data.raceVariant);
  check(item, { "saldo da disputa termina em 0, nunca negativo": (row) => row && row.quantityOnHand === 0 }, { cenario: "disputa" });
}

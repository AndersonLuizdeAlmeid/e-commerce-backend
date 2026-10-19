# 16 — Frontend (React + Next.js)

**Status:** Em revisão — SEO é prioridade, por isso o frontend usa **Next.js** com renderização no servidor (arquivo 24).

## 1. Decisões

| ADR | Decisão | Motivo |
|-----|---------|--------|
| ADR-F01 | **Next.js (App Router)** em vez de SPA com Vite | HTML completo para o Google, melhor LCP, ISR para catálogo, `next/image`, metadata e sitemap nativos. Continua sendo React. |
| ADR-F02 | Backend C# continua sendo a **única fonte de regras de negócio**; o Next.js só renderiza e faz chamadas à API | Evita lógica duplicada. |
| ADR-F03 | Páginas públicas como **Server Components** buscando a API no servidor; áreas logadas (carrinho, checkout, conta, admin) como **Client Components** chamando a API pelo navegador | Simplicidade: a autenticação continua como no arquivo 08. |
| ADR-F04 | Mesmo domínio raiz para loja e API (`loja.com.br` e `api.loja.com.br`) | Cookies `SameSite` funcionam (mesmo *site*), CORS simples. |

## 2. Stack

| Finalidade | Escolha |
|------------|---------|
| Linguagem | **TypeScript** (strict) |
| Framework | **Next.js** (App Router, React 19) |
| Estado do servidor (cliente) | **TanStack Query** nas áreas logadas |
| Estado local | Context; **Zustand** para o carrinho do visitante |
| Formulários | **React Hook Form** + **Zod** |
| Tipos da API | Gerados do OpenAPI (`openapi-typescript`) |
| Estilo | **Tailwind CSS** + componentes acessíveis (shadcn/ui / Radix) — a confirmar |
| Pagamento | SDK/componente da plataforma escolhida (ex.: Mercado Pago Bricks) para capturar o cartão sem passar pela loja |
| Testes | **Vitest** + **Testing Library** + **MSW**; **Playwright** para E2E; **Lighthouse CI** |
| Qualidade | ESLint, Prettier, `tsc --noEmit` no CI |
| Hospedagem | Vercel ou container Node (Docker) — arquivo 20 |

## 3. Estrutura

```
src/
├── app/
│   ├── (loja)/                     # páginas públicas, renderizadas no servidor
│   │   ├── page.tsx                # home (ISR)
│   │   ├── categoria/[slug]/page.tsx
│   │   ├── produto/[slug]/page.tsx # ISR + generateMetadata + JSON-LD
│   │   ├── busca/page.tsx          # SSR, noindex
│   │   ├── carrinho/page.tsx
│   │   ├── login/ cadastro/ recuperar-senha/ redefinir-senha/ confirmar-email/
│   ├── (conta)/conta/...           # perfil, endereços, pedidos — client, noindex
│   ├── checkout/                   # endereço → frete → revisão → pagamento
│   ├── admin/...                   # backoffice — client, noindex, bundle separado
│   ├── api/revalidate/route.ts     # revalidação sob demanda (segredo compartilhado)
│   ├── sitemap.ts  robots.ts  not-found.tsx
├── shared/  (api, auth, ui, lib)
└── features/ (catalog, cart, shipping, checkout, payment, account, admin)
```

## 4. Telas principais

| Área | Telas |
|------|-------|
| Loja | Home, categoria (com filtros de cor/tamanho/preço), produto (seletor de cor com bolinhas + grade de tamanhos, galeria por cor, simulador de frete por CEP), busca, carrinho (com frete) |
| Conta | Cadastro, login, confirmar e-mail, recuperar senha, perfil, endereços (autocompletar por CEP), meus pedidos, detalhe do pedido (status, pagamento, rastreio) |
| Checkout | Endereço → escolha PAC/SEDEX → revisão → pagamento (PIX com QR + "copiar código" e atualização automática ao ser pago; cartão via componente da plataforma) → confirmação |
| Admin | Dashboard, categorias, cores/tamanhos, produtos + grade de variações + imagens + SEO, estoque (com **botão de leitura de código de barras**: leitor USB ou câmera via `BarcodeDetector`/ZXing), regras de frete, pedidos (ações de status, rastreio), estornos pendentes, usuários |

## 5. Autenticação

- Access token **em memória**; refresh token em cookie `HttpOnly` (`credentials: 'include'`).
- Ao abrir a página: refresh silencioso para restaurar a sessão.
- Interceptor: em `401`, um refresh (single-flight) e repete; se falhar, logout.
- Compra exige conta: ao clicar em "Finalizar compra" sem login → login/cadastro → volta ao checkout com o carrinho mesclado.
- Middleware do Next.js redireciona áreas logadas sem sessão (UX; a segurança real é a API).

## 6. Carrinho

- Visitante: `localStorage` (`variantId` + `quantity`). Ao logar: `POST /cart/merge`.
- Preços e disponibilidade sempre vindos da API.

## 7. Tratamento de erros

- Parser central de ProblemDetails → erros de campo no formulário e `code` → mensagens/ações.
- `429` → aviso com tempo de espera. Error boundaries por rota.

## 8. Requisitos de qualidade

- **SEO**: arquivo 24 (metadata, canonical, JSON-LD, sitemap, Core Web Vitals).
- **Acessibilidade WCAG 2.1 AA**: seletor de cor com nome da cor (não só a bolinha), tamanhos indisponíveis anunciados como tal, foco visível, teclado.
- **Performance**: Lighthouse ≥ 90 em performance e acessibilidade, ≥ 95 em SEO (home, categoria, produto).
- Responsivo, mobile-first (a maioria das compras é pelo celular).

## 9. Configuração

- `API_INTERNAL_URL` (chamadas do servidor Next → API), `NEXT_PUBLIC_API_URL` (navegador → API), `REVALIDATE_SECRET`, chave pública da plataforma de pagamento.
- CSP e headers de segurança (arquivo 17), permitindo os domínios da plataforma de pagamento.

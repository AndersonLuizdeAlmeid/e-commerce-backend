# 24 — SEO (aparecer bem no Google)

**Status:** Rascunho — decisão do cliente: **SEO é prioridade**.

## 1. Consequência principal

Uma SPA pura (React + Vite) entrega HTML vazio e depende do Google executar JavaScript — indexação mais lenta e pior desempenho no primeiro carregamento. Por isso o frontend passa a usar **Next.js (React) com renderização no servidor** (ver arquivo 16, ADR-F01).

| Página | Estratégia de renderização |
|--------|----------------------------|
| Home, categorias, listagem | **ISR** (gerada no servidor e revalidada) |
| Detalhe de produto | **ISR** + revalidação sob demanda quando o produto muda |
| Busca (`/busca?q=`) | SSR, `noindex` (evita conteúdo duplicado) |
| Carrinho, checkout, conta, admin | Renderização no cliente, `noindex` |

## 2. Requisitos

| ID | Requisito |
|----|-----------|
| SEO-01 | URLs amigáveis e estáveis: `/produto/{slug}`, `/categoria/{slug}`. Sem IDs na URL. |
| SEO-02 | Alterar o slug gera **redirect 301** do antigo para o novo (tabela `catalog.slug_history`). Produto desativado → `410 Gone` ou 301 para a categoria (configurável). *Fase 1:* a API responde 301/410; a página Next.js converte em redirect permanente (`permanentRedirect`, HTTP **308**, que o Google trata como 301) para o slug novo ou para a categoria do produto inativo, porque o App Router não emite 410 sem um middleware consultando a API a cada request. Ver arquivo 20, D11. |
| SEO-03 | `<title>` e `<meta name="description">` únicos por página; campos **SEO title/description** editáveis no admin (com valor padrão gerado). |
| SEO-04 | `<link rel="canonical">` em todas as páginas; variações (cor/tamanho) **não** geram URLs indexáveis separadas — canonical para a página do produto. Filtros e ordenação com `noindex,follow` ou canonical para a listagem base. |
| SEO-05 | **Dados estruturados JSON-LD**: `Product`/`ProductGroup` + `Offer`/`AggregateOffer` (preço, moeda, disponibilidade e **GTIN** por variação), `BreadcrumbList`, `Organization`, `WebSite` (com `SearchAction`). |
| SEO-06 | `sitemap.xml` gerado dinamicamente (produtos e categorias ativos, `lastmod` = `updated_at`) e `robots.txt` (bloqueia `/conta`, `/checkout`, `/admin`, `/busca`). |
| SEO-07 | Open Graph e Twitter Cards (imagem, título, preço) para compartilhamento em WhatsApp/redes sociais. |
| SEO-08 | **Core Web Vitals** no verde (p75): LCP < 2,5 s, INP < 200 ms, CLS < 0,1. Lighthouse SEO ≥ 95. |
| SEO-09 | Imagens otimizadas (`next/image`: WebP/AVIF, tamanhos responsivos, `alt` obrigatório no cadastro). |
| SEO-10 | Texto descritivo editável na página de categoria (conteúdo indexável, não só a grade de produtos). |
| SEO-11 | Paginação com links rastreáveis (`?pagina=2`) e canonical próprio por página. |
| SEO-12 | Página 404 útil (busca + categorias) e retorno HTTP correto (404/410), nunca "soft 404". |
| SEO-13 | `lang="pt-BR"`, HTTPS, domínio único (redirect `www`/sem `www`). |
| SEO-14 | Integração com **Google Search Console** e **Google Merchant Center** (feed de produtos para o Google Shopping gratuito) — feed `GET /feeds/google-merchant.xml` (com GTIN quando houver código de barras, o que melhora a aprovação dos produtos). |

## 3. Impacto no backend

- Novos campos em produto/categoria: `seo_title`, `seo_description`, `alt` nas imagens.
- Tabela `catalog.slug_history` para redirects.
- Endpoint `GET /api/v1/catalog/slugs/resolve?path=` (ou incluído no detalhe) para o Next.js saber se responde 200, 301 ou 410.
- **Revalidação sob demanda**: quando o admin altera produto/categoria/estoque, o backend publica um evento (Outbox) que chama `POST {frontend}/api/revalidate` com segredo compartilhado → a página é regenerada em segundos.
- Endpoint do sitemap e do feed Merchant Center (podem ser gerados pelo Next.js consumindo a API).

## 4. Imagens

SEO e performance dependem de imagens boas e rápidas. **Decidido (arquivo 20, D06): upload pelo admin para o Cloudflare R2** (sem custo de saída; gratuito até 10 GB), servido pelo CDN da Cloudflare e otimizado pelo `next/image`. Em dev, MinIO (compatível com S3). Até lá, imagens ilustrativas (D12).

## 5. Medição

- Search Console (indexação, consultas), PageSpeed/CrUX (Web Vitals reais), relatório de erros 404.
- Teste automatizado no CI: Lighthouse CI nas páginas home, categoria e produto (orçamento de performance/SEO).

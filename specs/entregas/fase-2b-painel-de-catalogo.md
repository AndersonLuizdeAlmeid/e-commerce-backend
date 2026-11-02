# Entrega — Fase 2B: Painel de catálogo e nova identidade

**Status:** Implementada em 31/10/2026 e **validada contra banco, API, bucket e container reais em 02/11/2026** (seção 7, "Validação real"). Faltam só as verificações que pedem celular de verdade: brilho de folha, câmera do leitor, foto HEIC de um iPhone e arrastar fotos com o dedo.
**Repositórios:** `e-commerce-backend` e `e-commerce-frontend`.

## 1. Contexto

A Fase 2 foi dividida em 2A (contas e e-mails, implementada em 26/10/2026) e **2B**: o painel onde a MO Acessórios cadastra o catálogo sozinha. Depois da 2A, três coisas mudaram o escopo:

1. A loja é de **semijoias, acessórios e bolsas**, não de roupas. O modelo `cor × tamanho` continua genérico, com rótulos por categoria (arquivo 26, ADR-F06/F07).
2. A direção visual foi escolhida: **Cartela & Expositor** (arquivo 26). A loja inteira troca de cara nesta fase, e o admin já nasce no novo visual.
3. Entraram requisitos novos: **coleções** (RF-024), **vitrine** (RF-025), **rótulos de variação** (RF-050) e o detalhamento do **upload de imagens** (RF-061).

Por ser grande, a entrega é feita em **quatro fatias**. Cada uma fecha com build verde e checklist próprio, então dá para validar sem esperar o fim.

| Fatia | Conteúdo | Depende de |
|-------|----------|------------|
| **2B.1 — Fundação visual** | Design system "Cartela & Expositor" e novo visual nas telas existentes (Fases 1 e 2A) | — |
| **2B.2 — Cadastro do catálogo** | Categorias (subcategoria e rótulos), cores, tamanhos, produtos, grade de variações e código de barras | 2B.1 (componentes do admin) |
| **2B.3 — Imagens** | Upload para o R2 (RustFS em dev), galeria por acabamento e regra "sem imagem não ativa" | 2B.2 |
| **2B.4 — Coleções e vitrine** | Coleções, peça em destaque, ocasiões, página de coleção, nova home e revalidação das páginas | 2B.2 e 2B.3 |

Decisões que a entrega aplica: D06 (R2), D10 (só ler e cadastrar EAN), D11, Q07 (1 nível de subcategoria), T09 (Tailwind + Radix, sem visual do shadcn) e arquivo 26 (ADR-F05 a F08).

## 2. Objetivos

1. A administradora **cadastra um produto completo sozinha**, do celular ou do computador: categoria, texto, SEO, grade acabamento × aro/comprimento, preço, código de barras e fotos. A peça aparece na loja sem ajuda técnica.
2. **Foto de iPhone funciona** (HEIC), sai sem localização (EXIF/GPS) e fica leve (WebP).
3. A loja **parece a MO**: cartelas, expositor, monograma e a interação de virar a cartela, no celular primeiro.
4. **O que o admin muda aparece na loja em segundos**: o cache da API é invalidado por tag e as páginas do Next são revalidadas, sem esperar o ISR.
5. Trocar o slug **não quebra links nem SEO** (301 para produto, categoria e coleção).
6. A administradora **monta as vitrines**: coleções Eles e Prata 925, peça em destaque da home e ocasião da vez.

## 3. Escopo planejado

### 3.1 Banco

| Script | Conteúdo | Fatia |
|--------|----------|-------|
| `V0009__catalog_category_hierarchy_and_labels.sql` | `categories.parent_id` (Q07; trigger ou CHECK para 1 nível), `color_label`, `size_label` | 2B.2 |
| `V0010__catalog_images_storage.sql` | `product_images`: `storage_key`, `width`, `height`, `created_at` | 2B.3 |
| `V0011__catalog_collections.sql` | `collections` (nome, slug, descrição, SEO, `card_theme`, posição, ativa, capa), `product_collections`; `slug_history.entity_type` passa a aceitar `'Collection'` | 2B.4 |
| `V0012__catalog_showcase.sql` | `showcase_featured` (linha única), `occasions` (período com `EXCLUDE USING gist` contra sobreposição), `occasion_products` | 2B.4 |
| Seed `S0003__example_jewelry.sql` | Categorias de semijoias e bolsas com rótulos, acabamentos (Dourado, Prateado, Ródio, Prata 925), aros, comprimentos e tamanhos de bolsa, coleções Eles e Prata 925, produtos de exemplo; **inativa** os exemplos de roupa | 2B.2 / 2B.4 |

Cada script novo entra no `database/gerar-scripts.sh`. V0009 e V0010 estão em `database/fase-2b-schema.sql`; V0011 e V0012 ficam em `database/fase-2b4-schema.sql`, para quem já rodou o primeiro não travar em "Script já aplicado". O mesmo vale para os dados de exemplo: S0003 em `fase-2b-dados-exemplo.sql`, S0004 em `fase-2b4-dados-exemplo.sql`.

### 3.2 Domain e Application

- Agregados `Category`, `Color`, `Size`, `Product` (com `ProductVariant` e `ProductImage` dentro), `Collection` e `Occasion`; `ShowcaseFeatured` como entidade simples.
- Invariantes no Domain: subcategoria com 1 nível (RN), SKU e EAN únicos (RF-056 CA1/CA4, com dígito verificador), uma variação por cor + tamanho (CA2), produto sem imagem não ativa (RF-051 CA3) e ocasiões sem sobreposição (RF-025 CA2, garantido também pelo banco).
- Toda escrita do admin publica no **Outbox** um evento `CatalogChanged { tags, paths }`. O processador invalida o HybridCache por tag (`ICacheService.RemoveByTagAsync`) e chama o `/api/revalidate` do Next (arquivo 12 §4, arquivo 24). Assim o cache só muda **depois do commit** e uma falha de rede tenta de novo.
- `IImageStorage` (put, delete, URL pública) com implementação S3 (`AWSSDK.S3`), que serve tanto para o **R2** quanto para o **RustFS** no Docker Compose de desenvolvimento.
- `IImageProcessor` com **Magick.NET**: lê HEIC/JPEG/PNG/WebP, corrige a orientação, remove os metadados, reduz para no máximo 2400 px e salva em WebP. A escolha recai sobre o Magick.NET porque o ImageSharp não lê HEIC.
- Apagar o arquivo do R2 vai pelo Outbox (RF-061 CA4).

### 3.3 API (`/api/v1`, rotas no arquivo 07)

| Grupo | Requisitos | Fatia |
|-------|------------|-------|
| `admin/categories`, `admin/colors`, `admin/sizes` | RF-050 (com rótulos e subcategoria), RF-057 | 2B.2 |
| `admin/products`, `.../variants/generate`, `.../variants/{id}` | RF-051, RF-056 | 2B.2 |
| `admin/variants/by-barcode/{code}` | RF-059 (busca e vínculo; **entrada de estoque por leitura fica para a Fase 3**) | 2B.2 |
| `admin/products/{id}/images`, `.../images/order` | RF-061 | 2B.3 |
| `admin/collections`, `.../products`, `.../image` | RF-024 | 2B.4 |
| `admin/showcase/featured`, `admin/occasions` | RF-025 | 2B.4 |
| `catalog/collections`, `catalog/showcase`, `?collection=` em `catalog/products`, `variantLabels` em categoria e produto, coleções no sitemap | RF-024, RF-025, RF-050 | 2B.2 / 2B.4 |

Todas as rotas `admin/*` usam a policy `Admin` e uma partição nova no rate limiter, `admin-write`. O upload usa limite de corpo próprio (16 MB) só na sua rota.

### 3.4 Frontend

**2B.1 — Fundação visual (arquivo 26 §2 e §9):**
- Tokens Tailwind (tintas, tipos, espaçamento), Prata e Jost via `next/font`; sai o Inter.
- Componentes: `Cartela` com `Brilho`, vitrine, etiquetas, `SeletorDeAcabamento`, `SeletorDeTamanho` (com rótulo da categoria), `Sacola` (só o ícone e o contador; a sacola é da Fase 3), `FalarComAMO`, esqueletos e estados (arquivo 26 §5).
- Interação assinatura: brilho de folha nas cartelas e cartela que vira na página do produto; tudo respeita `prefers-reduced-motion`.
- Novo visual em home, categoria, produto, busca, 404, login, cadastro, senha e conta, **sem mudar comportamento**.
- `SITE_NAME` passa a "MO Acessórios". Monograma provisório em texto Prata até chegar o SVG (D-F06).

**2B.2 a 2B.4 — Painel `/admin` (modo Operate, arquivo 26 §4.8):**
- Client components com **TanStack Query**, **React Hook Form + Zod** e Radix; `noindex`, bundle separado e middleware que manda quem não é admin para o login.
- Telas: categorias, acabamentos/cores, tamanhos, lista de produtos (busca, filtros, ações em lote para coleções), editor de produto (dados, SEO, coleções, grade de variações em formato de planilha, fotos), coleções, vitrine (destaque e ocasiões).
- **Fotos:** escolher várias de uma vez (galeria ou câmera do celular); cada arquivo sobe numa requisição própria, até 3 ao mesmo tempo, com progresso e resultado por foto. Arrastar para reordenar (dnd-kit, com alternativa por teclado). Aviso quando a foto tem menos de 1600 px.
- **Código de barras:** botão "Ler código de barras" com `BarcodeDetector` e fallback `@zxing/browser`; leitor USB funciona como teclado no campo.
- Loja: página `/colecao/[slug]`, home com destaque e ocasião (`/api/catalog/showcase`), etiquetas de navegação com as coleções e rótulos de variação vindos da API.
- `next.config`: domínio das imagens (RustFS em dev; R2 em staging/produção, que depende do domínio pendente no arquivo 20).

### 3.5 Testes a escrever

| Projeto | Cobertura |
|---------|-----------|
| `Domain.UnitTests` | Subcategoria em 1 nível, SKU/EAN (dígito verificador), uma variação por cor + tamanho, ativação sem imagem, sobreposição de ocasião, herança de rótulos |
| `Application.UnitTests` | Validators de categoria, produto, variação, coleção, ocasião e rótulos de variação (tamanhos máximos) |
| `Api.FunctionalTests` | RF-050/051/056/057/059 (busca), RF-024, RF-025, RF-061 com armazenamento falso: HEIC de exemplo, arquivo falso com extensão `.jpg`, EXIF removido, limite de 15 MB; 301 de slug para os três tipos; 403 para Customer em `admin/*`; o evento `CatalogChanged` invalida o cache |
| `ArchitectureTests` | Domain sem dependência de Magick.NET e AWS; handlers do admin selados e internos |
| Frontend (Vitest) | Seletores com rótulo da categoria e estado esgotado, resolução de rótulos, validação dos formulários do admin (Zod), fila de upload (3 simultâneos, erro por arquivo) |

## 4. Fora do escopo

- Movimentação de estoque e entrada por leitura de código de barras (RF-052, RF-059 parte de estoque) → Fase 3.
- Sacola, frete e checkout → Fases 3 e 4. O fluxo de presente (RF-047) entra na Fase 4.
- Pedidos, usuários e dashboard no admin → Fase 5.
- Gerar ou imprimir etiquetas de código de barras (D10).
- Domínio de produção do CDN de imagens → depende do domínio (arquivo 20).

## 5. Pendências da loja que afetam esta entrega

| # | O que falta | Impacto se não chegar |
|---|-------------|-----------------------|
| D-F06 | **Logo em SVG** | Monograma provisório em texto; troca simples depois |
| D-F06 | **Fotos no padrão do arquivo 26 §7.2** | O seed usa fotos ilustrativas (D12); a direção fica mais fraca até as fotos reais |
| D-F10 | Cuidados que definem o "uso correto" | O verso da cartela mostra a garantia sem o texto de cuidados |

Nenhuma delas bloqueia o código.

## 6. Como validar

Seguindo o combinado: **primeiro só build** (etapa A), depois banco pelo DBeaver (B) e por último execução (C). Cada fatia preenche as linhas dela.

### Etapa A — build (a cada fatia)

| # | Comando | Esperado |
|---|---------|----------|
| A1 | `dotnet build ECommerce.sln` | 0 erros, 0 avisos |
| A2 | `dotnet format ECommerce.sln --verify-no-changes` | sem alterações |
| A3 | `npm run lint && npm run typecheck` | sem erros |
| A4 | `npm test` | verdes |
| A5 | `npm run build` contra o stub (`scripts/ci-api-stub.mjs` atualizado com coleções e vitrine) | conclui |

### Etapa B — banco pelo DBeaver

| # | Passo | Esperado |
|---|-------|----------|
| B1 | Rodar `database/fase-2b-schema.sql` e depois `database/fase-2b4-schema.sql` num banco com a 2A | `COMMIT` nos dois; V0009–V0012 em `public.schemaversions` |
| B2 | Rodar B1 de novo | para com "Script já aplicado" |
| B3 | Rodar `database/fase-2b-dados-exemplo.sql` e depois `database/fase-2b4-dados-exemplo.sql` | categorias de semijoias com rótulos; produtos de roupa inativos; coleções Eles e Prata 925, destaque e ocasião |

### Etapa C — execução

| # | Fatia | Verificação | Esperado |
|---|-------|-------------|----------|
| C1 | todas | `docker compose up -d postgres mailpit rustfs rustfs-init` e `dotnet test` | todos verdes |
| C2 | 2B.1 | Home, categoria e produto no celular (390 px) e no desktop (1440 px) | visual de cartela/expositor; nome, preço e ação da primeira cartela visíveis sem rolar |
| C3 | 2B.1 | Lighthouse CI | ≥ 90 performance/acessibilidade, ≥ 95 SEO (não pode cair em relação à Fase 1) |
| C4 | 2B.1 | `prefers-reduced-motion` ligado | cartela troca sem animação |
| C5 | 2B.2 | Admin cria subcategoria "Anéis masculinos" dentro de "Anéis" e tenta uma 3ª camada | 3ª camada recusada |
| C6 | 2B.2 | Produto com grade Dourado/Prateado × Aro 14–20 | 14 variações; a loja mostra "Acabamento" e "Aro" |
| C7 | 2B.2 | Ler um EAN pela câmera do celular | encontra a variação ou oferece vincular |
| C8 | 2B.3 | Subir 5 fotos do iPhone (HEIC) pelo celular | todas viram WebP; `exiftool` não mostra GPS |
| C9 | 2B.3 | Subir um `.txt` renomeado para `.jpg` | recusado com motivo; as outras fotos do lote sobem |
| C10 | 2B.3 | Ativar produto sem foto | recusado com mensagem clara |
| C11 | 2B.4 | Criar coleção "Eles" com 3 produtos em lote | `/colecao/eles` com cartelas grafite; está no sitemap |
| C12 | 2B.4 | Trocar a peça em destaque | home atualizada em segundos, sem redeploy |
| C13 | 2B.4 | Cadastrar duas ocasiões com período sobreposto | 409 com mensagem |
| C14 | todas | Trocar o slug de produto, categoria e coleção | URL antiga responde 301 |
| C15 | todas | Entrar como cliente e abrir `/admin` | redireciona; API responde 403 |

**Critério de aceite:** A1–A5, B1–B3 e C1–C15 passando.

## 7. Situação por fatia

### 2B.1 — Fundação visual (implementada em 29/10/2026, só frontend)

**O que entrou:**

| Peça | Arquivos |
|------|----------|
| Tokens (tintas, Prata + Jost, vitrine rosé, foco, seleção, barra de rolagem, números tabulares, movimento) | `src/app/globals.css`, `src/app/layout.tsx` |
| `Cartela`, `CartelaVazia`, `CartelaEsqueleto` e o `Brilho` de folha | `features/catalog/components/Cartela.tsx`, `Brilho.tsx` |
| Vitrine (grade com cartela de destaque) | `ProductGrid.tsx` |
| Etiquetas das categorias | `CategoryNav.tsx` |
| Página de listagem com filtros em cartela (recolhível no celular) | `ListingLayout.tsx`, `ListingFilters.tsx`, `Pagination.tsx`, `ListingSkeleton.tsx`, `Breadcrumb.tsx` |
| Produto: cartela com brilho de folha que **vira** (detalhes e garantia no verso), seletores com rótulo da categoria, barra do polegar com preço e WhatsApp | `ProductPurchase.tsx`, `produto/[slug]/page.tsx` |
| Rótulos de variação com padrão "Cor"/"Tamanho" até a API mandar (RF-050) | `variant-labels.ts` (+ teste) |
| Monograma provisório, ícones desenhados, "Falar com a MO", rodapé com garantia | `shared/ui/Monograma.tsx`, `icons.tsx`, `FalarComAMO.tsx`, `shared/lib/site.ts` |
| Novo visual em home, categoria, busca, 404, login, cadastro, senha e conta | páginas em `src/app` e `features/auth/components` (só classes; comportamento igual) |

**Decisões tomadas na implementação:**
- **Dourado em dois tons.** `folha` `#9C7A3C` sobre a cartela (3,5:1, só indicador) e `folha-vitrine` `#7E6128` sobre o rosé (3,9:1; marfim sobre ele, 5:1). O dourado do logo (`#B8934A`) não passa 3:1 nos fundos da loja.
- **Erro em `#8A2C1F`.** É a única cor fora do mundo, com 7,4:1. Sucesso usa tinta.
- **Botão "Adicionar à sacola" desabilitado,** como antes. A barra fixa do celular leva preço e WhatsApp; o botão entra nela na Fase 3, quando a sacola existir. O ícone de sacola também fica para a Fase 3, para não mostrar um controle que não funciona.
- **WhatsApp configurável** por `NEXT_PUBLIC_WHATSAPP_NUMBER` (está no `.env.example`), com o número do Instagram como padrão.
- **Ficou de fora: a transição animada entre a grade e o produto (View Transitions).** O React/Next desta versão só oferece isso como experimental. Entraram o brilho de folha e a cartela que vira.

**Validação:**

| # | Situação |
|---|----------|
| A1, A2 | Não se aplicam (backend não mudou) |
| A3 | ✅ lint e typecheck, 29/10/2026 |
| A4 | ✅ 35 testes (3 novos de rótulos), 29/10/2026 |
| A5 | ✅ `npm run build` contra o stub, 12 rotas, 29/10/2026 |
| Detector de design (`impeccable detect`) | ✅ sem achados |
| C2 | ✅ 29/10/2026, contra o stub. Celular (375 px) e desktop (1440 px): home, categoria (filtros abertos e fechados), produto (virar, voltar, acabamento esgotado, aro esgotado), login, 404 |
| C3 | ✅ acessibilidade, SEO e boas práticas **100/100/100** em home, categoria e produto (Lighthouse 12.8, celular e desktop, servidor de dev). ⏳ **Performance fica para o CI**: em modo dev o número não vale, e a porta 3000 estava com um `next start` que não foi iniciado nesta sessão, então o `.next` não foi reconstruído por baixo dele |
| C4 | ✅ as regras de `prefers-reduced-motion` estão no CSS servido (assentar e virar ficam sem animação). O navegador de teste não emula essa preferência, então a verificação foi pelo CSS |

**Correções feitas na revisão:**
- **Cartela de destaque:** no desktop, nome, preço e "Ver a peça" ficavam abaixo da dobra. Do tablet em diante ela virou uma cartela larga, com foto de um lado e texto do outro.
- **Produto:** nome e preço saíram da cartela para o topo da coluna ao lado, para aparecerem na primeira tela do desktop. No celular, a foto ficou quadrada.
- **Topo no celular:** a busca virou um ícone que leva a `/busca`, para não espremer o link da conta.
- **Gancho** redesenhado, mais visível: furo com borda e haste com brilho.
- **Foco:** o estilo global estava fora das camadas do Tailwind e desenhava um segundo contorno em volta da cartela. A base e os componentes foram para `@layer`, e agora aparece só o aro de folha.
- **Lighthouse:**
  - O relevo "MO" agora vem por `content` (era texto com contraste 1,3).
  - Os títulos das cartelas passaram a `h2` (ordem de cabeçalhos).
  - O nome do link do monograma passou a conter o texto visível.
  - Entrou um favicon provisório (`app/icon.svg`); a falta dele gerava um 404 no console.
- **Stub do CI com dados da MO:** 5 categorias e 12 peças com acabamentos, aros, comprimentos e combinações esgotadas. O `lighthouserc.json` agora aponta para `/categoria/aneis` e `/produto/anel-solitario-zirconia`.
- `.claude/launch.json` ganhou `api-stub` e `loja-stub`, para revisar a loja sem a API real.

Depois das correções: lint, typecheck, 35 testes e detector de design verdes.

**Revisão da direção com a cliente (29/10/2026):** ela não gostou do verde, do gancho/cabide e das letras finas da Bodoni no fundo claro. Para escolher, comparou fundo, cartão, letras e cor do texto numa página de estudo temporária dentro da própria loja (`/estudo-visual`, já removida). Escolha: **rosé claro, brilho de folha, Prata e texto preto**. O que mudou:
- Tokens renomeados de `expositor`/`feltro` para `vitrine` (`vitrine`, `vitrine-funda`, `vitrine-tinta`, `vitrine-texto`, `folha-vitrine`). O texto sobre o fundo virou token, porque o fundo agora é claro.
- Gancho, encaixe e o "assentar" saíram. A cartela ganhou o componente `Brilho`: inclina na direção do mouse (menos na página do produto), uma luz passa por ela e aparece um fio de folha na borda. A inclinação some com `prefers-reduced-motion`.
- Bodoni Moda trocada por Prata. Os estados que usavam o verde como hover ou sucesso passaram a usar grafite ou tinta.
- Bordas e etiquetas sobre o fundo agora são escuras. A categoria e a página atuais ficam em bronze com texto marfim.
- Verificado de novo no navegador (375 px e 1440 px). Lighthouse: acessibilidade, SEO e boas práticas **100** em home, categoria e produto, celular e desktop. O login fica com SEO 63 de propósito (`noindex`). O navegador de teste tem movimento reduzido ligado, o que confirmou na prática que a cartela não inclina (C4). Lint, typecheck, 35 testes e detector de design verdes. A spec 26 §2 e o contrato de direção (`.impeccable/surfaces/src-app-loja.md`) foram atualizados.

**Revisão de acabamento independente: `ship`** (vale para os 8 ajustes pedidos e a regressão abaixo, não para a superfície inteira; busca e `/conta` foram julgadas só pelo código):
1. Brilho no celular, levado pela rolagem (`animation-timeline: view()`); só pulso de opacidade com movimento reduzido. Conferido só no código.
2. "MO" impresso tom sobre tom, sem o falso relevo.
3. Preço em Prata na página do produto e na cartela da vez.
4. Home com 11 peças: fileiras inteiras em 2, 3 e 4 colunas.
5. Listagem com 3 colunas ao lado dos filtros.
6. Rodapé com folga para o botão "Falar com a MO".
7. Filtros como etiquetas com ponta (`etiqueta-ponta`) e aro/comprimento como furos numerados.
8. Vitrine rosé até o rodapé em páginas curtas. Esse ajuste estreitou seções com `mx-auto` dentro do `<main>` flex; a revisão pegou e foi corrigido com `w-full`.

**`DESIGN.md`** e `.impeccable/design.json` gerados do que foi construído. Os três desvios que ele apontou (hover em branco nas categorias, na paginação e no 404; sombra cinza do "Falar com a MO") foram corrigidos em 31/10/2026.

**Validação final (31/10/2026, build de produção contra o stub):**

| # | Situação |
|---|----------|
| A3, A4 | ✅ lint, typecheck e 35 testes |
| A5 | ✅ `npm run build`, com `/api/revalidate` e `/icon.svg` |
| C3 | ✅ Lighthouse no build de produção. Celular: performance 94 a 95, LCP 2,7 a 2,9 s. Desktop: performance 100, LCP 0,6 s. Acessibilidade, SEO e boas práticas 100 e CLS 0 em home, categoria e produto |
| `/api/revalidate` | ✅ sem segredo ou com segredo errado 401; tag desconhecida 400; tags válidas 200 |

As fotos ainda são placeholders, então os números de performance precisam ser medidos de novo com as fotos reais.

**Ainda falta na 2B.1:** olhar o brilho de folha num celular real.

### 2B.2 — Cadastro do catálogo (implementada em 31/10/2026, validada contra a API real em 02/11/2026)

**O que entrou:**

| Camada | Peças |
|--------|-------|
| Banco | `V0009` (subcategoria com gatilho de um nível, rótulos da grade, `version` em categorias); `database/fase-2b-schema.sql` e `database/fase-2b-dados-exemplo.sql` (seed `S0003`: semijoias e bolsas, inativa os exemplos de roupa) |
| Domínio | `Slug`, `Gtin`, `Category`, `Color`, `Size`, `Product` com `ProductVariant` (RN-020 a RN-026, Q07) |
| API | `admin/categories`, `admin/colors`, `admin/sizes`, `admin/products` com grade e variações, `admin/variants/by-barcode/{code}`; política `admin-write` |
| Loja | `variantLabels` resolvidos em categoria e produto; categoria mãe lista as peças das subcategorias; navegação só com as principais; **301 de categoria** (pendência da Fase 1) |
| Cache e páginas | `CatalogChanged` no Outbox invalida o HybridCache e chama `/api/revalidate`, com a seção `Storefront` na configuração |
| Painel `/admin` | Moldura própria (a da loja foi para `StoreChrome`), produtos (lista, busca, filtros, novo, editor), grade em formato de planilha, categorias com subcategoria e rótulos, acabamentos e tamanhos, leitor de código (USB, câmera, digitação) |

**Decisões tomadas na implementação:**
- **Produto nasce fora da loja** e entra quando a administradora ativa (RN-023).
- **Saldo zero em toda variação nova.** A entrada de estoque é da Fase 3. **Até lá, peça cadastrada pelo painel aparece como esgotada na loja**; o editor avisa isso.
- **"Sem foto não ativa" (RF-051 CA3) fica para a 2B.3**, junto com o upload; sem isso nenhum produto novo poderia ser ativado agora.
- **SKU gerado** como `ANEL-SOLITARIO-DOURADO-14`, com sufixo `-2` se já existir na loja, e editável.
- **Guarda do painel no navegador**, como em `/conta`, em vez do middleware citado no arquivo 16 §5: o cookie de sessão é da API, o Next não o enxerga. Quem protege de verdade é a policy `Admin`.
- **Diálogo nativo** (`<dialog>`) no leitor de código, em vez de Radix: é o único diálogo e o navegador já cuida de foco e teclado. Radix ainda não foi instalado.
- O `schema.d.ts` recebeu `variantLabels` e `parentId` à mão; os tipos do painel estão em `features/admin/api.ts`. `npm run gen:api` confere isso em C3.

**Correção fora do escopo:** o teste `CommandHandlers_AreSealedInternalAndEndWithHandler` estava errado desde a 2A (a exceção de `Behaviors` valia só para um lado do `Or`). Foi corrigido.

**Validação em 31/10/2026:**

| # | Situação |
|---|----------|
| A1, A2 | ✅ `dotnet build` com 0 avisos e 0 erros (com `--artifacts-path`, porque o Visual Studio segurava as DLLs); `dotnet format` sem alterações |
| A3, A4 | ✅ lint, typecheck e 63 testes do frontend (28 novos de validação) |
| A5 | ✅ `npm run build` contra o stub, com as rotas `/admin` |
| Testes .NET sem banco | ✅ Domain 63, Arquitetura 8, Application 32 |
| Testes funcionais (`AdminCatalogTests`) | ⏳ **escritos e compilando, não executados**: o Docker estava desligado |
| Navegador contra o stub (375 px e 1024 px) | ✅ login, lista, validação do formulário vazio, criar produto, gerar grade, código inválido na linha, salvar código e preço, ativar, categorias, acabamentos e tamanhos, leitor com código inválido, desconhecido e conhecido. Achou e corrigiu: rolagem horizontal causada por `sr-only`, campo de ordem espremendo o tamanho, leitor fechando sozinho no modo estrito, formulário perdendo edição ao salvar a grade, "Sair" escondido no celular |
| Câmera do leitor (C7) | ⏳ não testada: precisa de um celular de verdade |
| B1–B3 | ⏳ rodar no DBeaver: `fase-2b-schema.sql` e `fase-2b-dados-exemplo.sql` |
| C1, C5, C6, C7, C14, C15 | ⏳ execução contra a API real |

O stub (`scripts/ci-api-stub.mjs`) ganhou login e endpoints do painel em memória, para revisar as telas sem banco. **Ele aceita qualquer senha como admin** e serve só para desenvolvimento e CI.

**Ponto em aberto:** desativar uma categoria mãe não esconde as peças das subcategorias ativas. Elas somem da navegação, mas continuam acessíveis pelo link direto.

### 2B.3 — Imagens (implementada em 31/10/2026; envio real validado com RustFS em 02/11/2026, R2 ainda não)

**O que entrou:**

| Camada | Peças |
|--------|-------|
| Banco | `V0010` (`storage_key`, `width`, `height`, `created_at` em `catalog.product_images`), já dentro de `database/fase-2b-schema.sql` |
| Domínio | `ProductImage` e `IProductImageRepository`; erro de `alt` inválido |
| Processamento | `MagickImageProcessor` (Magick.NET): detecta o formato pelos *magic bytes*, lê JPEG, PNG, WebP e HEIC, corrige a orientação, converte para sRGB, remove metadados, reduz para 2400 px e salva em WebP |
| Armazenamento | `S3ImageStorage` (AWSSDK.S3), seção `ImageStorage` da configuração; `rustfs` e `rustfs-init` no `docker-compose.yml` (eram `minio` e `minio-init`; ver "Validação real") |
| API | `POST /admin/products/{id}/images`, `PUT .../images/order`, `PUT` e `DELETE .../images/{imageId}`; limite de 16 MB só na rota de envio |
| Regras | Produto sem foto não ativa (RF-051 CA3); remover a foto apaga do banco na hora e do bucket pelo Outbox (`DeleteStoredImage`, RF-061 CA4) |
| Painel | `ProductPhotos.tsx`: fila por arquivo (até 10, 3 em paralelo), "Tirar foto" no celular, galeria por acabamento, capa, arrastar com mouse, toque ou teclado, descrição salva ao sair do campo, aviso abaixo de 1600 px |
| Loja | `next.config.ts` libera o domínio de `NEXT_PUBLIC_IMAGES_URL` para o `next/image` |

**Decisões tomadas na implementação:**
- **A linha entra no banco antes do envio ao bucket.** Se o bucket falhar, a transação desfaz a linha e não sobra foto quebrada no catálogo.
- **A última foto de um produto ativo não pode ser removida** (`ActiveProductNeedsImage`). Sem isso, a regra de ativação seria contornada depois de ativar.
- **Progresso por foto é o estado de cada arquivo** (Na fila, Enviando, Enviada, Não enviada), sem barra de porcentagem. O `fetch` não informa o avanço do envio.
- **Teste de HEIC com arquivo real.** O Magick.NET lê HEIC, mas não grava, então os testes usam uma amostra de 619 bytes com EXIF e GPS embutida em base64.

**Correção da revisão no navegador:** no celular, as fotos ficavam em uma coluna, uma por tela, e com `sizes="200px"` saíam borradas. Agora são duas colunas, com `sizes` correto.

**Validação em 31/10/2026:**

| # | Situação |
|---|----------|
| A1, A2 | ✅ `dotnet build` com 0 avisos e 0 erros (com `--artifacts-path`); `dotnet format` sem alterações |
| A3, A4 | ✅ lint, typecheck e 81 testes do frontend |
| A5 | ✅ `npm run build` contra o stub |
| Detector de design (`impeccable detect`) | ✅ nada na tela de fotos. 4 avisos leves fora dela, em `page.tsx`, `globals.css` e `Monograma.tsx`: valores fora do `DESIGN.md` |
| Testes .NET sem banco | ✅ Domain 63, Arquitetura 8, Application 32, `ImageProcessorTests` 11 (HEIC, GPS removido, orientação, arquivo falso, redução) |
| Testes funcionais (`AdminProductImageTests`) | ⏳ **escritos e compilando, não executados**: o Docker estava desligado. Eles usam um armazenamento falso, então não exercitam o S3 |
| Navegador contra o stub (375 px e 1024 px) | ✅ ativar sem foto recusado com mensagem; envio de fotos; arquivo falso e `.gif` recusados com motivo sem parar os outros; aviso de foto pequena; descrição salva e mantida ao recarregar; sem rolagem horizontal |
| Reordenar | ✅ teclado e arrasto com ponteiro mudam a ordem e a capa, e a ordem se mantém ao recarregar. **Ressalva:** o teste foi por eventos simulados, porque o navegador de teste oculto dispara `visibilitychange` a cada 2 s e o dnd-kit cancela o arrasto nesse evento. Mouse e dedo de verdade ainda não foram usados |
| C8 | ⏳ precisa de iPhone, MinIO e `exiftool`. O processador já foi testado com HEIC, mas o caminho completo não |
| C9, C10 | ✅ contra o stub. ⏳ contra a API real |
| Câmera ("Tirar foto") | ⏳ não testada: precisa de um celular de verdade |
| `S3ImageStorage` com MinIO ou R2 | ⏳ **nunca executado** |
| Magick.NET na imagem *chiseled* | ⏳ **não verificado** (risco da seção 8). A primeira subida da API por container precisa enviar uma foto |

**Para testar de verdade:** `docker compose up -d postgres mailpit rustfs rustfs-init`, depois `dotnet test`. A loja em produção precisa de `NEXT_PUBLIC_IMAGES_URL` no build, igual a `ImageStorage:PublicBaseUrl` da API.

**Pontos em aberto:** peça cadastrada pelo painel segue esgotada até a Fase 3. Capa de coleção e imagem de ocasião (também RF-061) entram na 2B.4.

### 2B.4 — Coleções e vitrine (implementada em 31/10/2026, validada contra banco e API reais em 02/11/2026)

**O que entrou:**

| Camada | Peças |
|--------|-------|
| Banco | `V0011` (`collections`, `product_collections`, `slug_history` aceita `Collection`); `V0012` (`showcase_featured` de linha única, `occasions` com `EXCLUDE USING gist` contra período sobreposto, `occasion_products`); seed `S0004` |
| Domínio | `Collection` (com `CardTheme`), `Occasion`, `IShowcaseFeaturedRepository` |
| API pública | `catalog/collections`, `catalog/collections/{slug}` (301 de slug antigo), `catalog/showcase`, `?collection=` em `catalog/products` e `catalog/filters`, `categories` em `catalog/filters`, `collections` no sitemap |
| API do painel | `admin/collections` (cadastro, status, peças em lote, capa), `admin/showcase/featured`, `admin/occasions` (cadastro, imagem); filtro `collectionId` na lista de produtos; `collectionIds` no produto |
| Cache e páginas | Tags `collections` e `showcase` no HybridCache e no `/api/revalidate`; escrita do painel passa pelo `CatalogChanged` |
| Painel | `/admin/colecoes`; `/admin/vitrine` (destaque e ocasiões, com peças em ordem); seleção em lote e filtro por coleção na lista de produtos; seção "Coleções" no editor |
| Loja | `/colecao/[slug]` com categoria como primeiro filtro; cartela grafite com folha prateada; etiquetas com as coleções; home com destaque, ocasião e até 3 coleções |

**Decisões tomadas na implementação:**
- **Scripts separados** (`fase-2b4-*.sql`), para não travar quem já rodou os da 2B.
- **As coleções vêm antes das categorias nas etiquetas.** São poucas, e no celular as últimas ficam fora da tela. A etiqueta de coleção grafite já aparece em grafite.
- **Ordem da coleção é um número no formulário**, como nos tamanhos, em vez de arrastar: são poucas coleções.
- **Peças da ocasião sobem e descem por botões**, que já funcionam com teclado e toque, sem depender de arrastar numa lista dentro do formulário.
- **A virada da ocasião depende do TTL, sem job (RF-025 CA3):** 5 minutos no cache da API mais o ISR da home (o build mostra 2 minutos, o menor valor entre as buscas da página). Na pior hipótese, a ocasião entra ou sai cerca de 7 minutos depois da meia-noite.
- **O fuso de Brasília é calculado pelo Postgres**, para não depender do `tzdata` na imagem *chiseled*.
- **O painel avisa quando o destaque não pode aparecer** (fora da loja ou sem estoque), porque a home troca por outra peça em silêncio (RF-025 CA1).

**Correção da revisão no navegador:** a coluna do formulário ficava reservada mesmo fechada e cortava a tabela em 1280 px. Em Coleções, Vitrine e Categorias, ela agora só existe com o formulário aberto e em telas muito largas; nas demais, o formulário abre abaixo da tabela.

**O que ficou de fora, de propósito:**
- **Etiquetas "Novidades" e "Presente" e a página de Presente** (arquivo 26 §4.1 e §4.4): a página depende do fluxo de presente da Fase 4. Por isso o RF-025 CA5 só revalida a home por enquanto.
- **"Adicionar à sacola" na peça em destaque:** segue "Ver a peça" até a sacola existir (Fase 3).

**Validação em 31/10/2026:**

| # | Situação |
|---|----------|
| A1, A2 | ✅ `dotnet build` com 0 avisos e 0 erros (com `--artifacts-path`); `dotnet format` sem alterações |
| A3, A4 | ✅ lint, typecheck e 102 testes do frontend (21 novos) |
| A5 | ✅ `npm run build` contra o stub, com `/colecao/[slug]`, `/admin/colecoes` e `/admin/vitrine` |
| Detector de design | ✅ nada nas telas novas; os mesmos 4 avisos leves de antes, fora delas |
| Testes .NET sem banco | ✅ Domain 73 (10 novos), Arquitetura 8, Application 32, `ImageProcessorTests` 11 |
| Testes funcionais (`AdminCollectionTests`, `AdminShowcaseTests`) | ⏳ **escritos e compilando, não executados**: o Docker estava desligado. Até rodarem, **nenhuma linha de SQL nova foi executada** |
| B1–B3 | ⏳ rodar no DBeaver, incluindo os `fase-2b4-*.sql` |
| C11 | ✅ contra o stub: coleção criada, peças em lote, cartelas grafite em `/colecao/eles`, sitemap. ⏳ contra a API real |
| C12 | ✅ contra o stub: a API devolve o novo destaque e o acabamento. ⏳ **home atualizada em segundos não testada**: o stub não chama o `/api/revalidate` |
| C13 | ✅ contra o stub: período sobreposto recusado com mensagem; datas livres aceitas. ⏳ contra a API real |
| C14 (coleção) | ⏳ slug antigo só tem teste funcional; o stub não guarda histórico |
| Navegador contra o stub (375 px e 1280 px) | ✅ home (nome, preço e ação na primeira tela do celular), coleção com filtro de categoria e capa, `noindex` com filtro, canonical sem filtro; no painel, validação, nome repetido, capa (arquivo falso e PDF recusados), lote, destaque, ocasião e coleções no editor. Sem rolagem horizontal e sem erro no console |
| Capa e imagem de ocasião com `S3ImageStorage` | ⏳ nunca executado, como na 2B.3 |

**Pontos em aberto:**
- **Duas ocasiões salvas ao mesmo tempo** com período sobreposto: o banco recusa a segunda, mas a resposta é 500, não 409. É raro, com uma administradora só.
- **Com o estoque zerado até a Fase 3, peça cadastrada pelo painel não pode ser destaque de verdade.** A home mostra a novidade com estoque, e o painel avisa.

### Validação real (02/11/2026): banco, API, bucket, loja e container

Com o Docker ligado, a 2B rodou de ponta a ponta. A validação achou **seis defeitos que o stub e os testes sem banco não mostravam**, todos corrigidos:

| # | Defeito | Efeito | Correção |
|---|---------|--------|----------|
| 1 | Timeout do `HttpClient` tratado como desligamento no `OutboxProcessor` | **A API inteira parava** quando uma chamada do Outbox estourava o tempo (Next lento, Resend, R2). Existe desde a 2A | Só o token do host indica parada. Teste novo `Outbox_WhenProviderTimesOut_RetriesLaterAndKeepsProcessing`, visto falhando sem a correção |
| 2 | Imagem `noble-chiseled` sem ICU | **A API não subia em container**, e o slug perdia as letras acentuadas (`cora-o-a-es-an-is`) | Base `aspnet:10.0-noble-chiseled-extra`. Slug agora sai `coracao-acoes-aneis` |
| 3 | `loading.tsx` abria a resposta com 200 | Slug antigo e página inexistente em `/colecao` e `/categoria` **não respondiam redirect nem 404** (C14) | A decisão foi para um `layout.tsx` acima do limite de carregamento; o esqueleto continua |
| 4 | `<select>` de categoria nascia sem as opções | O editor mostrava **"Escolha" em peça com categoria** | O valor é reaplicado quando as opções chegam |
| 5 | `minio/minio` e `minio/mc` negados nos registros | `docker compose up` falhava | **RustFS** (`rustfs/rustfs:1.0.4`) e `amazon/aws-cli` para criar o bucket. A API não mudou: é o mesmo cliente S3 |
| 6 | Corpo inválido respondia 500 em Development | Erro enganoso ao depurar | O handler global respeita o status de `BadHttpRequestException` |

Também foram corrigidos **três problemas dos próprios testes**, que nunca tinham rodado inteiros: `AuthClient` fechava o stream da resposta (50 falhas), a lista de rotas públicas estava desatualizada e o `TRUNCATE` do Respawn entrava num impasse invisível ao Postgres com o Outbox, gerando timeouts de 30 s intermitentes. Sem esse último, 4 rodadas seguidas da suíte passaram.

| # | Resultado |
|---|-----------|
| A1, A2 | ✅ 0 avisos e 0 erros; formatação sem alterações |
| A3, A4 | ✅ lint, typecheck e 102 testes do frontend |
| A5 | ✅ `npm run build` |
| B1 | ✅ os 9 scripts, do zero, em Postgres 17 |
| B2 | ✅ repetir para com "Script já aplicado" |
| B3 | ✅ coleções Eles e Prata 925, destaque e ocasião. Depois deles, o `ECommerce.Migrator` responde "No new scripts", o que confirma que batem com o DbUp |
| C1 | ✅ .NET: Domain 73, Application 32, Arquitetura 8, Funcionais 151 |
| C5, C6, C10, C15 | ✅ testes funcionais; C10 e C15 também na API real, e o cliente em `/admin` cai na home |
| C8 | ✅ parcial: HEIC com GPS sai em WebP sem GPS no bucket real, na API local e no container. ⏳ falta foto de um iPhone de verdade |
| C9 | ✅ pelo painel: lote com foto boa, foto pequena (com aviso) e arquivo falso, recusado sem parar as outras |
| C11 | ✅ coleção, lote de 3, página com cartelas grafite, sitemap em 4,6 s |
| C12 | ✅ **build de produção: home muda em 3,3 a 4,2 s** |
| C13 | ✅ sobreposição responde 409; datas livres são aceitas |
| C14 | ✅ coleção, categoria e produto na loja. **O status é 308**, o redirect permanente do Next; a API responde 301. O Google trata os dois igual |
| Remoção do bucket (RF-061 CA4) | ✅ foto e capa somem pelo Outbox; bucket vazio depois da limpeza |
| 404 real | ✅ `/colecao`, `/categoria` e `/produto` inexistentes |

**Ainda não verificado:**
- **Só com celular de verdade:** brilho de folha, câmera do leitor (C7), "Tirar foto", foto HEIC de um iPhone (C8), arrastar fotos com o dedo.
- **R2 de verdade:** só o RustFS foi exercitado. O R2 exige `DisablePayloadSigning` (o código já faz isso em HTTPS), caminho que ainda não rodou.
- **Lighthouse (C3)** não foi repetido depois da nova home; o CI cobre.

## 8. Riscos e pontos de atenção

| Ponto | Detalhe |
|-------|---------|
| Limite de upload no Cloudflare | O plano grátis aceita até 100 MB por requisição. Por isso **cada foto sobe numa requisição própria** (máx. 15 MB), e não o lote inteiro |
| Magick.NET na imagem *chiseled* | **Validado em 02/11/2026:** HEIC vira WebP sem GPS dentro do container. A imagem da API passou a ser `noble-chiseled-extra`, que traz ICU |
| HEIC e licença | O suporte a HEIC do Magick.NET vem da libheif, com codecs que têm questões de patente. Para a escala da loja o risco é baixo, mas fica registrado |
| URL pública do R2 | O `r2.dev` tem limite de taxa e não serve para produção; staging pode usar, produção precisa de domínio próprio (arquivo 20) |
| Novo visual sem fotos reais | Com as fotos ilustrativas do seed a direção perde força; avaliar o visual de verdade só depois das fotos da loja |
| Fila do Outbox é serial | Com a loja fora do ar, cada `catalog.changed` espera a falha de conexão (4 s) ou o timeout (10 s) antes da próxima mensagem, atrasando a remoção de fotos e os e-mails. O backoff tenta 5 vezes em cerca de 7,5 minutos; depois disso a revalidação desiste, e a página se corrige sozinha quando o ISR vence (o cache da API já foi limpo na primeira tentativa). A transação da mensagem fica aberta durante a chamada externa. Vale rever antes do lançamento (Fase 6) |
| Tamanho da entrega | Se uma fatia passar de uma semana de trabalho, ela vira entrega própria (ex.: 2B.4 como 2C) |

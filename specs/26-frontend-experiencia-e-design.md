# 26 — Frontend: experiência e direção visual

**Status:** Em revisão (iniciada em 29/10/2026; direção visual e decisões da §8 respondidas pela cliente na mesma data)

Complementa o arquivo 16, que trata da arquitetura do frontend. Este arquivo define **como a loja parece e se comporta**: direção de arte, linguagem de componentes, comportamento de cada tela e o que ainda falta decidir. A verdade de produto (público, posicionamento, marca) fica em `e-commerce-frontend/PRODUCT.md`.

> **Correção de escopo.** As specs anteriores falam em "roupas". A loja é a **MO Acessórios**: **semijoias, acessórios e bolsas**, para masculino e feminino. O modelo de variação `cor × tamanho` continua genérico de propósito, para a loja poder vender roupas no futuro (ver §6, ADR-F06).

## 1. Quem chega e o que precisa fazer

| Persona | Situação | O que precisa |
|---------|----------|---------------|
| **Cliente comprando para si** | Celular, vindo do link da bio do Instagram ou do Google, olhando uma peça específica | Ver a peça de perto, entender o **acabamento** (dourado, prateado, ródio, prata 925) e o **tamanho** (aro, comprimento), confiar no preço, comprar com PIX sem falar com ninguém |
| **Cliente comprando presente** | Perto de uma data (Dia das Mães, dos Pais, dos Namorados, Natal), muitas vezes sem saber o tamanho | Achar ideias por ocasião e por pessoa (ela / ele), escolher sem errar o tamanho e mandar já pronto para presentear |
| **Cliente da linha masculina** | Público raro no segmento; hoje é a linha "Eles" do Instagram | Uma loja que não pareça feita só para mulheres |
| **Administradora** | Computador ou celular, cadastrando fotos e estoque, separando pedidos | Velocidade, leitura rápida e nenhum enfeite no caminho (modo *Operate*) |

**Sucesso:** a cliente encontra a peça, entende acabamento e tamanho, conclui a compra no celular sozinha e, se quiser, chama a MO no WhatsApp a um toque, sem sair do que estava fazendo.

## 2. Direção visual: **Cartela na vitrine rosé**

> **Histórico.** A direção sorteada e escolhida em 29/10/2026 foi "Cartela & Expositor": cartelas penduradas em ganchos sobre feltro verde-garrafa, com Bodoni. Depois de ver a loja montada, a cliente comparou opções na própria loja (página de estudo temporária, já removida) e revisou três pontos no mesmo dia: o **verde** não combinava com a marca, o **gancho/cabide** não agradou, e as **letras finas da Bodoni** ficavam estranhas no fundo claro. Escolha final: **rosé claro, brilho de folha, Prata e texto preto**. A tese da cartela continua.

### 2.1 Tese

Toda semijoia de boutique chega numa **cartela**: o cartãozinho marfim com a marca. **Na loja online, cada produto é uma cartela da MO apoiada numa vitrine rosé, e ela brilha como metal quando o olhar passa por ela.**

A direção recusa o padrão da categoria: foto grande de modelo, fundo creme, serifada e grade de cards brancos sem matéria. A identidade que a MO já tem (monograma "MO" em Didone, estrela dourada, marfim, o rosé e o dourado das fotos do Instagram) vira **material**: cartão marfim com "MO" em relevo seco, sobre uma vitrine rosé.

### 2.2 Mundo próprio (reconhecível mesmo sem conteúdo)

| Papel | Material | Valor (implementado, contraste conferido) |
|-------|----------|-------------------------------------------|
| **Vitrine** (fundo das páginas de loja) | Rosé claro com trama fraca de papel | `#E9CFCB`; rosé fundo `#D4B2AD` |
| **Cartela** (superfície de produto e de leitura) | Cartão marfim, a cor que a marca já usa | `#F4EEE3`, sombra tingida de rosé |
| **Folha** (estado ativo) | Dourado: `#9C7A3C` sobre a cartela, bronze `#7E6128` sobre a vitrine | 3,5:1 e 3,9:1 como indicador; marfim sobre o bronze, 5:1 |
| **Tinta** (texto) | Preto | `#1A1A17` (15:1 na cartela, 11,8:1 na vitrine) |
| **Erro** | Única cor fora do mundo | `#8A2C1F` (7,4:1) |
| **Cartela "Eles"** | Cartão grafite com folha prateada | `#2E2E2C` / `#C9CCCF` (marfim sobre grafite, 11:1; prata, 8:1) |

**Regras do mundo:**

1. **Tintas fixas.** Só existem vitrine, cartela, folha e tinta, mais a cor de erro e os pares grafite/prata da linha masculina. Sem cinzas decorativos e sem gradientes de cor. Sucesso usa tinta.
2. **A folha dourada é lei de estado.** Ela marca só o que está **selecionado ou ativo**: o acabamento escolhido, a categoria atual, a página atual. Nunca decora. **Dourado nunca carrega texto sobre marfim**; texto é sempre tinta (ou marfim sobre o bronze).
3. **A ausência é desenhada.** Acabamento esgotado aparece como **furo vazio** (amostra pontilhada) e aro esgotado aparece riscado, os dois anunciados como "esgotado"; nunca somem. A vitrine vazia é uma cartela com os dois furos vazios.
4. **Hierarquia só por escala de tipo.** Nada de selos "NOVO", "-15%" ou "MAIS VENDIDO" empilhados nas cartelas.
5. **Uma luz só.** Toda foto de produto segue a mesma direção de luz natural lateral (guia de fotografia, §7.2).

### 2.3 Tipografia

| Voz | Uso | Face |
|-----|-----|------|
| **Didone** | Nome da peça, títulos, preço em destaque. Ecoa o monograma "MO" | **Prata** (Google Fonts): Didone desenhada para tela, com traços finos firmes o bastante para o fundo claro. Substituiu a Bodoni Moda |
| **Geométrica** | Interface, textos, botões, caixa alta espaçada como o "ACESSÓRIOS" do logo | Jost |
| **Dígitos tabulares** | Preço, aro, comprimento, quantidade | `font-variant-numeric: tabular-nums` |
| **Manuscrita** | **Só** na prévia da dedicatória de presente (§4.4) | A definir; nunca em navegação nem em preço |

As fontes são carregadas com `next/font` (self-hosted, sem CLS).

### 2.4 Linguagem de componentes

| Componente | No mundo | Comportamento |
|------------|----------|---------------|
| `Cartela` | O cartão de produto | Cartão marfim, foto da peça, nome em Prata, preço tabular, "MO" em relevo seco no rodapé. A cartela da vez é larga do tablet em diante. Grafite na linha "Eles" |
| `Vitrine` (grade) | Grade de produtos sobre o rosé | No celular, 2 colunas; reflui sempre em cartelas inteiras |
| `Etiqueta` | Navegação: categorias e, na 2B.4, coleções (**Eles**, **Prata 925**) e **Presente** | Etiqueta marfim com ponta; a ativa leva o bronze, com texto marfim |
| `Seletor de acabamento` | Os banhos | Amostra do metal + **nome escrito** (WCAG). Esgotado = furo vazio |
| `Seletor de aro/comprimento` | Furos numerados | Dígitos tabulares; esgotado = riscado; link "Descubra seu aro" |
| `Sacola` | Saquinho de juta da MO | Contador no topo (Fase 3) |
| `Botão primário` | Cartão tinta | Um por tela; marfim sobre tinta, hover em grafite |
| `Falar com a MO` | Atendimento próximo | Botão fixo que abre o WhatsApp com mensagem pré-preenchida (nome e link da peça, nunca dados pessoais) |

### 2.5 Interação assinatura

**Brilho de folha.** A cartela inclina levemente na direção do mouse e uma luz passa por ela, como reflexo em metal, com um fio de folha na borda. Na página do produto a inclinação é menor. **A cartela vira:** a frente mostra a peça e o verso traz detalhes, garantia e, no fluxo de presente, a dedicatória.

Com `prefers-reduced-motion` a cartela não inclina e não gira (troca imediata); a luz continua, porque é só opacidade. No toque, a luz aparece sem inclinar.

### 2.6 Risco honesto

- **Depende das fotos.** A cartela pede a peça fotografada sobre fundo neutro, com a mesma luz (§7.2). Com foto de catálogo comum, vira uma grade com moldura.
- **Rosé claro e cartela marfim são próximos** (1,3:1). Quem separa a cartela do fundo é a sombra tingida; não remover a sombra.

## 3. Modos por área

| Área | Modo | Quanto do mundo aparece |
|------|------|-------------------------|
| Home, linha, categoria, produto, busca | **Persuade** | Tudo: vitrine rosé, cartelas, brilho de folha, cartela que vira |
| Sacola e checkout | **Persuade → Operate** | Cartelas na sacola e no resumo; os formulários são marfim liso, sem a vitrine rosé, com um passo por tela |
| Conta (login, cadastro, pedidos, endereços) | **Operate** | Superfície marfim, títulos em Didone, pedidos como cartelas pequenas |
| Admin | **Operate** | Mínimo: marfim, tinta, tinta também como cor de ação; furo vazio para esgotado e tags de SKU em forma de cartela. Densidade e velocidade antes de expressão |

## 4. Telas

Cada tela abaixo é intenção de composição e comportamento, não CSS. Os critérios de aceite viram testes (Testing Library e Playwright).

### 4.1 Home (`/`): a vitrine da entrada

**Primeira tela no celular (390 × 844):**
1. Topo: monograma "MO" à esquerda, busca e sacola à direita; abaixo, as etiquetas **Novidades · Eles · Prata 925 · Presente**.
2. **Uma cartela grande**, a **peça em destaque escolhida pela loja no admin** (RF-025), com nome, preço e **"Adicionar à sacola" funcionando ali mesmo** (o acabamento padrão já vem escolhido; o aro, quando existir, abre o seletor).
3. Abaixo, começa a vitrine: duas colunas de cartelas (novidades).

Depois da primeira tela, o ritmo alterna denso e calmo: categorias em lista (Pulseiras, Anéis, Colares, Brincos, Bolsas), depois uma faixa calma de **ocasião** (a data comemorativa da vez, escolhida no admin, RF-025), depois a coleção **Eles** em cartelas grafite, com o mesmo tamanho e destaque do resto, e a coleção **Prata 925**. Fecha com o rodapé: atendimento, trocas, frete, Instagram, WhatsApp.

**Critérios:**
- Dado um visitante no celular, quando a home carrega, então nome, preço e ação da peça em destaque estão na primeira tela, sem rolar.
- A coleção "Eles" nunca aparece em tamanho menor que as outras vitrines.
- Sem peça em destaque cadastrada (ou com ela esgotada), a primeira cartela é a novidade mais recente, sem espaço vazio. Sem ocasião ativa, a faixa de ocasião não aparece.

### 4.2 Categoria e coleção (`/categoria/[slug]`, `/colecao/[slug]`)

- **Categoria** é onde a peça mora (Anéis, Colares, Bolsas...). **Coleção** atravessa categorias: um anel masculino de prata está em Anéis, em **Eles** e em **Prata 925** (RF-024). As duas páginas usam a mesma vitrine; na coleção, a primeira etiqueta de filtro é a categoria.
- Na coleção **Eles**, todas as cartelas são grafite com folha prateada.
- Vitrine com filtros como **etiquetas**: acabamento, faixa de preço e, quando a categoria tiver, aro ou comprimento. "Ordenar" fica num menu.
- Filtros com rótulos da categoria (§6): em Anéis, "Aro"; em Colares, "Comprimento"; em Bolsas, "Cor" e "Tamanho".
- Paginação como hoje (SEO), com "Ver mais cartelas" no fim da fileira.
- **Estados:** sem resultado com filtros → cartela vazia com os furos à mostra, "Nenhuma peça com esses filtros" e "Limpar filtros". Categoria vazia → mensagem e link para Novidades.

### 4.3 Produto (`/produto/[slug]`)

**Primeira tela no celular:**
1. A **cartela da peça** ocupa a largura toda: foto na cartela, galeria por deslize horizontal (fotos do acabamento escolhido), e o botão "Virar" leva ao verso.
2. Nome em Didone e preço em dígitos tabulares. O componente de preço já nasce preparado para "ou R$ Y no PIX", mas só mostra essa linha se a loja decidir dar desconto no PIX (D-F04).
3. **Seletor de acabamento** (amostras com nome) e **seletor de aro/comprimento** (furos numerados).
4. **"Adicionar à sacola"** fixo no rodapé da tela enquanto se rola.
5. Abaixo: simulador de frete por CEP, "É presente?" (§4.4) e "Tirar dúvida no WhatsApp".

**Verso da cartela:** descrição, material e banho, medidas, **cuidados** e a **garantia: "até 6 meses no banho, com o uso correto"**. A frase "uso correto" sempre leva aos cuidados, porque é deles que a garantia depende. Para anéis, o **guia "Descubra seu aro"** (régua com fio ou medida de um anel que já tem, em texto e ilustração, sem prometer precisão que a loja não garante).

**Critérios:**
- Acabamento e aro sempre anunciados por texto (WCAG). Esgotado é anunciado como "esgotado" e não pode ser selecionado.
- Trocar o acabamento troca as fotos e atualiza a URL (`?cor=&tamanho=`), como hoje.
- Sem aro escolhido, "Adicionar à sacola" mostra "Escolha o aro" e leva o foco ao seletor, sem erro vermelho.

### 4.4 Presente: caminho de primeira classe

- Etiqueta **Presente** no topo: vitrine da **ocasião** ativa (RF-025) e **para quem** (para ela / para ele, este último usando a coleção Eles), com faixas de preço.
- Na peça: **"É presente?"** vira a cartela e mostra no verso a **prévia da dedicatória** escrita na face manuscrita, como um cartão de verdade. A dica de aro fica junto: "Não sabe o aro? Prefira pulseira, colar ou brinco".
- O pedido aceita **embalagem para presente** e **dedicatória** (RF-047, decidido em 29/10/2026). A dedicatória digitada na peça segue para a sacola e pode ser editada na revisão do checkout (§4.6).
- **Critérios:**
  - Dado "É presente?" ligado, quando o cliente digita a mensagem, então a prévia no verso da cartela atualiza enquanto ele digita e mostra quantos caracteres restam.
  - O pedido confirmado, o e-mail de pedido e a tela de pedido no admin mostram que é presente e trazem a dedicatória.
  - A dedicatória nunca aparece na nota de conteúdo nem em nada que acompanhe o preço dentro da embalagem.

### 4.5 Sacola (`/carrinho`)

- Lista de **cartelas pequenas**: foto, nome, acabamento, aro, quantidade e preço. Remover tem "desfazer" por alguns segundos.
- Frete por CEP com PAC/SEDEX e a regra de frete grátis ("faltam R$ X para frete grátis", quando houver regra).
- "Finalizar compra" sem login leva a login/cadastro e volta com a sacola mesclada (arquivo 16 §5).
- **Estados:** sacola vazia → saquinho de juta vazio e "Ver novidades"; item que esgotou → cartela com furo vazio e aviso, sem deixar seguir com ele.

### 4.6 Checkout (`/checkout`)

Um passo por tela, com a cartela-resumo sempre recolhível no topo: **Endereço → Envio → Revisão → Pagamento → Confirmação**.

- Endereço: CEP primeiro, com autocompletar.
- Revisão: bloco **Presente** com "Embalar para presente" e a dedicatória (preenchida se veio da peça). O valor da embalagem, se houver, aparece no total (D-F08).
- **PIX:** QR grande, "Copiar código" com confirmação, contagem do prazo (48 h, arquivo 25) e atualização automática quando o pagamento cai.
- Cartão: componente da plataforma de pagamento (arquivo 21), com o mínimo de moldura possível em volta.
- **Confirmação:** o momento de encantamento: a cartela "vai para o saquinho" (uma animação só), o número do pedido e o próximo passo.

### 4.7 Conta (`/login`, `/cadastro`, `/conta/...`)

- Marfim liso, um campo por linha, títulos em Didone e rótulos sempre visíveis (sem placeholder no lugar de rótulo).
- **Meus pedidos:** cada pedido é uma cartela pequena com status em texto ("Aguardando pagamento — 31 h restantes", "Enviado — rastrear").
- As telas da Fase 2A recebem o novo visual sem mudar o comportamento.

### 4.8 Admin (`/admin/...`)

Modo **Operate**: tabelas densas, filtros visíveis e ações por linha. A marca aparece só nos detalhes: SKU em tag de cartela, esgotado como furo vazio, tinta como cor de ação.

- **Produto:** grade de variações acabamento × aro/comprimento editável como planilha, com estoque por célula e foto por acabamento.
- **Estoque:** foco no leitor de código de barras (arquivo 16 §4). A leitura aciona um som e destaca a variação.
- **Vitrine (RF-025):** escolher a **peça em destaque** da home e a **ocasião da vez** (nome, texto curto, período de exibição e as peças da ocasião). Ocasião fora do período some sozinha.
- **Coleções (RF-024):** criar coleções (Eles, Prata 925...) e marcar produtos nelas, inclusive em lote a partir da lista de produtos.
- **Categorias:** os rótulos de variação ("Acabamento", "Aro"...) são editados no cadastro da categoria (ADR-F07).
- **Pedidos:** pedido de presente marcado na lista; a dedicatória aparece em destaque na separação.

## 5. Estados transversais

| Estado | Tratamento |
|--------|------------|
| Carregando | Esqueleto **em forma de cartela** (furos e moldura), nunca spinner no meio da tela |
| Erro de rede | Mensagem em tinta, "Tentar de novo" e link do WhatsApp |
| 404 | Vitrine rosé: "Essa página saiu da vitrine", mais busca e novidades |
| 429 | Aviso com o tempo de espera (arquivo 16 §7) |
| Esgotado | Furo vazio, anunciado por texto |
| Texto longo | Nome da peça em até 2 linhas na cartela pequena; inteiro na página do produto |

## 6. Variações: rótulos por categoria

| ADR | Decisão |
|-----|---------|
| ADR-F05 | **Direção visual "Cartela na vitrine rosé"** (§2): sorteada como "Cartela & Expositor" e revisada pela cliente em 29/10/2026 (rosé claro, brilho de folha, Prata, texto preto). |
| ADR-F06 | O backend mantém **`cor × tamanho` genérico**. A **tela** usa rótulos por categoria: semijoias → "Acabamento" + "Aro" ou "Comprimento"; bolsas → "Cor" + "Tamanho". Assim a loja pode vender roupas no futuro sem mudar o modelo. |

| ADR-F07 | Os rótulos ficam **na categoria**: colunas `color_label` e `size_label` em `catalog.categories`, editáveis no admin e expostas pela API de categoria e de produto. Subcategoria sem rótulo herda o da categoria pai; sem nenhum, a tela usa "Cor" e "Tamanho". (Decidido em 29/10/2026.) |
| ADR-F08 | **Eles** e **Prata 925** são **coleções**, não categorias: um produto mora numa categoria e pode estar em várias coleções (RF-024). (Decidido em 29/10/2026.) |

## 7. Requisitos de qualidade

### 7.1 Herdados do arquivo 16 §8
WCAG 2.1 AA, Lighthouse ≥ 90 (performance e acessibilidade) e ≥ 95 (SEO), mobile-first.

### 7.2 Novos
- **Contraste:** nenhum texto em dourado sobre marfim; o par tinta/rosé, o bronze sobre o rosé e o par grafite/prata são validados com ferramenta antes do merge.
- **Fotografia (guia para a loja):** peça na cartela real ou recortada em fundo neutro, sempre a mesma luz natural lateral, uma foto por acabamento, proporção da cartela e pelo menos 1600 px no lado maior. Bolsas penduradas pela alça.
- **Movimento:** só a interação assinatura e a confirmação de pedido; tudo respeita `prefers-reduced-motion`.
- **Toque:** alvos de pelo menos 44 × 44 px; o botão de compra fica ao alcance do polegar.
- **LCP:** a foto da primeira cartela é `priority`; as fontes saem de `next/font`.

## 8. Decisões

### 8.1 Decididas em 29/10/2026

| # | Pergunta | Decisão | Onde |
|---|----------|---------|------|
| D-F01 | Onde guardar os rótulos de variação | Na categoria | ADR-F07, RF-050 |
| D-F02 | Mensagem e embalagem para presente | **Sim, as duas** | RF-047, §4.4, §4.6 |
| D-F03 | Peça em destaque e ocasião editáveis | **Sim, pelo admin** | RF-025, §4.1, §4.8 |
| D-F05 | "Eles" e "Prata 925" | **Coleções** | ADR-F08, RF-024 |
| D-F07 | Garantia | **Até 6 meses no banho, com uso correto.** Trocas e devoluções seguem no mínimo o direito de arrependimento de 7 dias do CDC (art. 49), que vale para toda venda online | §4.3, rodapé, página de trocas |

### 8.2 Ainda em aberto

| # | Pergunta | Por que importa |
|---|----------|-----------------|
| D-F04 | Há desconto no PIX? E de quanto? | Muda a linha de preço (§4.3) e o total no checkout. O componente já nasce preparado |
| D-F06 | Arquivo vetorial do logo (SVG) e fotos no padrão da §7.2 | Sem o logo não há monograma; sem as fotos a direção perde força |
| D-F08 | A embalagem para presente é cobrada? Quanto? | Total do pedido e texto do checkout |
| D-F09 | Tamanho máximo da dedicatória e como ela vai: escrita à mão pela loja ou impressa? | Limite do campo (proposta: 200 caracteres) e prévia no verso da cartela |
| D-F10 | O que conta como "uso correto" para a garantia (cuidados oficiais da loja) | Texto dos cuidados no verso da cartela, ao qual a garantia remete |

### 8.3 Requisitos novos (incluídos no arquivo 01 em 29/10/2026)

As decisões acima criaram requisitos no backend, já escritos com critérios de aceite no arquivo 01 e com rotas no arquivo 07. O upload de imagens (RF-061) já estava aprovado (arquivo 20, D06), mas **ainda não foi implementado**. Ele foi detalhado agora porque produto, coleção e ocasião dependem dele.

| ID proposto | Requisito | Fase sugerida |
|-------------|-----------|---------------|
| RF-024 | **Coleções**: CRUD no admin, produto em N coleções, listagem pública por coleção com os mesmos filtros da categoria, URL própria, sitemap | 2B (admin de catálogo) |
| RF-025 | **Vitrine**: peça em destaque da home e ocasião da vez (nome, texto, período, produtos), editáveis no admin; endpoint público em cache | 2B |
| RF-047 | **Presente no pedido**: `is_gift`, `gift_wrap` e `gift_message` no pedido, congelados no checkout; aparecem no e-mail e no admin | 4 (checkout) |
| RF-050 (ampliado) | Rótulos de variação (`color_label`, `size_label`) no CRUD de categorias, com herança da categoria pai | 2B |
| RF-061 | **Upload de imagens** (JPEG/PNG/WebP/HEIC, até 15 MB, até 10 por vez, sem EXIF/GPS, WebP no R2) para produto, coleção e ocasião; produto sem imagem não ativa (RF-051 CA3) | 2B |

## 9. Ordem de implementação proposta

Os passos 1 a 3 formam a entrega da Fase 2B: [entregas/fase-2b-painel-de-catalogo.md](entregas/fase-2b-painel-de-catalogo.md).

1. **Fundação:** tokens (tintas, tipos, espaçamento), fontes, `Cartela`, `Brilho`, vitrine, `Etiqueta`, seletores e estados (§5). Também troca o `SITE_NAME` "Loja" por "MO Acessórios" e ajusta os dados de exemplo do seed para semijoias.
2. **Novo visual nas telas existentes** (Fases 1 e 2A): home, categoria, produto, busca, login/cadastro/conta, sem mudar comportamento.
3. **Admin de catálogo (2B)** já no modo Operate da §4.8, junto com RF-024 (coleções), RF-025 (vitrine) e os rótulos de variação. A home e a navegação passam a usar coleções e vitrine nesta fase.
4. As fases 3 a 5 usam os componentes prontos (sacola, checkout, pedidos). O fluxo de presente (RF-047) entra no checkout da Fase 4.

Ao fim do passo 2, a direção passa por uma revisão de acabamento (celular 390 px e desktop 1440 px) e o design system construído é documentado em `e-commerce-frontend/DESIGN.md`.

# 01 — Requisitos Funcionais

**Status:** Em revisão

Formato: cada requisito tem ID, ator, descrição e critérios de aceite (CA). Os CAs viram testes funcionais (ver arquivo 15).

---

## 1. Identidade e Conta

### RF-001 — Cadastro de cliente
**Ator:** Visitante
O visitante cria uma conta informando nome, e-mail, senha, telefone e (opcional) CPF.

- **CA1** Dado e-mail não cadastrado, quando enviar dados válidos, então a conta é criada com perfil `Customer`, o usuário já fica logado e recebe o e-mail de boas-vindas/confirmação (EM-01). Retorna `201`.
- **CA2** Dado e-mail já cadastrado, então retorna `409 Conflict` (mensagem genérica).
- **CA3** Senha **DEVE** ter no mínimo 8 caracteres, com letra maiúscula, minúscula e número; caso contrário `400`.
- **CA4** E-mail é normalizado (trim + lowercase) antes de persistir.
- **CA5** Não é possível se cadastrar como `Admin` por este endpoint.

> CPF pode ser exigido pela plataforma de pagamento (PIX/boleto) — nesse caso é solicitado no checkout, se ainda não informado.

### RF-002 — Login
- **CA1** Credenciais válidas → `200` com access token (JWT) no corpo e refresh token em cookie `HttpOnly`.
- **CA2** Credenciais inválidas → `401` com mensagem genérica.
- **CA3** Após 5 falhas consecutivas, a conta fica bloqueada por 15 minutos. Ver arquivo 08.
- **CA4** Usuário inativo não consegue logar.

### RF-003 — Renovar sessão (refresh)
- **CA1** Refresh token válido → novo access token + **novo** refresh token (rotação); o anterior é revogado.
- **CA2** Reuso de refresh token já revogado → revoga **toda a família** de tokens e retorna `401`.

### RF-004 — Logout
- **CA1** Revoga o refresh token atual e limpa o cookie. Retorna `204`.

### RF-005 — Perfil do cliente
- Consultar e atualizar nome, telefone e CPF. E-mail não é alterável no MVP.

### RF-006 — Alterar senha
- **CA1** Exige senha atual. Ao alterar, revoga todos os refresh tokens e envia EM-03.

### RF-007 — Endereços
- CRUD de endereços (CEP, logradouro, número, complemento, bairro, cidade, UF, destinatário). Um endereço pode ser padrão.
- **CA1** Cliente só acessa os próprios endereços (outro ID → `404`).
- **CA2** CEP é validado e os campos são preenchidos automaticamente (ViaCEP — arquivo 22).

### RF-008 — Recuperação de senha
- Fluxo por e-mail com token de uso único válido por 1 h (arquivo 23 §5).
- **CA1** A resposta é sempre `202`, exista ou não o e-mail.

### RF-009 — Confirmação de e-mail
- **CA1** Link de confirmação válido por 24 h; reenvio disponível.
- **CA2** Checkout exige e-mail confirmado (`422` com código `Identity.EmailNotConfirmed`). *(Confirmado — arquivo 20, D05.)*

---

## 2. Catálogo (público)

### RF-020 — Listar produtos
- Paginação (`page`, `pageSize` máx. 100), ordenação (`price`, `name`, `createdAt`), filtros (`category`, `minPrice`, `maxPrice`, `search`, **`colors[]`**, **`sizes[]`**).
- **CA1** Apenas produtos **ativos** com ao menos uma variação ativa aparecem.
- **CA2** Resposta inclui metadados de paginação.
- **CA3** Busca case-insensitive e sem acentos.
- **CA4** Cada item mostra o **menor preço** entre as variações ("a partir de R$ X") e as cores disponíveis.

### RF-021 — Detalhe do produto
- Por `slug`. Produto inativo → `404`/`410` (arquivo 24).
- Retorna as **variações** (cor, tamanho, preço, disponível sim/não), a **grade** de cores × tamanhos e as imagens (por cor, quando houver).
- **CA1** Não expõe a quantidade exata de estoque ao público.
- **CA2** Combinação inexistente ou sem estoque aparece como indisponível na grade.

### RF-022 — Listar categorias
- Retorna categorias ativas.

### RF-023 — Listar cores e tamanhos disponíveis (para filtros)

### RF-024 — Coleções
**Ator:** Visitante (consulta) / Admin (cadastro)
*Decidido em 29/10/2026 (arquivo 26, ADR-F08). Exemplos: "Eles" (masculino), "Prata 925".*
- Uma coleção agrupa produtos **de qualquer categoria**: o produto mora em **uma** categoria e pode estar em **várias** coleções.
- Campos: nome, slug, descrição, campos SEO, imagem de capa opcional (RF-061), tema da cartela (`Ivory` ou `Graphite`, arquivo 26 §2.2), posição na navegação, ativa.
- Público: listar coleções ativas e listar os produtos de uma coleção com a mesma paginação, ordenação e filtros do RF-020 (inclusive filtro por categoria).
- Admin: criar, editar, ativar/desativar, ordenar; incluir e remover produtos, **inclusive em lote** a partir da lista de produtos.
- **CA1** Produto inativo não aparece na coleção pública, mas continua vinculado.
- **CA2** Coleção inativa ou inexistente → `404`.
- **CA3** Coleção ativa entra no sitemap, com metadata e canonical próprios (arquivo 24); com filtros vira `noindex,follow`, como a categoria.
- **CA4** Alterar o slug cria redirect 301 (`slug_history` com `entity_type = 'Collection'`).
- **CA5** Incluir um produto que já está na coleção não duplica nem dá erro.
- **CA6** Excluir coleção não exclui produtos (só remove os vínculos).

### RF-025 — Vitrine: peça em destaque e ocasião
**Ator:** Visitante (consulta) / Admin (cadastro)
*Decidido em 29/10/2026 (arquivo 26, D-F03).*
- **Peça em destaque:** o admin escolhe um produto (e, opcionalmente, o acabamento/cor padrão) para a primeira tela da home.
- **Ocasião:** o admin cadastra datas comemorativas (ex.: Dia das Mães) com nome, texto curto (até 160 caracteres), imagem opcional (RF-061), início, fim, ativa e a lista ordenada de produtos.
- Público: uma consulta devolve o destaque e a ocasião vigente, para a home e a página de Presente.
- **CA1** Destaque inativo, inexistente ou sem nenhuma variação disponível → a consulta devolve no lugar a **novidade mais recente com estoque**, com o campo `isFallback = true`.
- **CA2** Duas ocasiões ativas não podem ter períodos que se sobrepõem → `409`.
- **CA3** Fora do período (início e fim inclusivos, horário de Brasília), a ocasião não é devolvida, sem depender de job.
- **CA4** A ocasião devolve só produtos ativos; sem nenhum, a ocasião não é devolvida.
- **CA5** Alterar destaque ou ocasião invalida o cache e revalida a home e a página de Presente.

---

## 3. Carrinho e Frete

### RF-030 — Gerenciar carrinho
**Ator:** Cliente
- Adicionar **variação**, alterar quantidade, remover, limpar, consultar.
- **CA1** Quantidade por item: 1 a 99.
- **CA2** Variação inativa ou inexistente não pode ser adicionada (`422`).
- **CA3** Exibe preço **atual** e sinaliza itens sem estoque suficiente.
- **CA4** Carrinho **não reserva** estoque.

### RF-031 — Mesclar carrinho do visitante
- Ao logar, o frontend envia os itens do carrinho local; o backend mescla somando quantidades (limite 99).

### RF-032 — Cotar frete
**Ator:** Visitante/Cliente
- Informar CEP + itens → opções Correios (PAC, SEDEX) com preço e prazo, já com as regras de frete da loja aplicadas (arquivo 22).
- **CA1** CEP inválido → `400`.
- **CA2** Serviço dos Correios indisponível → valores de contingência configurados.

---

## 4. Pedidos e Pagamento (Cliente)

### RF-040 — Checkout (criar pedido)
**Ator:** Cliente (cadastro **obrigatório** — não há compra como visitante)
Entrada: endereço de entrega (ID), **serviço de frete** escolhido, observação opcional e, opcionalmente, os dados de **presente** (RF-047).
- **CA1** Carrinho vazio → `422`.
- **CA2** Valida variação ativa e estoque suficiente para cada item; se algum falhar → `409` listando os itens, e **nada** é persistido.
- **CA3** **Recalcula o frete** no servidor para o serviço escolhido.
- **CA4** Em sucesso: cria o pedido em `AguardandoPagamento`, congela preço/nome/SKU/cor/tamanho, frete e endereço, baixa o estoque e esvazia o carrinho — **mesma transação**; e-mail EM-04 via Outbox.
- **CA5** Total = soma(itens) + frete (após regras) + embalagem para presente, quando escolhida (RF-047). 2 casas decimais.
- **CA6** Número de pedido legível e sequencial (`PED-000123`).
- **CA7** Header `Idempotency-Key` evita pedido duplicado.

### RF-044 — Pagar pedido
- Escolher PIX ou cartão (boleto opcional) e pagar pela plataforma integrada (arquivo 21).
- **CA1** Só pedidos `AguardandoPagamento` aceitam pagamento.
- **CA2** PIX retorna QR Code, copia-e-cola e expiração.
- **CA3** Cartão recusado permite nova tentativa (inclusive com outro método).
- **CA4** Valor cobrado = total do pedido calculado no servidor.

### RF-045 — Confirmação automática do pagamento
- Webhook da plataforma aprova o pagamento → pedido vai para `PagamentoConfirmado`, e-mails EM-06 (cliente) e EM-07 (admin).

### RF-046 — Cancelamento automático por falta de pagamento
- **CA1** Pedido em `AguardandoPagamento` há **48 h** é cancelado automaticamente, o estoque volta e o cliente recebe EM-10.
- **CA2** Lembrete de pagamento enviado com 24 h (EM-05).
- **CA3** Pagamento aprovado depois do cancelamento → estorno automático (RN-101).

### RF-047 — Presente no pedido
**Ator:** Cliente
*Decidido em 29/10/2026 (arquivo 26, D-F02).*
- No checkout, o cliente pode marcar o pedido como **presente**, pedir **embalagem para presente** e escrever uma **dedicatória**.
- O valor da embalagem é configurado pela loja (zero = grátis; valor ainda em aberto, arquivo 26, D-F08).
- **CA1** Dedicatória opcional, texto puro, até **200 caracteres** e 6 linhas (limite provisório, arquivo 26, D-F09). Acima disso → `400` com erro no campo.
- **CA2** Embalagem escolhida soma o valor configurado **no momento do checkout** ao total (RF-040 CA5); mudar o valor depois não altera pedidos existentes.
- **CA3** Presente, embalagem e dedicatória são **congelados** no pedido. O cliente não edita depois; o admin pode corrigir a dedicatória até o pedido ser `Enviado`, com registro de quem alterou.
- **CA4** A dedicatória aparece no detalhe do pedido (cliente e admin), no e-mail de pedido criado (EM-04) e no aviso ao admin (EM-07). Na lista do admin, pedidos de presente são marcados.
- **CA5** A dedicatória é dado do pedido (mesma retenção, LGPD) e **não** é gravada em logs.

### RF-041 — Listar meus pedidos
- Paginado, mais recentes primeiro.

### RF-042 — Detalhe do meu pedido
- Inclui status, pagamento, frete, rastreio e histórico. Pedido de outro cliente → `404`.

### RF-043 — Cancelar meu pedido
- **CA1** Permitido em `AguardandoPagamento` ou `PagamentoConfirmado`.
- **CA2** Devolve o estoque; se já pago, dispara **estorno** (arquivo 21 §5.4).

---

## 5. Backoffice (Administrador)

### RF-050 — CRUD de categorias
- Criar, editar (incl. texto e campos de SEO), ativar/desativar. Não exclui categoria com produtos (`409`).
- **Rótulos de variação** (arquivo 26, ADR-F07): `colorLabel` e `sizeLabel` opcionais, até 30 caracteres cada (ex.: "Acabamento" e "Aro" em Anéis; "Cor" e "Tamanho" em Bolsas). As APIs públicas de categoria e de produto devolvem os rótulos **já resolvidos**.
- **CA1** Subcategoria sem rótulo herda o da categoria pai; sem nenhum, vale "Cor" / "Tamanho".
- **CA2** Mudar o rótulo não altera variações nem pedidos; só o texto da tela.

### RF-051 — CRUD de produtos
- Campos: nome, slug, descrição, categoria, **coleções** (RF-024), imagens (RF-061), campos SEO, peso/medidas padrão, ativo.
- **CA1** Exclusão é lógica.
- **CA2** Alterar slug cria redirect 301 (arquivo 24).
- **CA3** Produto **sem nenhuma imagem não pode ser ativado** → `422` (a peça é a protagonista da loja, arquivo 26).

### RF-061 — Imagens (upload pelo painel)
**Ator:** Admin
*Aprovado no arquivo 20, D06 (Cloudflare R2); detalhado em 29/10/2026.*
- Usado por **produto** (galeria por cor/acabamento), **coleção** (capa, RF-024) e **ocasião** (RF-025).
- O painel deixa escolher **até 10 arquivos de uma vez**, do computador ou direto da **câmera do celular**. Cada arquivo sobe numa **requisição própria** (até 3 em paralelo), com progresso por foto; assim ninguém esbarra no limite de 100 MB por requisição do Cloudflare.
- Formatos: **JPEG, PNG, WebP e HEIC/HEIF** (foto de iPhone). Até **15 MB** por arquivo.
- Processamento no servidor: o tipo é validado pelos *magic bytes* e a imagem é **reprocessada** (arquivo 17); a orientação é corrigida; **todos os metadados (EXIF, GPS) são removidos**; a imagem é reduzida para no máximo 2400 px no lado maior e salva em **WebP**. Os outros tamanhos saem do `next/image`.
- Armazenamento no R2 com chave imutável (ex.: `products/{productId}/{imageId}.webp`) e URL pública pelo domínio de imagens. Em desenvolvimento, o **RustFS** (S3 compatível) substitui o R2 com o mesmo cliente; os testes automatizados usam um armazenamento falso.
- Produto: cada imagem tem `alt` (obrigatório; o painel sugere "Nome — Acabamento"), cor/acabamento opcional e posição. Reordenar arrastando; a primeira de cada acabamento é a capa dele.
- **CA1** Arquivo inválido volta com o motivo (tipo não aceito, acima de 15 MB, imagem corrompida) e não salva nada; as outras fotos escolhidas junto continuam subindo.
- **CA2** Imagem salva não contém EXIF nem GPS.
- **CA3** Imagem com menos de 1600 px no lado maior é aceita, mas o painel avisa que vai perder qualidade (padrão de foto do arquivo 26 §7.2).
- **CA4** Remover a imagem tira do banco na hora; o arquivo no R2 é apagado depois, via Outbox (arquivo 25).
- **CA5** A última foto de um produto ativo não pode ser removida → `422` (mesma regra do RF-051 CA3).
- **CA5** Reordenar mantém a posição única por produto + cor (`catalog.product_images`).
- **CA6** Qualquer mudança de imagem invalida o cache e revalida a página do produto, da coleção ou da home afetada.

### RF-056 — Gerenciar variações
- Gerar a grade a partir das cores e tamanhos escolhidos (ex.: 3 cores × 4 tamanhos = 12 variações) e editar cada uma: SKU, preço (padrão herdado do produto), peso/medidas, ativo.
- **CA1** SKU único.
- **CA2** Não pode haver duas variações com a mesma cor + tamanho no mesmo produto.
- **CA3** Alterar preço não afeta pedidos existentes.
- **CA4** Cada variação PODE ter um **código de barras** (EAN-8/EAN-13/GTIN), validado pelo dígito verificador e único na loja.

### RF-059 — Código de barras
**Ator:** Admin
- Botão **"Ler código de barras"** no painel: leitura por leitor USB (funciona como teclado) ou pela **câmera do celular**, que localiza a variação (produto + cor + tamanho).
- Usos: cadastro rápido do EAN na variação, **entrada de estoque** por leitura (cada leitura soma 1 ou abre o ajuste de quantidade) e busca no painel.
- **CA1** Código não cadastrado → oferecer vincular a uma variação existente.
- **CA2** Código inválido (dígito verificador) → mensagem de erro, nada é alterado.
- *Decidido (arquivo 20, D10): só **ler e cadastrar**. Gerar códigos internos e imprimir etiquetas ficam fora do MVP.*

### RF-057 — Cores e tamanhos
- CRUD de cores (nome, código hex para a bolinha de cor) e tamanhos (nome, ordem de exibição: PP, P, M, G, GG; 36, 38...).

### RF-052 — Gerenciar estoque
- Ajustes **por variação** (entrada/saída/ajuste) com motivo; cada movimentação registrada.
- **CA1** Estoque nunca negativo.

### RF-053 — Gerenciar pedidos
- Listar com filtros (status, período, cliente, forma de pagamento).
- Transições (arquivo 04): `AguardandoPagamento → PagamentoConfirmado → EmSeparacao → Enviado → Entregue`; `Cancelado` a partir dos estados permitidos.
- **CA1** A confirmação de pagamento é **automática** (webhook); confirmação manual só como exceção, com motivo obrigatório (ex.: pagamento conciliado fora do sistema).
- **CA2** Transição inválida → `422`.
- **CA3** Marcar `Enviado` exige código de rastreio.
- **CA4** Toda transição registra quem fez e quando.

### RF-058 — Regras de frete
- CRUD de regras (arquivo 22 §3.3), CEP de origem, dias de manuseio e valores de contingência.

### RF-054 — Gerenciar usuários
- Listar, ativar/desativar, promover/rebaixar admin.
- **CA1** Admin não pode desativar/rebaixar a si mesmo. **CA2** Sempre ≥ 1 admin ativo.

### RF-055 — Dashboard
- Pedidos por status, faturamento (pagamentos aprovados), ticket médio, variações com estoque baixo, estornos pendentes.

---

## 6. Seed inicial

### RF-060 — Admin inicial
- Criado a partir de variáveis de ambiente se não existir nenhum admin.

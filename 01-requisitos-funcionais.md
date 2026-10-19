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
- **CA2** Checkout exige e-mail confirmado (`422` com código `Identity.EmailNotConfirmed`). *(Ver arquivo 20, D05.)*

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
Entrada: endereço de entrega (ID), **serviço de frete** escolhido, observação opcional.
- **CA1** Carrinho vazio → `422`.
- **CA2** Valida variação ativa e estoque suficiente para cada item; se algum falhar → `409` listando os itens, e **nada** é persistido.
- **CA3** **Recalcula o frete** no servidor para o serviço escolhido.
- **CA4** Em sucesso: cria o pedido em `AguardandoPagamento`, congela preço/nome/SKU/cor/tamanho, frete e endereço, baixa o estoque e esvazia o carrinho — **mesma transação**; e-mail EM-04 via Outbox.
- **CA5** Total = soma(itens) + frete (após regras). 2 casas decimais.
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

### RF-051 — CRUD de produtos
- Campos: nome, slug, descrição, categoria, imagens (com `alt` e cor opcional), campos SEO, peso/medidas padrão, ativo.
- **CA1** Exclusão é lógica.
- **CA2** Alterar slug cria redirect 301 (arquivo 24).

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
- *A confirmar com o cliente (arquivo 20, D10): também **gerar** códigos internos para produtos sem EAN e **imprimir etiquetas**.*

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

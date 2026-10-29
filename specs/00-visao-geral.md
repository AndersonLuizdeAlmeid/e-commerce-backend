# 00 — Visão Geral

**Status:** Em revisão (decisões do cliente de 19/10/2026 incorporadas)

## 1. Objetivo

Construir uma plataforma de e-commerce B2C composta por:

- **Backend**: API REST em **C# / ASP.NET Core (.NET 10 LTS)**.
- **Frontend**: **React com Next.js** (renderização no servidor, por causa de SEO — arquivo 24) + TypeScript.
- **Banco de dados**: **PostgreSQL**.
- **Acesso a dados**: **Dapper** (sem Entity Framework).

O sistema permite que **clientes** naveguem no catálogo, escolham **cor e tamanho**, calculem o **frete dos Correios**, **paguem online** e acompanhem seus pedidos, e que **administradores** gerenciem catálogo, estoque, frete, pedidos e usuários.

## 2. Decisões do cliente (19/10/2026)

| Tema | Decisão | Spec |
|------|---------|------|
| Pagamento | **Integrado com uma plataforma** (PIX, cartão) já na primeira versão | 21 |
| Variações | Produtos **com cores e tamanhos** | 04 |
| Frete | **Correios**, cálculo integrado, regras flexíveis definidas pela loja | 22 |
| Pedido não pago | **Cancelado automaticamente após 48 h** | 25 |
| Compra sem conta | **Não permitida** — cadastro obrigatório | 09 |
| E-mails | **Sim**, e-mails transacionais | 23 |
| SEO | **Prioridade** — renderização no servidor | 24 |

## 3. Escopo da primeira versão

| Área | Incluído |
|------|----------|
| Identidade | Cadastro, confirmação de e-mail, login, logout, recuperação e troca de senha, perfis Cliente/Admin |
| Catálogo | Categorias, produtos com **variações (cor × tamanho)**, imagens, busca, filtros (inclusive por cor/tamanho), paginação |
| Estoque | Quantidade **por variação**, baixa na criação do pedido, devolução no cancelamento |
| Carrinho | Carrinho persistido por cliente; carrinho local para visitante |
| Frete | Cotação Correios (PAC/SEDEX) por CEP, regras de frete grátis/desconto, código de rastreio |
| Pagamento | PIX e cartão via plataforma, confirmação automática por webhook, estorno no cancelamento |
| Pedidos | Checkout, histórico, cancelamento, **cancelamento automático em 48 h** sem pagamento |
| E-mails | Boas-vindas, senha, pedido criado/pago/enviado/entregue/cancelado, lembrete de pagamento |
| SEO | Páginas renderizadas no servidor, URLs amigáveis, sitemap, dados estruturados, feed Google Shopping |
| Backoffice | CRUD de catálogo e variações, estoque, regras de frete, gestão de pedidos e usuários, painel de números |

## 4. Fora de escopo (primeira versão)

- Cupons, promoções de produto, programa de fidelidade.
- Avaliações de produtos.
- Marketplace / múltiplos vendedores.
- Multi-idioma e multi-moeda (moeda única: **BRL**).
- Emissão de nota fiscal.
- E-mail marketing / newsletter.
- Geração automática de etiquetas e atualização automática de entrega pelo rastreio (evolução — arquivo 22).

## 5. Personas

| Persona | Descrição |
|---------|-----------|
| **Visitante** | Não autenticado. Navega no catálogo, simula frete, monta carrinho local. **Não compra** sem criar conta. |
| **Cliente** (`Customer`) | Autenticado. Gerencia perfil, endereços, carrinho, pedidos e pagamentos próprios. |
| **Administrador** (`Admin`) | Autenticado. Acesso ao backoffice. |

## 6. Glossário (Linguagem Ubíqua)

| Termo | Significado |
|-------|-------------|
| **Produto** | Item do catálogo (ex.: "Camiseta Básica"), com nome, descrição, categoria e imagens. Não é vendido diretamente. |
| **Variação** | Combinação vendável de um produto (ex.: Camiseta Básica, **Azul, M**). Tem SKU, preço, peso/medidas e estoque próprios. |
| **SKU** | Código único da variação. |
| **Cor / Tamanho** | Atributos padronizados das variações (tabelas de cores e tamanhos mantidas pelo admin). |
| **Categoria** | Agrupamento de produtos. |
| **Carrinho** | Intenção de compra; não reserva estoque. |
| **Cotação de frete** | Opções de envio (PAC, SEDEX) com preço e prazo para um CEP. |
| **Regra de frete** | Ajuste comercial da loja sobre a cotação (frete grátis acima de X, desconto por região...). |
| **Pedido** | Compromisso de compra; congela preços, variações, frete e endereço. |
| **Pagamento** | Tentativa de pagamento de um pedido numa plataforma (PIX, cartão). |
| **Webhook** | Aviso automático da plataforma de pagamento à loja quando o status do pagamento muda. |
| **Estorno** | Devolução do valor pago ao cliente. |

## 7. Repositórios

- `e-commerce-backend` (este repositório): API, specs, migrations, testes.
- `e-commerce-frontend` (repositório separado — decidido, arquivo 20, T01): Next.js.

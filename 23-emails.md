# 23 — E-mails Transacionais

**Status:** Rascunho — decisão do cliente: **envio de e-mails na primeira versão**.

## 1. Provedor

Integração via porta `IEmailSender`; provedor a definir (arquivo 20, D04).

| Provedor | Observação |
|----------|------------|
| **Amazon SES** (sugestão por custo) | Muito barato em volume; exige verificação de domínio |
| Resend | API simples, boa experiência de desenvolvimento |
| SendGrid / Brevo | Painel de métricas, plano gratuito limitado |

Em desenvolvimento: **Mailpit** (container que captura os e-mails localmente — nada é enviado de verdade).

## 2. Entregabilidade (obrigatório antes do go-live)

- Domínio próprio de envio (ex.: `nao-responda@loja.com.br`) com **SPF, DKIM e DMARC** configurados.
- Endereço de resposta (`Reply-To`) para o atendimento.

## 3. Catálogo de e-mails

| ID | Gatilho (evento) | Destinatário | Conteúdo |
|----|------------------|--------------|----------|
| EM-01 | `UserRegistered` | Cliente | Boas-vindas + **confirmação de e-mail** (link válido por 24 h) |
| EM-02 | Solicitação de recuperação de senha | Cliente | Link de redefinição (válido por **1 h**, uso único) |
| EM-03 | Senha alterada | Cliente | Aviso de segurança ("não foi você?") |
| EM-04 | `OrderPlaced` | Cliente | Resumo do pedido + como pagar (QR PIX/link) + prazo de 48 h |
| EM-05 | 24 h sem pagamento | Cliente | Lembrete de pagamento |
| EM-06 | `OrderPaymentConfirmed` | Cliente | Pagamento aprovado |
| EM-07 | `OrderPaymentConfirmed` | **Admin** | Novo pedido pago para separar |
| EM-08 | `OrderShipped` | Cliente | Enviado + código e link de rastreio |
| EM-09 | `OrderDelivered` | Cliente | Entregue |
| EM-10 | `OrderCancelled` | Cliente | Cancelado (motivo: pelo cliente, pela loja, ou **falta de pagamento em 48 h**) + estorno quando houver |
| EM-11 | Estoque baixo (diário) | Admin | Variações abaixo do limiar |

## 4. Implementação

- **Nunca** enviar e-mail dentro da transação do caso de uso. O handler grava uma mensagem no **Outbox** (mesma transação); um processador em background envia (arquivo 25). Garante que: pedido criado ⇒ e-mail sai (eventualmente) e pedido com rollback ⇒ nenhum e-mail.
- Retry com backoff exponencial (até 5 tentativas); após isso, mensagem marcada como falha e alerta.
- Templates em HTML responsivo + versão texto, em pt-BR, com marca da loja; renderizados com **Razor** (`RazorLight` ou componentes Razor) ou **Scriban**. Templates versionados no repositório.
- Registro em `notifications.email_log` (destinatário mascarado, template, status, id do provedor) para suporte.
- Links nos e-mails apontam para o frontend (`FRONTEND_BASE_URL`).

## 5. Recuperação de senha (RF-008)

1. `POST /auth/forgot-password` `{ email }` → **sempre** `202` (não revela se o e-mail existe).
2. Se existir: gera token aleatório (32 bytes), salva **hash** + expiração (1 h), envia EM-02.
3. `POST /auth/reset-password` `{ token, newPassword }` → valida, troca a senha, invalida o token, revoga sessões (arquivo 08), envia EM-03.
4. Rate limit `auth-strict` nos dois endpoints.

## 6. Confirmação de e-mail (RF-009)

- Conta funciona sem confirmação para navegar, mas **checkout exige e-mail confirmado** (reduz pedidos falsos e garante que os e-mails do pedido chegam). Reenvio do link disponível na área do cliente. *(Confirmar com o cliente — arquivo 20, D05.)*

## 7. LGPD

- E-mails transacionais não exigem consentimento (execução de contrato). **Marketing/newsletter** fica fora do escopo e exigiria opt-in separado.

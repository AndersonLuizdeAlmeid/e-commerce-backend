using ECommerce.Application.Abstractions.Authentication;
using ECommerce.Application.Abstractions.Outbox;
using ECommerce.Application.Notifications;
using ECommerce.Domain.Identity;

namespace ECommerce.Application.Identity;

/// <summary>
/// E-mails de conta (EM-01..EM-03) sempre pelo Outbox, na transação do caso de uso (arquivo 23 §4):
/// se o cadastro der rollback, o e-mail não sai.
/// </summary>
internal sealed class AccountEmails(
    IOutbox outbox,
    ISecureTokenFactory secureTokens,
    IUserTokenRepository userTokens,
    TimeProvider time)
{
    /// <summary>EM-01: emite um link de confirmação novo (24 h) e invalida os anteriores.</summary>
    public async Task SendEmailConfirmationAsync(User user, string name, bool welcome, CancellationToken cancellationToken)
    {
        var token = await IssueTokenAsync(user, UserTokenPurpose.EmailConfirmation, cancellationToken);
        await EnqueueAsync(
            EmailTemplates.WelcomeAndConfirmation,
            user,
            new Dictionary<string, string> { ["name"] = name, ["welcome"] = welcome ? "true" : "false" },
            new Dictionary<string, string> { ["token"] = token },
            cancellationToken);
    }

    /// <summary>EM-02: link de redefinição de senha (1 h, uso único).</summary>
    public async Task SendPasswordResetAsync(User user, string name, CancellationToken cancellationToken)
    {
        var token = await IssueTokenAsync(user, UserTokenPurpose.PasswordReset, cancellationToken);
        await EnqueueAsync(
            EmailTemplates.PasswordReset,
            user,
            new Dictionary<string, string> { ["name"] = name },
            new Dictionary<string, string> { ["token"] = token },
            cancellationToken);
    }

    /// <summary>EM-03: aviso de segurança depois de troca ou redefinição de senha.</summary>
    public Task SendPasswordChangedAsync(User user, string name, CancellationToken cancellationToken) =>
        EnqueueAsync(
            EmailTemplates.PasswordChanged,
            user,
            new Dictionary<string, string> { ["name"] = name },
            new Dictionary<string, string>(),
            cancellationToken);

    private async Task<string> IssueTokenAsync(User user, UserTokenPurpose purpose, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var token = secureTokens.Create();

        await userTokens.InvalidateAllAsync(user.Id, purpose, now, cancellationToken);
        await userTokens.AddAsync(UserToken.Issue(user.Id, purpose, token.Hash, now), cancellationToken);

        return token.Raw;
    }

    private Task EnqueueAsync(
        string template,
        User user,
        Dictionary<string, string> model,
        Dictionary<string, string> secrets,
        CancellationToken cancellationToken) =>
        outbox.EnqueueAsync(
            SendEmail.Type,
            new SendEmail(template, user.Email.Value, user.Id.Value, model, secrets),
            cancellationToken);
}

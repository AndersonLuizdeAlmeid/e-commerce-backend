using System.Text.Json;
using Dapper;
using ECommerce.Application.Notifications;
using ECommerce.Domain.Identity;
using ECommerce.Infrastructure.Messaging;
using Npgsql;

namespace ECommerce.Infrastructure.Notifications;

/// <summary>Mensagem <see cref="SendEmail.Type"/> do Outbox: renderiza, envia e registra em notifications.email_log.</summary>
internal sealed class SendEmailHandler(
    IEmailSender sender,
    EmailTemplateRenderer renderer,
    NpgsqlDataSource dataSource,
    TimeProvider time) : IOutboxMessageHandler
{
    public string Type => SendEmail.Type;

    public async Task HandleAsync(string payloadJson, CancellationToken cancellationToken)
    {
        var email = JsonSerializer.Deserialize<SendEmail>(payloadJson, OutboxWriter.SerializerOptions)
            ?? throw new InvalidOperationException("Payload de e-mail vazio.");

        var message = renderer.Render(email);
        try
        {
            var providerId = await sender.SendAsync(message, cancellationToken);
            await LogAsync(email, "Sent", providerId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await LogAsync(email, "Failed", null, cancellationToken);
            throw;
        }
    }

    private async Task LogAsync(SendEmail email, string status, string? providerId, CancellationToken cancellationToken)
    {
        var masked = Email.Create(email.To) is { IsSuccess: true } valid ? valid.Value.Masked() : "***";
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO notifications.email_log (id, template, recipient_masked, status, provider_message_id, related_entity_id, created_at)
            VALUES (@Id, @Template, @Recipient, @Status, @ProviderId, @RelatedId, @Now)
            """,
            new
            {
                Id = Guid.CreateVersion7(),
                email.Template,
                Recipient = masked,
                Status = status,
                ProviderId = providerId,
                RelatedId = email.RelatedEntityId,
                Now = time.GetUtcNow().UtcDateTime,
            },
            cancellationToken: cancellationToken));
    }
}

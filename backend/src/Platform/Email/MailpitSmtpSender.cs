using System.Net;
using System.Net.Mail;

namespace Lagerkraft.Platform.Email;

public sealed class MailpitSmtpSender(IConfiguration configuration, ILogger<MailpitSmtpSender> logger) : IEmailSender
{
    public async Task SendAsync(string to, string subject, string body, CancellationToken ct)
    {
        var host = configuration["Email:Smtp:Host"] ?? "localhost";
        var port = int.TryParse(configuration["Email:Smtp:Port"], out var p) ? p : 1025;
        var from = configuration["Email:From"] ?? "noreply@lagerkraft.local";

        using var client = new SmtpClient(host, port)
        {
            DeliveryMethod = SmtpDeliveryMethod.Network,
            EnableSsl = false
        };
        using var message = new MailMessage(from, to, subject, body);
        try
        {
            await client.SendMailAsync(message, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SMTP send to {To} failed (dev Mailpit may be down)", to);
            throw;
        }
    }
}

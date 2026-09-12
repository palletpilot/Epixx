namespace Lagerkraft.Platform.Email;

/// <summary>
/// Production transactional email. Wired when Email:Provider=mailjet; not used in local/dev.
/// </summary>
public sealed class MailjetSender(IConfiguration configuration, ILogger<MailjetSender> logger) : IEmailSender
{
    public Task SendAsync(string to, string subject, string body, CancellationToken ct)
    {
        var key = configuration["Email:Mailjet:ApiKey"];
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidOperationException("Email:Mailjet:ApiKey is not configured.");
        }

        // ponytail: HTTP Mailjet client lands with production email wiring; config gate is enough for SP0.
        logger.LogError("MailjetSender invoked without HTTP client implementation for {To}", to);
        throw new NotImplementedException("Mailjet HTTP send is not implemented in SP0 B3.");
    }
}

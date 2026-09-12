using System.Collections.Concurrent;

namespace Lagerkraft.Platform.Email;

public sealed class CapturingEmailSender : IEmailSender
{
    private readonly ConcurrentQueue<CapturedEmail> _sent = new();

    public IReadOnlyCollection<CapturedEmail> Sent => _sent.ToArray();

    public Task SendAsync(string to, string subject, string body, CancellationToken ct)
    {
        _sent.Enqueue(new CapturedEmail(to, subject, body));
        return Task.CompletedTask;
    }

    public sealed record CapturedEmail(string To, string Subject, string Body);
}

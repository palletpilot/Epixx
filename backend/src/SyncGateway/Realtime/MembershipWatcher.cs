using System.Text.Json;

namespace Lagerkraft.SyncGateway.Realtime;

/// <summary>
/// Watches tenant bus for membership_changed and closes SSE connections with a stale session.
/// </summary>
public sealed class MembershipWatcher(ConnectionRegistry registry)
{
    public Task HandleAsync(TenantBusMessage message, CancellationToken ct)
    {
        if (!message.Subject.Contains("membership_changed", StringComparison.OrdinalIgnoreCase)
            && !message.Payload.Contains("membership_changed", StringComparison.OrdinalIgnoreCase))
        {
            return Task.CompletedTask;
        }

        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(message.Payload) ? "{}" : message.Payload);
            var root = doc.RootElement;
            if (root.TryGetProperty("data", out var data))
            {
                root = data;
            }

            if (!root.TryGetProperty("user_id", out var userEl)
                || !Guid.TryParse(userEl.GetString() ?? userEl.ToString(), out var userId))
            {
                return Task.CompletedTask;
            }

            int? sv = null;
            if (root.TryGetProperty("session_version", out var svEl) && svEl.TryGetInt32(out var svi))
            {
                sv = svi;
            }

            // Close connections whose token sv is behind the new membership version.
            var tenantId = ExtractTenant(message.Subject);
            if (tenantId is null)
            {
                return Task.CompletedTask;
            }

            registry.CloseForUser(tenantId.Value, userId, sv);
        }
        catch (JsonException)
        {
            // ignore malformed
        }

        return Task.CompletedTask;
    }

    private static Guid? ExtractTenant(string subject)
    {
        // lagerkraft.{tenant}.tenant.membership_changed
        var parts = subject.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 && Guid.TryParse(parts[1], out var tid))
        {
            return tid;
        }

        return null;
    }
}

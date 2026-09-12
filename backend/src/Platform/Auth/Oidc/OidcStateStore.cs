using System.Collections.Concurrent;

namespace Lagerkraft.Platform.Auth.Oidc;

public sealed class OidcStateStore
{
    private readonly ConcurrentDictionary<string, OidcState> _states = new();

    public string Create(Guid tenantId, Guid providerId)
    {
        var state = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        _states[state] = new OidcState(tenantId, providerId, DateTimeOffset.UtcNow.AddMinutes(15));
        return state;
    }

    public bool TryTake(string state, out OidcState value)
    {
        if (_states.TryRemove(state, out value!) && value.ExpiresAt > DateTimeOffset.UtcNow)
        {
            return true;
        }

        value = null!;
        return false;
    }

    public sealed record OidcState(Guid TenantId, Guid ProviderId, DateTimeOffset ExpiresAt);
}

using System.Collections.Concurrent;
using System.Text.Json;
using Lagerkraft.Platform.Data;
using Lagerkraft.Platform.Tenancy;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Lagerkraft.Platform.Auth.Oidc;

public sealed class DynamicOidcHandler(
    IServiceScopeFactory scopes,
    ConnectionStringProtector protector,
    IConfiguration configuration)
{
    private readonly ConcurrentDictionary<string, OpenIdConnectConfiguration> _discovery = new(StringComparer.Ordinal);

    public async Task<IdentityProvider?> ResolveBySlugOrEmailAsync(string? slug, string? email, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

        if (!string.IsNullOrWhiteSpace(slug))
        {
            var bySlug = await db.IdentityProviders.AsNoTracking()
                .Where(p => db.Tenants.Any(t => t.Id == p.TenantId && t.Slug == slug))
                .FirstOrDefaultAsync(ct);
            if (bySlug is not null)
            {
                return bySlug;
            }
        }

        if (!string.IsNullOrWhiteSpace(email))
        {
            var at = email.LastIndexOf('@');
            if (at > 0)
            {
                var domain = email[(at + 1)..].Trim().ToLowerInvariant();
                return await db.IdentityProviders.AsNoTracking()
                    .Where(p => p.DomainHint != null && p.DomainHint.ToLower() == domain)
                    .FirstOrDefaultAsync(ct);
            }
        }

        return null;
    }

    public async Task<OpenIdConnectOptions> BuildOptionsAsync(IdentityProvider provider, CancellationToken ct)
    {
        var secret = provider.ClientSecret is null ? "" : protector.Unprotect(provider.ClientSecret);
        var callback = (configuration["PublicBaseUrl"] ?? "http://localhost:5100").TrimEnd('/')
            + "/auth/oidc/callback";
        var options = new OpenIdConnectOptions
        {
            Authority = provider.Issuer.TrimEnd('/'),
            ClientId = provider.ClientId,
            ClientSecret = secret,
            ResponseType = OpenIdConnectResponseType.Code,
            SaveTokens = true,
            GetClaimsFromUserInfoEndpoint = false,
            CallbackPath = "/auth/oidc/callback",
            SignedOutCallbackPath = "/auth/oidc/signed-out",
            Scope = { "openid", "profile", "email" },
            TokenValidationParameters =
            {
                ValidateIssuer = true,
                ValidIssuer = provider.Issuer.TrimEnd('/')
            }
        };
        options.Events.OnRedirectToIdentityProvider = ctx =>
        {
            ctx.ProtocolMessage.RedirectUri = callback;
            return Task.CompletedTask;
        };

        _ = await GetDiscoveryAsync(provider.Issuer, ct);
        return options;
    }

    public async Task<OpenIdConnectConfiguration> GetDiscoveryAsync(string issuer, CancellationToken ct)
    {
        var key = issuer.TrimEnd('/');
        if (_discovery.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var address = key + "/.well-known/openid-configuration";
        var retriever = new OpenIdConnectConfigurationRetriever();
        var manager = new ConfigurationManager<OpenIdConnectConfiguration>(
            address,
            retriever,
            new HttpDocumentRetriever { RequireHttps = false });
        var config = await manager.GetConfigurationAsync(ct);
        _discovery[key] = config;
        return config;
    }

    public static string? MapRole(IdentityProvider provider, IEnumerable<string> roles, IEnumerable<string> groups)
    {
        Dictionary<string, string>? mappings = null;
        if (!string.IsNullOrWhiteSpace(provider.RoleMappings))
        {
            mappings = JsonSerializer.Deserialize<Dictionary<string, string>>(provider.RoleMappings);
        }

        foreach (var role in roles)
        {
            if (mappings is not null && mappings.TryGetValue(role, out var mapped))
            {
                return mapped;
            }
        }

        foreach (var group in groups)
        {
            if (mappings is not null && mappings.TryGetValue(group, out var mapped))
            {
                return mapped;
            }
        }

        return provider.DefaultRole;
    }
}

using System.Text.Json.Serialization;

namespace Lagerkraft.Platform.Auth.Oidc;

public sealed record OidcStartRequest(
    [property: JsonPropertyName("email")] string? Email,
    [property: JsonPropertyName("slug")] string? Slug);

public sealed record OidcStartResponse(
    [property: JsonPropertyName("authorize_url")] string AuthorizeUrl,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("tenant_id")] Guid TenantId);

public sealed record OidcCallbackRequest(
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("id_token")] string IdToken,
    [property: JsonPropertyName("code")] string? Code);

public sealed record UpsertOidcProviderRequest(
    [property: JsonPropertyName("issuer")] string Issuer,
    [property: JsonPropertyName("client_id")] string ClientId,
    [property: JsonPropertyName("client_secret")] string ClientSecret,
    [property: JsonPropertyName("client_secret_expires_at")] DateTimeOffset? ClientSecretExpiresAt,
    [property: JsonPropertyName("domain_hint")] string? DomainHint,
    [property: JsonPropertyName("jit_provisioning")] string JitProvisioning,
    [property: JsonPropertyName("default_role")] string? DefaultRole,
    [property: JsonPropertyName("role_mappings")] string? RoleMappings);

public sealed record OidcProviderResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("issuer")] string Issuer,
    [property: JsonPropertyName("client_id")] string ClientId,
    [property: JsonPropertyName("domain_hint")] string? DomainHint,
    [property: JsonPropertyName("jit_provisioning")] string JitProvisioning,
    [property: JsonPropertyName("default_role")] string? DefaultRole,
    [property: JsonPropertyName("enforced")] bool Enforced,
    [property: JsonPropertyName("client_secret_expires_at")] DateTimeOffset? ClientSecretExpiresAt,
    [property: JsonPropertyName("role_mappings")] string? RoleMappings);

public sealed record SetEnforcedRequest([property: JsonPropertyName("enforced")] bool Enforced);

public sealed record OidcTestLoginRequest(
    [property: JsonPropertyName("id_token")] string IdToken);

public sealed record OidcTestLoginResponse(
    [property: JsonPropertyName("claims")] Dictionary<string, string> Claims);

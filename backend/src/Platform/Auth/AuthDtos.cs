using System.Text.Json.Serialization;

namespace Lagerkraft.Platform.Auth;

public sealed record LoginRequest(
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("password")] string Password,
    [property: JsonPropertyName("totp")] string? Totp,
    [property: JsonPropertyName("device_id")] Guid? DeviceId = null);

public sealed record TokenResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("refresh_token")] string RefreshToken,
    [property: JsonPropertyName("token_type")] string TokenType,
    [property: JsonPropertyName("expires_in")] int ExpiresIn);

public sealed record MembershipChoice(
    [property: JsonPropertyName("tenant_id")] Guid TenantId,
    [property: JsonPropertyName("company_name")] string CompanyName,
    [property: JsonPropertyName("slug")] string Slug);

public sealed record ChooserResponse(
    [property: JsonPropertyName("chooser_token")] string ChooserToken,
    [property: JsonPropertyName("memberships")] IReadOnlyList<MembershipChoice> Memberships);

public sealed record ChooseTenantRequest(
    [property: JsonPropertyName("chooser_token")] string ChooserToken,
    [property: JsonPropertyName("tenant_id")] Guid TenantId);

public sealed record RefreshRequest(
    [property: JsonPropertyName("refresh_token")] string RefreshToken);

public sealed record LogoutRequest(
    [property: JsonPropertyName("refresh_token")] string RefreshToken);

public sealed record SwitchTenantRequest(
    [property: JsonPropertyName("tenant_id")] Guid TenantId);

public sealed record PinUnlockRequest(
    [property: JsonPropertyName("device_id")] Guid DeviceId,
    [property: JsonPropertyName("user_id")] Guid UserId,
    [property: JsonPropertyName("pin")] string Pin);

public sealed record TotpEnrollResponse(
    [property: JsonPropertyName("secret")] string Secret,
    [property: JsonPropertyName("uri")] string Uri);

public sealed record TotpConfirmRequest(
    [property: JsonPropertyName("code")] string TotpCode);

using System.Security.Claims;
using System.Text.Json;
using Lagerkraft.Platform.Data;
using Lagerkraft.Shared;
using Lagerkraft.Shared.Auth;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Lagerkraft.Platform.Auth;

public sealed class JwtIssuer(SigningKey key, IClock clock)
{
    public const string Issuer = "lagerkraft";
    public const string Audience = "lagerkraft";
    public const int AccessMinutes = 15;
    public const int ChooserMinutes = 5;

    public string IssueAccess(Membership membership, string amr, Guid? deviceId)
    {
        var claims = new List<Claim>
        {
            Str(JwtRegisteredClaimNames.Sub, membership.UserId.ToString()),
            Str(JwtRegisteredClaimNames.Jti, Ids.New().ToString()),
            Str(LagerkraftClaims.TenantId, membership.TenantId.ToString()),
            Str(LagerkraftClaims.AuthMethod, amr),
            Str(LagerkraftClaims.Owner, membership.IsOwner ? "true" : "false"),
            Str(LagerkraftClaims.RoleAssignments, SerializeAssignments(membership.RoleAssignments)),
            Str(LagerkraftClaims.PermissionMapVersion, Permissions.MapVersion.ToString()),
            Str(LagerkraftClaims.SessionVersion, membership.SessionVersion.ToString())
        };
        if (deviceId is { } dev)
        {
            claims.Add(Str(LagerkraftClaims.DeviceId, dev.ToString()));
        }

        return Create(claims, clock.UtcNow.AddMinutes(AccessMinutes));
    }

    public string IssueChooser(Guid userId, string amr) =>
        Create(
        [
            Str(JwtRegisteredClaimNames.Sub, userId.ToString()),
            Str(JwtRegisteredClaimNames.Jti, Ids.New().ToString()),
            Str("purpose", "chooser"),
            Str(LagerkraftClaims.AuthMethod, amr)
        ],
            clock.UtcNow.AddMinutes(ChooserMinutes));

    public async Task<JsonWebToken?> ReadAsync(string token)
    {
        var handler = new JsonWebTokenHandler();
        var result = await handler.ValidateTokenAsync(token, ValidationParameters());
        return result.IsValid ? result.SecurityToken as JsonWebToken : null;
    }

    public TokenValidationParameters ValidationParameters() => new()
    {
        ValidIssuer = Issuer,
        ValidAudience = Audience,
        IssuerSigningKey = key.Credentials.Key,
        ValidateIssuerSigningKey = true,
        ValidateLifetime = true,
        ClockSkew = TimeSpan.Zero,
        NameClaimType = JwtRegisteredClaimNames.Sub,
        LifetimeValidator = (notBefore, expires, _, _) =>
        {
            var now = clock.UtcNow.UtcDateTime;
            if (notBefore.HasValue && now < notBefore.Value)
            {
                return false;
            }

            return !expires.HasValue || now < expires.Value;
        }
    };

    private string Create(IEnumerable<Claim> claims, DateTimeOffset expires)
    {
        var now = clock.UtcNow.UtcDateTime;
        var handler = new JsonWebTokenHandler();
        return handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            Subject = new ClaimsIdentity(claims),
            NotBefore = now,
            Expires = expires.UtcDateTime,
            IssuedAt = now,
            SigningCredentials = key.Credentials
        });
    }

    private static Claim Str(string type, string value) =>
        new(type, JsonSerializer.Serialize(value), JsonClaimValueTypes.Json);

    private static string SerializeAssignments(IEnumerable<Data.RoleAssignment> assignments)
    {
        var active = assignments.Where(a => a.ValidTo is null).ToList();
        var grouped = active
            .GroupBy(a => a.Role.InternalName)
            .Select(g =>
            {
                var ids = g.Select(a => a.WarehouseId).ToList();
                if (ids.Any(id => id is null))
                {
                    return new Dictionary<string, object?> { ["r"] = g.Key, ["w"] = "*" };
                }

                return new Dictionary<string, object?>
                {
                    ["r"] = g.Key,
                    ["w"] = ids.Select(id => id!.Value.ToString()).ToArray()
                };
            });
        return JsonSerializer.Serialize(grouped);
    }
}

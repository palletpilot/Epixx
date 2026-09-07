using System.Security.Claims;
using Lagerkraft.Platform.Data;
using Lagerkraft.Shared;
using Lagerkraft.Shared.Auth;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Lagerkraft.Platform.Auth;

public static class AuthApi
{
    public static WebApplication MapAuthApi(this WebApplication app)
    {
        app.MapGet("/.well-known/jwks.json", Jwks);
        var auth = app.MapGroup("/auth");
        auth.MapPost("/login", Login);
        auth.MapPost("/choose-tenant", ChooseTenant);
        auth.MapPost("/refresh", Refresh);
        auth.MapPost("/logout", Logout);
        auth.MapPost("/pin-unlock", PinUnlock);
        auth.MapPost("/switch-tenant", SwitchTenant).RequireAuthorization();
        auth.MapPost("/totp/enroll", TotpEnroll).RequireAuthorization();
        auth.MapPost("/totp/enroll/confirm", TotpConfirm).RequireAuthorization();
        return app;
    }

    private static IResult Jwks(SigningKey key)
    {
        var parameters = key.Rsa.ExportParameters(false);
        return Results.Json(new
        {
            keys = new[]
            {
                new
                {
                    kty = "RSA",
                    use = "sig",
                    kid = SigningKey.KeyId,
                    alg = "RS256",
                    n = Base64UrlEncoder.Encode(parameters.Modulus!),
                    e = Base64UrlEncoder.Encode(parameters.Exponent!)
                }
            }
        });
    }

    private static async Task<IResult> Login(
        [FromBody] LoginRequest body,
        UserManager<AppUser> users,
        PlatformDbContext db,
        JwtIssuer jwt,
        RefreshTokenStore refresh,
        IClock clock,
        CancellationToken ct)
    {
        var user = await users.FindByEmailAsync(body.Email);
        if (user is null || !await users.CheckPasswordAsync(user, body.Password))
        {
            return Unauthorized("invalid_credentials");
        }

        if (user.TwoFactorEnabled)
        {
            if (string.IsNullOrWhiteSpace(body.Totp)
                || !await users.VerifyTwoFactorTokenAsync(
                    user, TokenOptions.DefaultAuthenticatorProvider, body.Totp))
            {
                return Unauthorized("totp_invalid");
            }
        }

        var memberships = await MembershipsFor(db, user.Id).ToListAsync(ct);
        if (memberships.Count == 0)
        {
            return Unauthorized("no_membership");
        }

        foreach (var membership in memberships)
        {
            ResetPinLock(membership);
        }

        await db.SaveChangesAsync(ct);

        var amr = user.TwoFactorEnabled ? "mfa" : "pwd";
        if (memberships.Count == 1)
        {
            return Results.Ok(await IssueAsync(jwt, refresh, memberships[0], amr, deviceId: null, ct));
        }

        return Results.Ok(new ChooserResponse(
            jwt.IssueChooser(user.Id, amr),
            memberships.Select(m => new MembershipChoice(m.TenantId, m.Tenant.CompanyName, m.Tenant.Slug)).ToList()));
    }

    private static async Task<IResult> ChooseTenant(
        [FromBody] ChooseTenantRequest body,
        JwtIssuer jwt,
        RefreshTokenStore refresh,
        PlatformDbContext db,
        CancellationToken ct)
    {
        var token = await jwt.ReadAsync(body.ChooserToken);
        if (token is null
            || !token.TryGetPayloadValue<string>("purpose", out var purpose)
            || purpose != "chooser")
        {
            return Unauthorized("invalid_chooser");
        }

        if (!Guid.TryParse(token.Subject, out var userId))
        {
            return Unauthorized("invalid_chooser");
        }

        var membership = await MembershipsFor(db, userId)
            .Where(m => m.TenantId == body.TenantId)
            .FirstOrDefaultAsync(ct);
        if (membership is null)
        {
            return Forbidden("not_a_member");
        }

        var amr = token.TryGetPayloadValue<string>(LagerkraftClaims.AuthMethod, out var method)
            ? method
            : "pwd";
        return Results.Ok(await IssueAsync(jwt, refresh, membership, amr, deviceId: null, ct));
    }

    private static async Task<IResult> Refresh(
        [FromBody] RefreshRequest body,
        RefreshTokenStore refresh,
        PlatformDbContext db,
        JwtIssuer jwt,
        IClock clock,
        CancellationToken ct)
    {
        var row = await refresh.FindAsync(body.RefreshToken, ct);
        if (row is null || row.ExpiresAt <= clock.UtcNow)
        {
            return Unauthorized("invalid_refresh");
        }

        var membership = await db.Memberships
            .Include(m => m.Tenant)
            .Include(m => m.RoleAssignments).ThenInclude(a => a.Role)
            .Where(m => m.Id == row.MembershipId)
            .FirstOrDefaultAsync(ct);
        if (membership is null || row.SessionVersion != membership.SessionVersion)
        {
            return Unauthorized("invalid_refresh");
        }

        db.RefreshTokens.Remove(row);
        await db.SaveChangesAsync(ct);
        return Results.Ok(await IssueAsync(jwt, refresh, membership, "pwd", row.DeviceId, ct));
    }

    private static async Task<IResult> Logout([FromBody] LogoutRequest body, RefreshTokenStore refresh, CancellationToken ct)
    {
        await refresh.RevokeAsync(body.RefreshToken, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> PinUnlock(
        [FromBody] PinUnlockRequest body,
        PlatformDbContext db,
        JwtIssuer jwt,
        RefreshTokenStore refresh,
        IClock clock,
        CancellationToken ct)
    {
        var device = await db.Devices.Where(d => d.Id == body.DeviceId).FirstOrDefaultAsync(ct);
        if (device is null || device.RevokedAt is not null)
        {
            return Unauthorized("invalid_pin");
        }

        var membership = await MembershipsFor(db, body.UserId)
            .Where(m => m.TenantId == device.TenantId)
            .FirstOrDefaultAsync(ct);
        if (membership?.PinHash is null)
        {
            return Unauthorized("invalid_pin");
        }

        if (membership.PinFullLoginRequired)
        {
            return Unauthorized("pin_full_login_required");
        }

        if (membership.PinLockedUntil is { } until && until > clock.UtcNow)
        {
            return Results.Json(new { code = "pin_locked" }, statusCode: StatusCodes.Status423Locked);
        }

        if (!PinHasher.Verify(body.Pin, membership.PinHash))
        {
            membership.PinFailedAttempts++;
            if (membership.PinFailedAttempts >= 10)
            {
                membership.PinFullLoginRequired = true;
            }
            else if (membership.PinFailedAttempts == 5)
            {
                membership.PinLockedUntil = clock.UtcNow.AddMinutes(15);
            }

            await db.SaveChangesAsync(ct);
            return Unauthorized("invalid_pin");
        }

        ResetPinLock(membership);
        db.DeviceSessions.Add(new DeviceSession
        {
            Id = Ids.New(),
            DeviceId = device.Id,
            MembershipId = membership.Id,
            CreatedAt = clock.UtcNow
        });
        await db.SaveChangesAsync(ct);
        return Results.Ok(await IssueAsync(jwt, refresh, membership, "pin", device.Id, ct));
    }

    private static async Task<IResult> SwitchTenant(
        [FromBody] SwitchTenantRequest body,
        ClaimsPrincipal principal,
        PlatformDbContext db,
        JwtIssuer jwt,
        RefreshTokenStore refresh,
        CancellationToken ct)
    {
        if (!TryUserId(principal, out var userId))
        {
            return Unauthorized("invalid_token");
        }

        var membership = await MembershipsFor(db, userId)
            .Where(m => m.TenantId == body.TenantId)
            .FirstOrDefaultAsync(ct);
        if (membership is null)
        {
            return Forbidden("not_a_member");
        }

        var amr = principal.FindFirstValue(LagerkraftClaims.AuthMethod) ?? "pwd";
        Guid? deviceId = Guid.TryParse(principal.FindFirstValue(LagerkraftClaims.DeviceId), out var dev)
            ? dev
            : null;
        return Results.Ok(await IssueAsync(jwt, refresh, membership, amr, deviceId, ct));
    }

    private static async Task<IResult> TotpEnroll(ClaimsPrincipal principal, UserManager<AppUser> users)
    {
        var user = await CurrentUser(principal, users);
        if (user is null)
        {
            return Unauthorized("invalid_token");
        }

        await users.ResetAuthenticatorKeyAsync(user);
        var secret = await users.GetAuthenticatorKeyAsync(user);
        var uri = $"otpauth://totp/Lagerkraft:{user.Email}?secret={secret}&issuer=Lagerkraft";
        return Results.Ok(new TotpEnrollResponse(secret ?? "", uri));
    }

    private static async Task<IResult> TotpConfirm(
        HttpContext http,
        ClaimsPrincipal principal,
        UserManager<AppUser> users)
    {
        var body = await http.Request.ReadFromJsonAsync<TotpConfirmRequest>();
        var user = await CurrentUser(principal, users);
        if (user is null)
        {
            return Unauthorized("invalid_token");
        }

        if (body is null
            || string.IsNullOrWhiteSpace(body.TotpCode)
            || !await users.VerifyTwoFactorTokenAsync(
                user, TokenOptions.DefaultAuthenticatorProvider, body.TotpCode))
        {
            return Unauthorized("totp_invalid");
        }

        await users.SetTwoFactorEnabledAsync(user, true);
        return Results.NoContent();
    }

    private static async Task<AppUser?> CurrentUser(ClaimsPrincipal principal, UserManager<AppUser> users)
    {
        return TryUserId(principal, out var id) ? await users.FindByIdAsync(id.ToString()) : null;
    }

    private static bool TryUserId(ClaimsPrincipal principal, out Guid userId)
    {
        var raw = principal.FindFirstValue(JwtRegisteredClaimNames.Sub)
            ?? principal.FindFirstValue("sub")
            ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(raw, out userId);
    }

    private static IQueryable<Membership> MembershipsFor(PlatformDbContext db, Guid userId) =>
        db.Memberships
            .Include(m => m.Tenant)
            .Include(m => m.RoleAssignments).ThenInclude(a => a.Role)
            .Where(m => m.UserId == userId);

    private static async Task<TokenResponse> IssueAsync(
        JwtIssuer jwt,
        RefreshTokenStore refresh,
        Membership membership,
        string amr,
        Guid? deviceId,
        CancellationToken ct)
    {
        var access = jwt.IssueAccess(membership, amr, deviceId);
        var refreshToken = await refresh.IssueAsync(membership, deviceId, ct);
        return new TokenResponse(access, refreshToken, "Bearer", JwtIssuer.AccessMinutes * 60);
    }

    private static void ResetPinLock(Membership membership)
    {
        membership.PinFailedAttempts = 0;
        membership.PinLockedUntil = null;
        membership.PinFullLoginRequired = false;
    }

    private static IResult Unauthorized(string code) =>
        Results.Json(new { code }, statusCode: StatusCodes.Status401Unauthorized);

    private static IResult Forbidden(string code) =>
        Results.Json(new { code }, statusCode: StatusCodes.Status403Forbidden);
}

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Lagerkraft.Shared.Auth;

public sealed class PermissionRequirement(string permission) : IAuthorizationRequirement
{
    public string Permission { get; } = permission;
}

public sealed class PermissionPolicyProvider(IOptions<AuthorizationOptions> options)
    : DefaultAuthorizationPolicyProvider(options)
{
    public const string Prefix = "permission:";

    public static string PolicyName(string permission) => Prefix + permission;

    public override async Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (policyName.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return new AuthorizationPolicyBuilder()
                .AddRequirements(new PermissionRequirement(policyName[Prefix.Length..]))
                .Build();
        }

        return await base.GetPolicyAsync(policyName);
    }
}

public sealed class PermissionAuthorizationHandler : AuthorizationHandler<PermissionRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        PermissionRequirement requirement)
    {
        var assignments = RoleAssignment.Parse(context.User);
        Guid? warehouseId = null;
        if (context.Resource is HttpContext http
            && http.Request.RouteValues.TryGetValue("warehouseId", out var raw)
            && Guid.TryParse(raw?.ToString(), out var id))
        {
            warehouseId = id;
        }

        if (Permissions.Allows(assignments, requirement.Permission, warehouseId))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

public static class PermissionEndpointExtensions
{
    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, string permission)
        where TBuilder : IEndpointConventionBuilder =>
        builder.RequireAuthorization(PermissionPolicyProvider.PolicyName(permission));
}

---
name: new-slice
description: Scaffolds a vertical-slice feature (endpoint, handler, validator, test) in one of the Lagerkraft .NET services following the project shape. Use when adding a REST endpoint or feature to platform, wms-core, sync-gateway or integrations, or when asked to "add an endpoint" or "add a feature slice".
disable-model-invocation: true
---

# New vertical slice

One folder per feature. No controllers, no MediatR, no repositories.

## Files

```
backend/src/<Service>/<Area>/<FeatureName>/
  Endpoint.cs     MapXxx(this IEndpointRouteBuilder) with RequirePermission(...)
  Handler.cs      static class with Request/Response records and Handle(...)
  Validator.cs    AbstractValidator<Request>
backend/tests/Lagerkraft.<Service>.Tests/<Area>/<FeatureName>Tests.cs
```

Register the endpoint in the area's `Map<Area>Endpoints` extension; register the validator by assembly scan (already configured).

## Templates

```csharp
// Endpoint.cs
public static class CreateEnrollmentCodeEndpoint
{
    public static IEndpointRouteBuilder MapCreateEnrollmentCode(this IEndpointRouteBuilder app)
    {
        app.MapPost("/devices/enrollment-codes", async (
                CreateEnrollmentCode.Request req, CreateEnrollmentCode.Handler h, CancellationToken ct)
            => (await h.Handle(req, ct)).ToHttp(StatusCodes.Status201Created))
           .RequirePermission(Permissions.DevicesManage)
           .WithTags("Devices");
        return app;
    }
}

// Handler.cs
public static class CreateEnrollmentCode
{
    public sealed record Request(Guid[]? WarehouseIds);
    public sealed record Response(string Code, DateTimeOffset ExpiresAt);

    public sealed class Handler(PlatformDbContext db, IClock clock, TenantContext tenant)
    {
        public async Task<Result<Response>> Handle(Request r, CancellationToken ct)
        {
            var code = EnrollmentCode.New(tenant.TenantId, r.WarehouseIds, clock.UtcNow.AddMinutes(15));
            db.EnrollmentCodes.Add(code);
            await db.SaveChangesAsync(ct);
            return new Response(code.Value, code.ExpiresAt);
        }
    }
}

// Validator.cs
public sealed class CreateEnrollmentCodeValidator : AbstractValidator<CreateEnrollmentCode.Request>
{
    public CreateEnrollmentCodeValidator() =>
        RuleFor(x => x.WarehouseIds).Must(ids => ids is null || ids.Length <= 50);
}
```

## Test shape

```csharp
[Collection(nameof(PostgresCollection))]
public sealed class CreateEnrollmentCodeTests(PlatformFactory f)
{
    [Fact]
    public async Task Create_AsWarehouseManager_Returns201AndSixDigits()
    {
        var client = f.ClientFor(Roles.WarehouseManager, warehouse: f.Seed.WarehouseId);
        var res = await client.PostAsJsonAsync("/devices/enrollment-codes", new { });
        res.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await res.Content.ReadFromJsonAsync<CreateEnrollmentCode.Response>())!.Code.ShouldMatch("^\\d{6}$");
    }

    [Fact]
    public async Task Create_AsViewer_Returns403() { ... }
}
```

## Checklist

- Permission constant exists in `Lagerkraft.Shared.Permissions` and the spec's matrix agrees.
- `Result` errors map to the right status (`not_found` 404, `forbidden` 403, `conflict` 409, validation 400).
- Anything that changes state a client cares about writes `change_log` (wms-core) or publishes an event (platform), not both.
- Internal endpoints go under `/internal/` and use `RequireInternalToken()` instead of `RequirePermission`.

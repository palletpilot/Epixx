using System.Security.Cryptography;
using System.Text;

namespace Lagerkraft.WmsCore.Api.Internal;

public sealed class InternalTokenFilter(IConfiguration configuration) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var expected = configuration["Internal:Token"] ?? "";
        var actual = context.HttpContext.Request.Headers["Lagerkraft-Internal-Token"].ToString();
        if (expected.Length == 0 || !FixedEquals(actual, expected))
        {
            return Results.Unauthorized();
        }

        return await next(context);
    }

    private static bool FixedEquals(string actual, string expected)
    {
        var a = Encoding.UTF8.GetBytes(actual);
        var b = Encoding.UTF8.GetBytes(expected);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }
}

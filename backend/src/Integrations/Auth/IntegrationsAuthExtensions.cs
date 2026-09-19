using System.Security.Cryptography;
using Lagerkraft.Shared.Auth;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;

namespace Lagerkraft.Integrations.Auth;

public static class IntegrationsAuthExtensions
{
    public const string Issuer = "lagerkraft";
    public const string Audience = "lagerkraft";

    public static IServiceCollection AddIntegrationsAuth(this IServiceCollection services, IConfiguration config)
    {
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IHttpClientFactory>((options, httpFactory) =>
            {
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = Issuer,
                    ValidAudience = Audience,
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ClockSkew = TimeSpan.Zero,
                    NameClaimType = "sub"
                };

                var pem = config["Jwt:PrivateKeyPem"];
                if (!string.IsNullOrWhiteSpace(pem))
                {
                    var rsa = RSA.Create();
                    rsa.ImportFromPem(pem);
                    options.TokenValidationParameters.IssuerSigningKey =
                        new RsaSecurityKey(rsa) { KeyId = "integrations-test-1" };
                }
                else
                {
                    var jwksUrl = config["Jwt:JwksUrl"]
                        ?? throw new InvalidOperationException("Jwt:JwksUrl or Jwt:PrivateKeyPem is required");
                    options.TokenValidationParameters.IssuerSigningKeyResolver =
                        (_, _, _, _) => LoadJwks(httpFactory, jwksUrl);
                }
            });
        services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
        services.AddSingleton<IAuthorizationHandler, PermissionAuthorizationHandler>();
        services.AddAuthorization();
        return services;
    }

    private static IEnumerable<SecurityKey> LoadJwks(IHttpClientFactory httpFactory, string jwksUrl)
    {
        var http = httpFactory.CreateClient();
        var json = http.GetStringAsync(jwksUrl).GetAwaiter().GetResult();
        var jwks = new JsonWebKeySet(json);
        return jwks.GetSigningKeys();
    }
}

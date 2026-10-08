using Microsoft.Agents.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Microsoft.IdentityModel.Validators;

namespace AnalyzerWebApi.Authentication;

public static class TeamsBotAuthentication
{
    public const string Scheme = "TeamsBot";
    public const string Policy = "TeamsBotMessages";

    private const string AzureBotScheme = "TeamsBot.AzureBotService";
    private const string EntraScheme = "TeamsBot.Entra";

    public static IServiceCollection AddTeamsBotAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var section = configuration.GetSection("TokenValidation");
        var audiences = section.GetSection("Audiences").Get<string[]>();
        var tenantId = section["TenantId"];
        var clientId = configuration["Connections:ServiceConnection:Settings:ClientId"];

        if (!section.GetValue<bool>("Enabled"))
        {
            throw new InvalidOperationException(
                "TokenValidation:Enabled doit être true pour le bot Teams.");
        }

        if (audiences is not { Length: 1 }
            || !Guid.TryParse(audiences[0], out var audienceId)
            || !Guid.TryParse(clientId, out var connectionId)
            || audienceId != connectionId
            || audienceId == Guid.Empty)
        {
            throw new InvalidOperationException(
                "TokenValidation:Audiences doit contenir l'ID du bot, identique à Connections:ServiceConnection:Settings:ClientId.");
        }

        if (!Guid.TryParse(tenantId, out var tenantGuid) || tenantGuid == Guid.Empty)
        {
            throw new InvalidOperationException(
                "TokenValidation:TenantId doit contenir l'ID du locataire TClient.");
        }

        // Émetteurs du cloud commercial repris de l'exemple Microsoft.
        // Le locataire du bot permet également les jetons Entra qui lui sont destinés.
        var tenantIds = new[]
        {
            "d6d49420-f39b-4df7-a1dc-d59a935871db",
            "f8cdef31-a31e-4b4a-93e4-5f571e91255a",
            "69e9b82d-4842-4902-8d1e-abc5b98a55e8",
            tenantGuid.ToString("D")
        };

        var entraIssuers = tenantIds.SelectMany(id => new[]
        {
            $"https://sts.windows.net/{id}/",
            $"https://login.microsoftonline.com/{id}/v2.0"
        }).ToArray();

        // L'appel sans paramètres conserve le schéma Bearer de l'API.
        services.AddAuthentication()
            .AddPolicyScheme(Scheme, Scheme, options =>
            {
                options.ForwardDefaultSelector = SelectScheme;
            })
            .AddJwtBearer(AzureBotScheme, options => ConfigureBearer(
                options,
                AuthenticationConstants.PublicAzureBotServiceOpenIdMetadataUrl,
                audiences,
                [AuthenticationConstants.BotFrameworkTokenIssuer]))
            .AddJwtBearer(EntraScheme, options =>
            {
                ConfigureBearer(
                    options,
                    AuthenticationConstants.PublicOpenIdMetadataUrl,
                    audiences,
                    entraIssuers);

                options.Events = new JwtBearerEvents
                {
                    OnTokenValidated = context =>
                    {
                        var tid = context.Principal?.FindFirst("tid")?.Value;
                        var issuer = context.Principal?.FindFirst("iss")?.Value;

                        if (!Guid.TryParse(tid, out var tokenTenant)
                            || (issuer != $"https://sts.windows.net/{tokenTenant:D}/"
                                && issuer != $"https://login.microsoftonline.com/{tokenTenant:D}/v2.0"))
                        {
                            context.Fail("Le locataire du jeton ne correspond pas à son émetteur.");
                        }

                        return Task.CompletedTask;
                    }
                };
            });

        services.AddAuthorization(options =>
        {
            options.AddPolicy(Policy, policy =>
            {
                policy.AddAuthenticationSchemes(Scheme);
                policy.RequireAuthenticatedUser();
            });
        });

        return services;
    }

    private static string SelectScheme(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var encodedToken = header[7..].Trim();
            var handler = new JsonWebTokenHandler();

            try
            {
                if (handler.CanReadToken(encodedToken))
                {
                    // Cette lecture choisit les métadonnées. Elle n'authentifie pas le jeton.
                    // Le schéma sélectionné vérifie ensuite signature, émetteur,
                    // audience et durée de validité.
                    var issuer = handler.ReadJsonWebToken(encodedToken).Issuer;
                    if (issuer != AuthenticationConstants.BotFrameworkTokenIssuer)
                    {
                        return EntraScheme;
                    }
                }
            }
            catch (Exception exception) when (
                exception is ArgumentException or SecurityTokenException)
            {
                // Le gestionnaire JWT rejettera ensuite le jeton mal formé.
            }
        }

        return AzureBotScheme;
    }

    private static void ConfigureBearer(
        JwtBearerOptions options,
        string metadataAddress,
        string[] audiences,
        string[] issuers)
    {
        options.MetadataAddress = metadataAddress;
        options.RequireHttpsMetadata = true;
        options.MapInboundClaims = false;
        options.IncludeErrorDetails = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuers = issuers,
            ValidateAudience = true,
            ValidAudiences = audiences,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(5),
            RequireExpirationTime = true,
            ValidateIssuerSigningKey = true,
            RequireSignedTokens = true
        };
        options.TokenValidationParameters.EnableAadSigningKeyIssuerValidation();
    }
}

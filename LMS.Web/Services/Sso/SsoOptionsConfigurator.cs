using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace LMS.Web.Services.Sso;

/// <summary>Supplies the OpenID Connect handler with options loaded from the database rather
/// than from static configuration, so an administrator can set up SSO in the UI without a
/// redeploy. The options monitor caches the result; <see cref="SsoOptionsCache"/> evicts it
/// when the configuration is saved, so changes take effect on the next request. (§AUTH-09)</summary>
public class SsoOptionsConfigurator : IConfigureNamedOptions<OpenIdConnectOptions>
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<SsoOptionsConfigurator> _log;

    public SsoOptionsConfigurator(IServiceScopeFactory scopes, ILogger<SsoOptionsConfigurator> log)
    { _scopes = scopes; _log = log; }

    public void Configure(OpenIdConnectOptions options) => Configure(SsoService.Scheme, options);

    public void Configure(string? name, OpenIdConnectOptions options)
    {
        if (name != SsoService.Scheme) return;

        using var scope = _scopes.CreateScope();
        var sso = scope.ServiceProvider.GetRequiredService<SsoService>();
        var cfg = sso.GetActiveAsync().GetAwaiter().GetResult();

        // The external identity lands in Identity's temporary external cookie, so
        // SignInManager.GetExternalLoginInfoAsync() can pick it up in the callback and the
        // application cookie is only issued after our own gates pass.
        options.SignInScheme = IdentityConstants.ExternalScheme;
        options.CallbackPath = "/signin-oidc";
        options.ResponseType = OpenIdConnectResponseType.Code;
        options.UsePkce = true;
        options.SaveTokens = false;                 // we need identity, not the IdP's access tokens
        options.GetClaimsFromUserInfoEndpoint = true;
        options.Scope.Clear();
        options.Scope.Add("openid"); options.Scope.Add("profile"); options.Scope.Add("email");
        options.MapInboundClaims = false;           // keep raw claim types (sub, email, groups)
        options.TokenValidationParameters.NameClaimType = "name";

        if (cfg == null)
        {
            // Not configured yet: give the handler a syntactically valid placeholder so the
            // scheme can exist; the sign-in page never offers SSO in this state.
            options.Authority = "https://sso.not-configured.invalid";
            options.ClientId = "not-configured";
            options.RequireHttpsMetadata = true;
            return;
        }

        options.Authority = cfg.Authority.TrimEnd('/');
        options.ClientId = cfg.ClientId;
        options.ClientSecret = sso.Unprotect(cfg.ClientSecretProtected);
        // Plain HTTP metadata is only tolerated for a loopback IdP (local test harness);
        // any real deployment must use HTTPS.
        var isLoopback = options.Authority.StartsWith("http://localhost", StringComparison.OrdinalIgnoreCase)
                      || options.Authority.StartsWith("http://127.0.0.1", StringComparison.OrdinalIgnoreCase);
        options.RequireHttpsMetadata = !isLoopback;

        // Group/role claims are read from the id_token in the callback (Entra ID, Keycloak
        // and Okta can all be configured to emit them there). A provider that exposes groups
        // only from the userinfo endpoint would need a ClaimAction — deferred to Phase 2.

        options.Events = new OpenIdConnectEvents
        {
            OnRemoteFailure = ctx =>
            {
                _log.LogWarning(ctx.Failure, "SSO sign-in failed at the identity provider.");
                ctx.Response.Redirect("/Account/Login?ssoError=1");
                ctx.HandleResponse();
                return Task.CompletedTask;
            }
        };
    }
}

/// <summary>Evicts the cached OIDC options so a saved configuration change applies immediately.</summary>
public class SsoOptionsCache
{
    private readonly IOptionsMonitorCache<OpenIdConnectOptions> _cache;
    public SsoOptionsCache(IOptionsMonitorCache<OpenIdConnectOptions> cache) => _cache = cache;
    public void Invalidate() => _cache.TryRemove(SsoService.Scheme);
}

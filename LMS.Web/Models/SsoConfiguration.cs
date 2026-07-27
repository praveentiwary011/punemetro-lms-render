using System.ComponentModel.DataAnnotations;

namespace LMS.Web.Models;

/// <summary>Single sign-on protocol. Phase 1 implements OpenID Connect; SAML 2.0 is the
/// planned second protocol and shares this configuration shape (§AUTH-09).</summary>
public enum SsoProtocol { Oidc = 0, Saml2 = 1 }

/// <summary>Per-organisation single sign-on configuration. SSO is a tenant-level decision —
/// each client organisation federates with its own identity provider — so this hangs off
/// Organisation rather than being a platform-wide setting.</summary>
public class SsoConfiguration
{
    public int Id { get; set; }
    public int OrganisationId { get; set; }
    public Organisation? Organisation { get; set; }

    public SsoProtocol Protocol { get; set; } = SsoProtocol.Oidc;
    public bool IsEnabled { get; set; }

    /// <summary>Button label on the sign-in page, e.g. "Sign in with Pune Metro account".</summary>
    [MaxLength(100)] public string DisplayName { get; set; } = "your organisation account";

    /// <summary>OIDC authority (issuer) URL — discovery is read from {Authority}/.well-known/openid-configuration.</summary>
    [MaxLength(300)] public string Authority { get; set; } = "";
    [MaxLength(200)] public string ClientId { get; set; } = "";

    /// <summary>Client secret, protected with the Data Protection API — never stored in clear
    /// and never returned to the browser.</summary>
    public string? ClientSecretProtected { get; set; }

    /// <summary>Comma-separated email domains that route to this IdP (home-realm discovery).</summary>
    [MaxLength(300)] public string EmailDomains { get; set; } = "";

    /// <summary>Claim carrying the user's groups/roles at the IdP (e.g. "groups", "roles").</summary>
    [MaxLength(100)] public string? RoleClaimName { get; set; }

    /// <summary>Newline-separated "IdP group = LMS organisation role" mappings.</summary>
    public string? RoleMappings { get; set; }

    /// <summary>Create an LMS account on first successful SSO sign-in (restricted to EmailDomains).</summary>
    public bool JitProvisioning { get; set; } = true;

    /// <summary>Organisation role given to a JIT-created user with no mapped group.</summary>
    [MaxLength(100)] public string DefaultRole { get; set; } = "Trainee";

    /// <summary>Break-glass: when false, this organisation's users must use SSO — but Super
    /// Users can always sign in with a password so an IdP outage can never lock everyone out.</summary>
    public bool AllowLocalPassword { get; set; } = true;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

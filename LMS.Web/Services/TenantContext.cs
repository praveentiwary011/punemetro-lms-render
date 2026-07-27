using System.Security.Claims;

namespace LMS.Web.Services;

/// <summary>
/// Per-request tenant scope, resolved from the signed-in principal. Used by the
/// AppDbContext global query filters so tenant isolation is the default: an
/// ordinary query returns only the caller's organisation's rows, and returning
/// another tenant's rows requires an explicit <c>IgnoreQueryFilters()</c>.
/// </summary>
public interface ITenantContext
{
    /// <summary>The signed-in user's organisation id, or null when unknown.</summary>
    int? OrganisationId { get; }

    /// <summary>True when the caller may see across all tenants — the platform owner
    /// (Super User), or trusted server code running without an HTTP request
    /// (seeding, background services). Such callers bypass the tenant filter.</summary>
    bool Unrestricted { get; }
}

/// <summary>Claim type carrying the user's organisation id on their principal.</summary>
public static class TenantClaims
{
    public const string OrganisationId = "org_id";
}

/// <summary>Resolves <see cref="ITenantContext"/> from the current HTTP request's
/// authenticated principal. No HTTP context (background work / seeding) and
/// unauthenticated requests (e.g. the login and public-verify paths) are treated as
/// unrestricted, because they cannot reach tenant-scoped authorised endpoints.</summary>
public class HttpTenantContext : ITenantContext
{
    private readonly IHttpContextAccessor _accessor;
    public HttpTenantContext(IHttpContextAccessor accessor) => _accessor = accessor;

    private ClaimsPrincipal? Principal => _accessor.HttpContext?.User;

    public bool Unrestricted =>
        _accessor.HttpContext == null ||
        Principal?.Identity?.IsAuthenticated != true ||
        Principal.IsInRole("SuperUser");

    public int? OrganisationId =>
        int.TryParse(Principal?.FindFirst(TenantClaims.OrganisationId)?.Value, out var id) ? id : null;
}

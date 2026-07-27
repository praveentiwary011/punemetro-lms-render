using LMS.Web.Data;
using LMS.Web.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace LMS.Web.Services.Sso;

/// <summary>Reads and writes per-organisation SSO configuration, protects the client secret,
/// and resolves which identity provider an email address belongs to (home-realm discovery).
/// (§AUTH-09/10)</summary>
public class SsoService
{
    public const string Scheme = "oidc";
    private const string Purpose = "LMS.sso.secret.v1";

    private readonly AppDbContext _db;
    private readonly IDataProtector _protector;

    public SsoService(AppDbContext db, IDataProtectionProvider dp)
    { _db = db; _protector = dp.CreateProtector(Purpose); }

    /// <summary>The single enabled OIDC configuration (Phase 1 supports one federated
    /// organisation per installation; the table is already keyed per organisation so
    /// Phase 2 can serve several without a schema change).</summary>
    public Task<SsoConfiguration?> GetActiveAsync() =>
        _db.SsoConfigurations.IgnoreQueryFilters().AsNoTracking()
           .Include(s => s.Organisation)
           .FirstOrDefaultAsync(s => s.IsEnabled && s.Protocol == SsoProtocol.Oidc);

    public Task<SsoConfiguration?> GetForOrganisationAsync(int organisationId) =>
        _db.SsoConfigurations.IgnoreQueryFilters()
           .FirstOrDefaultAsync(s => s.OrganisationId == organisationId);

    public string Protect(string secret) => _protector.Protect(secret);

    public string? Unprotect(string? protectedSecret)
    {
        if (string.IsNullOrEmpty(protectedSecret)) return null;
        try { return _protector.Unprotect(protectedSecret); }
        catch { return null; }   // key ring rotated / corrupt value — treat as unset
    }

    /// <summary>True when this email's domain is federated by the given configuration.</summary>
    public static bool DomainMatches(SsoConfiguration cfg, string? email)
    {
        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@')) return false;
        var domain = email.Split('@')[^1].Trim().ToLowerInvariant();
        return cfg.EmailDomains.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                  .Any(d => string.Equals(d.Trim().TrimStart('@'), domain, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Maps the IdP's group/role claim values onto this organisation's own role
    /// names, using the admin's "IdP group = LMS role" lines. Unmapped users get DefaultRole.</summary>
    public static List<string> MapRoles(SsoConfiguration cfg, IEnumerable<string> idpGroups)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in (cfg.RoleMappings ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('=', 2);
            if (parts.Length == 2 && parts[0].Trim().Length > 0 && parts[1].Trim().Length > 0)
                map[parts[0].Trim()] = parts[1].Trim();
        }
        var roles = idpGroups.Where(g => map.ContainsKey(g)).Select(g => map[g]).Distinct().ToList();
        if (roles.Count == 0 && !string.IsNullOrWhiteSpace(cfg.DefaultRole)) roles.Add(cfg.DefaultRole.Trim());
        return roles;
    }
}

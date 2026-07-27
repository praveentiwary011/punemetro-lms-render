using System.Security.Claims;
using LMS.Web.Data;
using LMS.Web.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace LMS.Web.Services;

/// <summary>
/// Role mapping for organisation-defined custom roles: when the user's principal is
/// built, every custom role they hold contributes a role claim for its mapped
/// platform role (Student/Instructor/Principal/Admin). Authorisation, menus and
/// dashboards therefore treat "Station Controller (maps to Trainer)" exactly like a
/// Trainer — no duplicate role bookkeeping on the user record. Because the security
/// stamp is validated (and the principal rebuilt) on every request, mapping changes
/// take effect immediately without re-login.
/// </summary>
public class MappedRolesClaimsFactory : UserClaimsPrincipalFactory<ApplicationUser, IdentityRole>
{
    private readonly AppDbContext _db;

    public MappedRolesClaimsFactory(UserManager<ApplicationUser> userManager,
        RoleManager<IdentityRole> roleManager, IOptions<IdentityOptions> options, AppDbContext db)
        : base(userManager, roleManager, options) => _db = db;

    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(ApplicationUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        var roleClaimType = Options.ClaimsIdentity.RoleClaimType;
        var held = identity.FindAll(roleClaimType).Select(c => c.Value).ToHashSet();

        // Tenant scope claim consumed by ITenantContext / the DbContext query filters.
        // Rebuilt every request (security stamp is validated each request), so an org
        // change takes effect immediately.
        if (user.OrganisationId != null)
            identity.AddClaim(new Claim(TenantClaims.OrganisationId, user.OrganisationId.Value.ToString()));

        // Organisation-scoped: role names may repeat across tenants (each org can
        // have its own "Trainee"/"Trainer"), so only the user's own organisation's
        // mapping applies.
        var mappedBases = await _db.OrganisationRoles.AsNoTracking()
            .Where(r => r.OrganisationId == user.OrganisationId && held.Contains(r.Name))
            .Select(r => r.MapsToRole)
            .Distinct()
            .ToListAsync();

        foreach (var baseRole in mappedBases)
            if (!string.IsNullOrEmpty(baseRole) && held.Add(baseRole))
                identity.AddClaim(new Claim(roleClaimType, baseRole));

        return identity;
    }
}

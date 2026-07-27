using LMS.Web.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace LMS.Web.Data;

/// <summary>
/// Write-side tenant guard, complementing the read-side global query filters. For a
/// tenant-restricted caller it auto-stamps the owning organisation id on new
/// tenant-owned rows and rejects any insert / update / delete that would touch another
/// tenant's data. Unrestricted callers — the platform owner (Super User) and trusted
/// server code with no request context (seeding, background services) — pass through
/// untouched. This makes cross-tenant writes fail closed even if a controller ever
/// forgets an explicit check.
/// </summary>
public sealed class TenantSaveChangesInterceptor : SaveChangesInterceptor
{
    // Tenant-owned entity -> the property that carries its owning organisation id.
    private static readonly Dictionary<Type, string> TenantKey = new()
    {
        [typeof(ApplicationUser)] = "OrganisationId",
        [typeof(Course)] = "OrganisationId",
        [typeof(OrganisationRole)] = "OrganisationId",
        [typeof(TrainingLocation)] = "OrganisationId",
        [typeof(SubscriptionLicense)] = "OrganisationId",
        [typeof(Organisation)] = "Id",
    };

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Enforce(eventData.Context as AppDbContext);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Enforce(eventData.Context as AppDbContext);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private static void Enforce(AppDbContext? db)
    {
        if (db == null) return;
        var (unrestricted, tenantId) = db.TenantWriteScope;
        if (unrestricted) return;   // Super User / seeding / background services

        foreach (var entry in db.ChangeTracker.Entries())
        {
            if (!TenantKey.TryGetValue(entry.Metadata.ClrType, out var keyProp)) continue;
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted)) continue;

            var prop = entry.Property(keyProp);
            var isOrganisation = entry.Metadata.ClrType == typeof(Organisation);

            if (entry.State == EntityState.Added && !isOrganisation)
            {
                var current = prop.CurrentValue as int?;
                if (current is null or 0)
                {
                    // New tenant-owned row without an organisation → stamp the caller's.
                    if (tenantId == null) throw Deny(entry.Metadata.ClrType, "insert without a tenant scope");
                    prop.CurrentValue = tenantId;
                }
                else if (current != tenantId)
                {
                    throw Deny(entry.Metadata.ClrType, "cross-tenant insert");
                }
            }
            else
            {
                // Modified/Deleted (any entity), or an Organisation being inserted:
                // the owning id must be the caller's own tenant.
                var owner = (entry.State == EntityState.Added ? prop.CurrentValue : prop.OriginalValue) as int?;
                if (owner != tenantId) throw Deny(entry.Metadata.ClrType, $"cross-tenant {entry.State.ToString().ToLowerInvariant()}");
            }
        }
    }

    private static InvalidOperationException Deny(Type entity, string reason) =>
        new($"Tenant write rejected for {entity.Name}: {reason}.");
}

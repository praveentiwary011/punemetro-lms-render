using LMS.Web.Data;
using LMS.Web.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace LMS.Web.Services;

/// <summary>
/// Background reminder schedule for subscription licensing (LIC):
/// the tenant's Administrators receive the first expiry notification two months
/// (61 days) before the organisation's license coverage ends, then one reminder
/// per week until expiry. A renewal that extends coverage beyond the two-month
/// window stops the reminders automatically, because the schedule always looks
/// at the organisation's LATEST license end date. The send timestamp is kept on
/// that license row (LastExpiryNotifiedAt), so restarts never duplicate reminders.
/// The platform owner's organisation is exempt.
/// </summary>
public class LicenseExpiryNotifier : BackgroundService
{
    private const int FirstNoticeDays = 61;         // ~2 months before expiry
    private static readonly TimeSpan WeeklyGap = TimeSpan.FromDays(7);
    private static readonly TimeSpan CheckEvery = TimeSpan.FromHours(12);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<LicenseExpiryNotifier> _log;

    public LicenseExpiryNotifier(IServiceScopeFactory scopeFactory, ILogger<LicenseExpiryNotifier> log)
    {
        _scopeFactory = scopeFactory;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken); // let startup/seeding finish
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (Exception ex) { _log.LogError(ex, "License expiry check failed"); }
            await Task.Delay(CheckEvery, stoppingToken);
        }
    }

    public async Task RunOnceAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var today = DateTime.UtcNow.Date;
        var admins = await userManager.GetUsersInRoleAsync("Admin");

        var orgs = await db.Organisations
            .Where(o => o.IsActive && o.Code != Branding.OwnerOrgCode && o.Licenses.Any())
            .Select(o => new { Org = o, Latest = o.Licenses.OrderByDescending(l => l.EndDate).First() })
            .ToListAsync(ct);

        foreach (var x in orgs)
        {
            if (x.Latest.ValidityType == LicenseValidityType.NeverExpires) continue; // perpetual — never expires
            var daysLeft = (int)(x.Latest.EndDate.Date - today).TotalDays;
            if (daysLeft < 0 || daysLeft > FirstNoticeDays) continue;           // outside the reminder window
            if (x.Latest.LastExpiryNotifiedAt is DateTime last &&
                DateTime.UtcNow - last < WeeklyGap) continue;                    // weekly cadence

            var recipients = admins.Where(a => a.OrganisationId == x.Org.Id && a.IsActive).ToList();
            if (recipients.Count == 0) continue;

            var message = $"Subscription license for {x.Org.Name} expires on {x.Latest.EndDate:dd MMM yyyy} " +
                          $"({(daysLeft == 0 ? "today" : daysLeft + " day(s) remaining")}). Please arrange renewal with the platform provider.";
            foreach (var admin in recipients)
                Notifier.Notify(db, admin.Id, message, "/Admin/Dashboard");

            var tracked = await db.SubscriptionLicenses.FirstAsync(l => l.Id == x.Latest.Id, ct);
            tracked.LastExpiryNotifiedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            _log.LogInformation("License expiry reminder sent for {Org} ({Days} days left) to {Count} admin(s)",
                x.Org.Name, daysLeft, recipients.Count);
        }
    }
}

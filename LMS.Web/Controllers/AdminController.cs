using LMS.Web.Data;
using LMS.Web.Models;
using LMS.Web.Services;
using LMS.Web.Services.Grading;
using LMS.Web.Services.Sso;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LMS.Web.Controllers;

[Authorize(Roles = "Admin")]
public class AdminController : Controller
{
    private readonly AppDbContext _db;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly RoleManager<IdentityRole> _roleManager;

    private readonly Services.Email.MailSettingsStore _mail;
    private readonly Services.Email.EmailQueue _emailQueue;

    public AdminController(AppDbContext db, UserManager<ApplicationUser> userManager, RoleManager<IdentityRole> roleManager,
        IWebHostEnvironment env, Services.Email.MailSettingsStore mail, Services.Email.EmailQueue emailQueue)
    {
        _db = db;
        _userManager = userManager;
        _roleManager = roleManager;
        _env = env;
        _mail = mail;
        _emailQueue = emailQueue;
    }

    // ---------- Email notifications (§NOT-05..07) ----------

    public async Task<IActionResult> MailSettings()
    {
        var s = await _mail.LoadAsync();
        ViewBag.HasPassword = !string.IsNullOrEmpty(s.Password);
        ViewBag.Queued = await _db.EmailOutbox.CountAsync(e => e.SentAt == null);
        ViewBag.Sent = await _db.EmailOutbox.CountAsync(e => e.SentAt != null);
        ViewBag.Failing = await _db.EmailOutbox
            .Where(e => e.SentAt == null && e.LastError != null)
            .OrderByDescending(e => e.LastAttemptAt).Take(5).ToListAsync();
        return View(s);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveMailSettings(bool enabled, string host, int port, bool useStartTls,
        string? user, string? password, string fromAddress, string? fromName, string? baseUrl)
    {
        if (enabled && (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(fromAddress)))
        {
            TempData["Err"] = "A mail server and a From address are required before notifications can be enabled.";
            return RedirectToAction("MailSettings");
        }

        var values = new Dictionary<string, string>
        {
            [Services.Email.MailSettingsStore.KeyEnabled] = enabled ? "true" : "false",
            [Services.Email.MailSettingsStore.KeyHost] = (host ?? "").Trim(),
            [Services.Email.MailSettingsStore.KeyPort] = (port <= 0 ? 587 : port).ToString(),
            [Services.Email.MailSettingsStore.KeyStartTls] = useStartTls ? "true" : "false",
            [Services.Email.MailSettingsStore.KeyUser] = (user ?? "").Trim(),
            [Services.Email.MailSettingsStore.KeyFromAddress] = (fromAddress ?? "").Trim(),
            [Services.Email.MailSettingsStore.KeyFromName] = (fromName ?? "").Trim(),
            [Services.Email.MailSettingsStore.KeyBaseUrl] = (baseUrl ?? "").Trim().TrimEnd('/')
        };
        // Blank means "keep the stored password" — it is never sent back to the browser,
        // so an empty field cannot be taken as an instruction to clear it.
        if (!string.IsNullOrEmpty(password))
            values[Services.Email.MailSettingsStore.KeyPassword] = _mail.Protect(password);

        await _mail.SaveAsync(values);
        Notifier.Audit(_db, User.GetUserId(), User.Identity!.Name ?? "", "UpdateMailSettings",
            enabled ? $"enabled: {host}:{port}" : "disabled");
        await _db.SaveChangesAsync();
        TempData["Ok"] = "Mail settings saved.";
        return RedirectToAction("MailSettings");
    }

    /// <summary>Runs a scheduled digest immediately instead of waiting for its slot.
    /// The jobs are idempotent — the outbox dedupe key means a learner who already has
    /// this week's digest (or tomorrow's reminder) queued will not get a second one —
    /// so this is safe to press more than once.</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> RunEmailJob(string job,
        [FromServices] Services.Email.NewCourseDigestJob weekly,
        [FromServices] Services.Email.UpcomingReminderJob reminders)
    {
        var queued = job == "weekly"
            ? await weekly.RunAsync(HttpContext.RequestAborted, ignoreSchedule: true)
            : await reminders.RunAsync(HttpContext.RequestAborted, ignoreSchedule: true);

        var what = job == "weekly" ? "New-course digest" : "Day-before reminders";
        TempData["Ok"] = queued == 0
            ? $"{what}: nothing to send right now."
            : $"{what}: {queued} message(s) queued.";
        Notifier.Audit(_db, User.GetUserId(), User.Identity!.Name ?? "", "RunEmailJob", $"{job}: {queued} queued");
        await _db.SaveChangesAsync();
        return RedirectToAction("MailSettings");
    }

    /// <summary>Queues one test message to the address given. It goes through the same
    /// outbox and worker as everything else, so a success here proves the whole path.</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SendTestEmail(string to)
    {
        var s = await _mail.LoadAsync();
        if (!s.IsUsable)
        {
            TempData["Err"] = "Configure and enable the mail server first.";
            return RedirectToAction("MailSettings");
        }
        if (string.IsNullOrWhiteSpace(to))
        {
            TempData["Err"] = "Enter an address to send the test to.";
            return RedirectToAction("MailSettings");
        }

        var orgId = await CallerOrganisationIdAsync();
        var orgName = orgId == null ? "" :
            await _db.Organisations.Where(o => o.Id == orgId).Select(o => o.Name).FirstOrDefaultAsync() ?? "";
        var (subject, html) = Services.Email.EmailTemplates.Test(orgName);
        await _emailQueue.EnqueueAsync(to.Trim(), "", subject, html, EmailKind.Test, null, orgId);

        TempData["Ok"] = $"Test message queued for {to.Trim()} — it is sent within a minute.";
        return RedirectToAction("MailSettings");
    }
    private readonly IWebHostEnvironment _env;

    /// <summary>Stores a tenant logo under wwwroot/uploads/logos and returns its URL.
    /// Raster images only (no SVG — script-capable), max 2 MB. Returns null and sets
    /// TempData["Err"] when the file is rejected.</summary>
    private async Task<string?> SaveLogoAsync(IFormFile? logo)
    {
        if (logo == null || logo.Length == 0) return null;
        // Validate by decoding the image (not the extension) and re-encode to a clean
        // PNG — this rejects a script/HTML payload wearing a .png name and strips any
        // embedded content, so the served logo can never carry active markup.
        var url = await UploadHelper.SaveImageAsync(logo, _env, "logos", 2_000_000);
        if (url == null)
            TempData["Err"] = "Logo must be a valid image (PNG, JPG or WebP) under 2 MB.";
        return url;
    }

    public async Task<IActionResult> Dashboard()
    {
        var vm = await DashboardBuilder.BuildAdminAsync(_db, showAudit: true);
        return View(vm);
    }

    // ---------- Users ----------
    public async Task<IActionResult> Users(string? q, string? role, int? organisationId)
    {
        var term = q?.Trim().ToLower();
        var users = await _db.Users
            .Include(u => u.Organisation)
            .Where(u => term == null ||
                        u.FullName.ToLower().Contains(term) ||
                        u.Email!.ToLower().Contains(term) ||
                        u.Department.ToLower().Contains(term))
            .Where(u => organisationId == null || u.OrganisationId == organisationId)
            .OrderBy(u => u.FullName).ToListAsync();

        var withRoles = new List<(ApplicationUser User, IList<string> Roles)>();
        foreach (var u in users)
        {
            var roles = await _userManager.GetRolesAsync(u);
            if (role == null || roles.Contains(role))
                withRoles.Add((u, roles));
        }
        ViewBag.Query = q;
        ViewBag.Role = role;
        ViewBag.OrganisationId = organisationId;
        ViewBag.Organisations = await _db.Organisations.OrderBy(o => o.Name).ToListAsync();
        ViewBag.CustomRoles = await _db.OrganisationRoles.OrderBy(r => r.Name).Select(r => r.Name).Distinct().ToListAsync();
        // Custom role names per organisation, so the listing can show each user's
        // organisation-specific role(s) instead of the built-in platform roles.
        var orgRolePairs = await _db.OrganisationRoles.Select(r => new { r.OrganisationId, r.Name }).ToListAsync();
        ViewBag.OrgRoleNames = orgRolePairs.GroupBy(p => p.OrganisationId)
            .ToDictionary(g => g.Key, g => g.Select(p => p.Name).ToHashSet());
        // Company roles offered in the Create-user form: the caller's own organisation's
        // roles (a Super User, who picks the tenant, sees them all).
        if (User.IsInRole("SuperUser"))
            ViewBag.CreateRoles = orgRolePairs.Select(p => p.Name).Distinct().OrderBy(n => n).ToList();
        else
        {
            var callerOrg = await _db.Users.Where(u => u.Id == User.GetUserId()).Select(u => u.OrganisationId).FirstOrDefaultAsync();
            ViewBag.CreateRoles = orgRolePairs.Where(p => p.OrganisationId == callerOrg).Select(p => p.Name).OrderBy(n => n).ToList();
        }
        return View(withRoles);
    }

    private static readonly string[] AssignableRoles = { "Student", "Instructor", "Principal", "Admin" };

    /// <summary>The platform owner's organisation. Super User accounts always belong here.</summary>
    private async Task<int?> OwnerOrgIdAsync() =>
        await _db.Organisations.Where(o => o.Code == "ABSOLUTESYS").Select(o => (int?)o.Id).FirstOrDefaultAsync();

    /// <summary>Invariant: an account holding the SuperUser role is always tagged to
    /// AbsoluteSYS (the organisation that owns the application), never a client tenant.</summary>
    private async Task EnforceSuperUserOrgAsync(ApplicationUser user)
    {
        if (!await _userManager.IsInRoleAsync(user, "SuperUser")) return;
        var ownerId = await OwnerOrgIdAsync();
        if (ownerId != null && user.OrganisationId != ownerId)
        {
            user.OrganisationId = ownerId;
            await _userManager.UpdateAsync(user);
        }
    }

    /// <summary>Built-in roles plus every organisation-defined custom role.
    /// Only a Super User may grant or revoke the SuperUser role.</summary>
    private async Task<List<string>> AllAssignableRolesAsync()
    {
        var roles = AssignableRoles.Concat(await _db.OrganisationRoles.Select(r => r.Name).ToListAsync()).Distinct().ToList();
        if (User.IsInRole("SuperUser")) roles.Add("SuperUser");
        return roles;
    }

    /// <summary>Admins assign an organisation's own (company) roles; each maps to a built-in
    /// platform role. This adds the mapped built-in Identity role alongside every company
    /// role so authorisation and role enumeration keep working even though the UI only shows
    /// the company roles.</summary>
    private async Task<List<string>> WithMappedBaseRolesAsync(IEnumerable<string> roleNames, int? organisationId)
    {
        var result = new HashSet<string>(roleNames, StringComparer.Ordinal);
        if (organisationId != null)
        {
            var names = result.ToList();
            var baseRoles = await _db.OrganisationRoles
                .Where(r => r.OrganisationId == organisationId && names.Contains(r.Name))
                .Select(r => r.MapsToRole).ToListAsync();
            foreach (var b in baseRoles) result.Add(b);
        }
        return result.ToList();
    }

    /// <summary>Tenant + privilege guard for by-id user administration. Identity's
    /// <c>FindByIdAsync</c> uses <c>DbSet.FindAsync</c>, which BYPASSES the global
    /// query filters, so a target resolved by id could belong to any tenant. Every
    /// action that loads a user by id must call this first: a non-Super-User admin may
    /// act only on users in their own organisation, and never on a Super User account.</summary>
    private async Task<bool> CanManageAsync(ApplicationUser target)
    {
        if (User.IsInRole("SuperUser")) return true;
        if (await _userManager.IsInRoleAsync(target, "SuperUser")) return false;
        var callerOrg = await _db.Users.Where(u => u.Id == User.GetUserId())
            .Select(u => u.OrganisationId).FirstOrDefaultAsync();
        return callerOrg != null && target.OrganisationId == callerOrg;
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateUser(string fullName, string email, string password, List<string>? roles, int? organisationId)
    {
        // Only the Super User places users into arbitrary tenants; an org-scoped
        // admin always creates users inside their own organisation.
        if (!User.IsInRole("SuperUser"))
            organisationId = await _db.Users.Where(u => u.Id == User.GetUserId())
                .Select(u => u.OrganisationId).FirstOrDefaultAsync();
        if (organisationId != null && !await _db.Organisations.AnyAsync(o => o.Id == organisationId))
            organisationId = null;

        var valid = (roles ?? new()).Intersect(await AllAssignableRolesAsync()).ToList();
        // Every role chosen brings its mapped built-in along; default a role-less user to Trainee.
        var expanded = await WithMappedBaseRolesAsync(valid, organisationId);
        if (expanded.Count == 0) expanded.Add("Student");

        var user = new ApplicationUser { UserName = email, Email = email, FullName = fullName, EmailConfirmed = true, OrganisationId = organisationId };
        var result = await _userManager.CreateAsync(user, password);
        if (result.Succeeded)
        {
            await _userManager.AddToRolesAsync(user, expanded);
            await EnforceSuperUserOrgAsync(user);
            Notifier.Audit(_db, User.GetUserId(), User.Identity!.Name ?? "", "CreateUser", $"{email} as {string.Join(", ", valid)}");
            await _db.SaveChangesAsync();
            TempData["Ok"] = $"User {email} created.";
        }
        else TempData["Err"] = string.Join(" ", result.Errors.Select(e => e.Description));
        return RedirectToAction("Users");
    }

    /// <summary>Replace the user's roles with exactly the checked set (a user can hold several roles at once).</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SetUserRoles(string id, List<string>? roles)
    {
        var user = await _userManager.FindByIdAsync(id);
        if (user == null) return NotFound();
        if (!await CanManageAsync(user)) return Forbid();

        var target = (roles ?? new()).Intersect(await AllAssignableRolesAsync()).ToList();
        if (target.Count == 0)
        {
            TempData["Err"] = "A user must keep at least one role.";
            return RedirectToAction("Users");
        }

        // Keep the built-in role each chosen company role maps to, so authorisation works.
        target = await WithMappedBaseRolesAsync(target, user.OrganisationId);

        var current = await _userManager.GetRolesAsync(user);
        var toRemove = current.Except(target).ToList();
        var toAdd = target.Except(current).ToList();
        if (toRemove.Count > 0) await _userManager.RemoveFromRolesAsync(user, toRemove);
        if (toAdd.Count > 0) await _userManager.AddToRolesAsync(user, toAdd);
        await EnforceSuperUserOrgAsync(user);

        Notifier.Audit(_db, User.GetUserId(), User.Identity!.Name ?? "", "SetUserRoles", $"{user.Email} -> {string.Join(", ", target)}");
        await _db.SaveChangesAsync();
        TempData["Ok"] = $"Roles updated for {user.Email}: {string.Join(", ", target)}.";
        return RedirectToAction("Users");
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SetUserRole(string id, string role)
    {
        var user = await _userManager.FindByIdAsync(id);
        if (user == null) return NotFound();
        if (!await CanManageAsync(user)) return Forbid();
        var current = await _userManager.GetRolesAsync(user);
        await _userManager.RemoveFromRolesAsync(user, current);
        await _userManager.AddToRoleAsync(user, role);
        await EnforceSuperUserOrgAsync(user);
        Notifier.Audit(_db, User.GetUserId(), User.Identity!.Name ?? "", "SetUserRole", $"{user.Email} -> {role}");
        await _db.SaveChangesAsync();
        TempData["Ok"] = $"Role updated for {user.Email}.";
        return RedirectToAction("Users");
    }

    /// <summary>Add or remove a single role without touching the user's other roles.</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleUserRole(string id, string role)
    {
        var user = await _userManager.FindByIdAsync(id);
        if (user == null) return NotFound();
        if (!await CanManageAsync(user)) return Forbid();
        var current = await _userManager.GetRolesAsync(user);
        if (current.Contains(role))
        {
            if (current.Count == 1)
            {
                TempData["Err"] = "A user must keep at least one role.";
                return RedirectToAction("Users");
            }
            await _userManager.RemoveFromRoleAsync(user, role);
            TempData["Ok"] = $"Removed {role} role from {user.Email}.";
        }
        else
        {
            await _userManager.AddToRoleAsync(user, role);
            TempData["Ok"] = $"Added {role} role to {user.Email}.";
        }
        await EnforceSuperUserOrgAsync(user);
        Notifier.Audit(_db, User.GetUserId(), User.Identity!.Name ?? "", "ToggleUserRole", $"{user.Email}: {role}");
        await _db.SaveChangesAsync();
        return RedirectToAction("Users");
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleUserActive(string id)
    {
        var user = await _userManager.FindByIdAsync(id);
        if (user == null) return NotFound();
        if (!await CanManageAsync(user)) return Forbid();
        user.IsActive = !user.IsActive;
        await _userManager.UpdateAsync(user);
        Notifier.Audit(_db, User.GetUserId(), User.Identity!.Name ?? "", user.IsActive ? "ActivateUser" : "DeactivateUser", user.Email);
        await _db.SaveChangesAsync();
        return RedirectToAction("Users");
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ResetPassword(string id, string newPassword)
    {
        var user = await _userManager.FindByIdAsync(id);
        if (user == null) return NotFound();
        if (!await CanManageAsync(user)) return Forbid();
        var token = await _userManager.GeneratePasswordResetTokenAsync(user);
        var result = await _userManager.ResetPasswordAsync(user, token, newPassword);
        TempData[result.Succeeded ? "Ok" : "Err"] = result.Succeeded
            ? $"Password reset for {user.Email}."
            : string.Join(" ", result.Errors.Select(e => e.Description));
        return RedirectToAction("Users");
    }

    // ---------- Organisations (multi-tenancy) ----------
    // Tenant onboarding and management is reserved for the platform Super User
    // (who also holds Admin). A regular Admin administers within tenants only.
    [Authorize(Roles = "SuperUser")]
    public async Task<IActionResult> Organisations()
    {
        var orgs = await _db.Organisations
            .Select(o => new OrganisationRow
            {
                Org = o,
                UserCount = o.Users.Count,
                CourseCount = o.Courses.Count,
                RoleCount = o.Roles.Count,
                LicenseEnd = o.Licenses.Max(l => (DateTime?)l.EndDate),
                LicensePerpetual = o.Licenses.Any(l => l.ValidityType == LicenseValidityType.NeverExpires)
            })
            .OrderBy(x => x.Org.Name).ToListAsync();
        return View(orgs);
    }

    public class OrganisationRow
    {
        public Organisation Org { get; set; } = null!;
        public int UserCount { get; set; }
        public int CourseCount { get; set; }
        public int RoleCount { get; set; }
        /// <summary>End of the organisation's latest license period (null = never licensed).</summary>
        public DateTime? LicenseEnd { get; set; }
        /// <summary>True when the organisation holds a perpetual (never-expires) license.</summary>
        public bool LicensePerpetual { get; set; }
    }

    /// <summary>Builds a subscription license from the Super User's entry (§LIC).
    /// Exactly one of the three validity options must be supplied: an end date
    /// (date range), a number of months, or a number of days — months/days run
    /// from the start date, inclusive.</summary>
    private (SubscriptionLicense? License, string? Error) BuildLicense(
        int organisationId, DateTime? startDate, DateTime? endDate, int? months, int? days, string? reference,
        bool neverExpires = false)
    {
        var provided = (endDate != null ? 1 : 0) + (months != null ? 1 : 0) + (days != null ? 1 : 0) + (neverExpires ? 1 : 0);
        if (provided != 1)
            return (null, "Enter exactly one validity option: an end date (date range), a number of months, a number of days, or never expires.");

        var start = (startDate ?? DateTime.UtcNow).Date;
        DateTime end; LicenseValidityType type; int? value = null;
        if (neverExpires) { end = SubscriptionLicense.PerpetualEndDate; type = LicenseValidityType.NeverExpires; }
        else if (endDate != null) { end = endDate.Value.Date; type = LicenseValidityType.DateRange; }
        else if (months != null)
        {
            if (months <= 0) return (null, "Months must be a positive number.");
            end = start.AddMonths(months.Value).AddDays(-1); type = LicenseValidityType.Months; value = months;
        }
        else
        {
            if (days <= 0) return (null, "Days must be a positive number.");
            end = start.AddDays(days!.Value - 1); type = LicenseValidityType.Days; value = days;
        }
        if (end < start) return (null, "The license end date must not be before its start date.");

        return (new SubscriptionLicense
        {
            OrganisationId = organisationId, StartDate = start, EndDate = end,
            ValidityType = type, ValidityValue = value,
            Reference = string.IsNullOrWhiteSpace(reference) ? null : reference.Trim(),
            CreatedById = User.GetUserId()
        }, null);
    }

    /// <summary>Records the next licensing period for a tenant (renewal or first
    /// license). History is append-only and every addition is audit-logged.</summary>
    [Authorize(Roles = "SuperUser")]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AddOrganisationLicense(int organisationId,
        DateTime? startDate, DateTime? endDate, int? months, int? days, string? reference, bool neverExpires = false)
    {
        var org = await _db.Organisations.FindAsync(organisationId);
        if (org == null) return NotFound();

        // Renewals default to starting the day after current coverage ends
        if (startDate == null)
        {
            var latestEnd = await _db.SubscriptionLicenses
                .Where(l => l.OrganisationId == organisationId)
                .MaxAsync(l => (DateTime?)l.EndDate);
            startDate = latestEnd != null && latestEnd.Value.Date >= DateTime.UtcNow.Date
                ? latestEnd.Value.Date.AddDays(1)
                : DateTime.UtcNow.Date;
        }

        var (license, error) = BuildLicense(organisationId, startDate, endDate, months, days, reference, neverExpires);
        if (license == null)
        {
            TempData["Err"] = error;
            return RedirectToAction("Organisation", new { id = organisationId });
        }
        _db.SubscriptionLicenses.Add(license);
        var endText = license.IsPerpetual ? "never expires" : license.EndDate.ToString("yyyy-MM-dd");
        Notifier.Audit(_db, User.GetUserId(), User.Identity!.Name ?? "", "AddSubscriptionLicense",
            $"{org.Name}: {license.StartDate:yyyy-MM-dd} → {endText} ({license.ValidityType}{(license.ValidityValue != null ? $" {license.ValidityValue}" : "")}{(license.Reference != null ? $", ref {license.Reference}" : "")})");
        await _db.SaveChangesAsync();
        TempData["Ok"] = license.IsPerpetual
            ? $"Perpetual license recorded for {org.Name}: valid from {license.StartDate:dd MMM yyyy}, never expires."
            : $"License recorded for {org.Name}: {license.StartDate:dd MMM yyyy} – {license.EndDate:dd MMM yyyy}.";
        return RedirectToAction("Organisation", new { id = organisationId });
    }

    [Authorize(Roles = "SuperUser")]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> OnboardOrganisation(string name, string code, string? contactEmail, string? contactPhone, string? address, IFormFile? logo,
        DateTime? licenseStart, DateTime? licenseEnd, int? licenseMonths, int? licenseDays, string? licenseReference, bool licenseNeverExpires = false)
    {
        name = name?.Trim() ?? ""; code = code?.Trim().ToUpperInvariant() ?? "";
        if (name.Length == 0 || code.Length == 0)
        {
            TempData["Err"] = "Organisation name and code are required.";
            return RedirectToAction("Organisations");
        }
        if (await _db.Organisations.AnyAsync(o => o.Code == code || o.Name == name))
        {
            TempData["Err"] = $"An organisation with that name or code already exists.";
            return RedirectToAction("Organisations");
        }

        // Subscription license is part of onboarding: validate before creating anything
        var hasLicenseInput = licenseEnd != null || licenseMonths != null || licenseDays != null || licenseNeverExpires;
        SubscriptionLicense? license = null;
        if (hasLicenseInput)
        {
            string? error;
            (license, error) = BuildLicense(0, licenseStart, licenseEnd, licenseMonths, licenseDays, licenseReference, licenseNeverExpires);
            if (license == null)
            {
                TempData["Err"] = error;
                return RedirectToAction("Organisations");
            }
        }

        var org = new Organisation { Name = name, Code = code, ContactEmail = contactEmail, ContactPhone = contactPhone, Address = address, LogoUrl = await SaveLogoAsync(logo) };
        _db.Organisations.Add(org);
        Notifier.Audit(_db, User.GetUserId(), User.Identity!.Name ?? "", "OnboardOrganisation", $"{name} ({code})");
        await _db.SaveChangesAsync();   // commits the org (with its LogoUrl) — the logo file now has an owning row
        UploadHelper.Commit(HttpContext);

        // Every tenant starts with the four default tagged roles (its own vocabulary
        // of the built-in LMS roles) so users can always be tagged to an org role.
        foreach (var (baseRole, label) in MappableRoles)
        {
            if (!await _roleManager.RoleExistsAsync(label))
                await _roleManager.CreateAsync(new IdentityRole(label));
            _db.OrganisationRoles.Add(new OrganisationRole
            {
                OrganisationId = org.Id, Name = label, MapsToRole = baseRole,
                Description = $"Default organisation role tagged to the built-in {label} LMS role"
            });
        }
        await _db.SaveChangesAsync();

        if (license != null)
        {
            license.OrganisationId = org.Id;
            _db.SubscriptionLicenses.Add(license);
            var endText = license.IsPerpetual ? "never expires" : license.EndDate.ToString("yyyy-MM-dd");
            Notifier.Audit(_db, User.GetUserId(), User.Identity!.Name ?? "", "AddSubscriptionLicense",
                $"{org.Name}: {license.StartDate:yyyy-MM-dd} → {endText} ({license.ValidityType}{(license.ValidityValue != null ? $" {license.ValidityValue}" : "")})");
            await _db.SaveChangesAsync();
            TempData["Ok"] = license.IsPerpetual
                ? $"Organisation \"{name}\" onboarded with a perpetual license (never expires). You can now add its roles, training locations and users."
                : $"Organisation \"{name}\" onboarded with a license valid until {license.EndDate:dd MMM yyyy}. You can now add its roles, training locations and users.";
        }
        else
        {
            TempData["Ok"] = $"Organisation \"{name}\" onboarded WITHOUT a subscription license — its users cannot sign in until one is added below.";
        }
        return RedirectToAction("Organisation", new { id = org.Id });
    }

    [Authorize(Roles = "SuperUser")]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> EditOrganisation(int id, string name, string code, string? contactEmail, string? contactPhone, string? address, IFormFile? logo)
    {
        var org = await _db.Organisations.FindAsync(id);
        if (org == null) return NotFound();
        name = name?.Trim() ?? ""; code = code?.Trim().ToUpperInvariant() ?? "";
        if (name.Length == 0 || code.Length == 0)
        {
            TempData["Err"] = "Organisation name and code are required.";
            return RedirectToAction("Organisation", new { id });
        }
        if (await _db.Organisations.AnyAsync(o => o.Id != id && (o.Code == code || o.Name == name)))
        {
            TempData["Err"] = "Another organisation already uses that name or code.";
            return RedirectToAction("Organisation", new { id });
        }
        org.Name = name; org.Code = code; org.ContactEmail = contactEmail; org.ContactPhone = contactPhone; org.Address = address;
        var previousLogo = org.LogoUrl;
        var newLogo = await SaveLogoAsync(logo);
        if (newLogo != null) org.LogoUrl = newLogo;
        Notifier.Audit(_db, User.GetUserId(), User.Identity!.Name ?? "", "EditOrganisation", $"{name} ({code})");
        await _db.SaveChangesAsync();
        if (newLogo != null) UploadHelper.TryDeleteStored(previousLogo, _env);   // replaced logo — remove the old file
        TempData["Ok"] = "Organisation details updated.";
        return RedirectToAction("Organisation", new { id });
    }

    [Authorize(Roles = "SuperUser")]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleOrganisationActive(int id)
    {
        var org = await _db.Organisations.FindAsync(id);
        if (org == null) return NotFound();
        // The platform owner's organisation (home of Super Users) cannot be
        // deactivated — that would lock the platform operators out.
        if (org.IsActive)
        {
            var superUsers = await _userManager.GetUsersInRoleAsync("SuperUser");
            if (superUsers.Any(s => s.OrganisationId == org.Id))
            {
                TempData["Err"] = $"\"{org.Name}\" hosts the platform Super User account(s) and cannot be deactivated.";
                return RedirectToAction("Organisations");
            }
        }
        org.IsActive = !org.IsActive;
        Notifier.Audit(_db, User.GetUserId(), User.Identity!.Name ?? "", org.IsActive ? "ActivateOrganisation" : "DeactivateOrganisation", org.Name);
        await _db.SaveChangesAsync();
        TempData["Ok"] = $"Organisation \"{org.Name}\" {(org.IsActive ? "activated" : "deactivated")}.";
        return RedirectToAction("Organisations");
    }

    [Authorize(Roles = "SuperUser")]
    /// <summary>Organisation detail: profile, custom roles, and this tenant's users.</summary>
    public async Task<IActionResult> Organisation(int id)
    {
        var org = await _db.Organisations
            .Include(o => o.Roles)
            .Include(o => o.Locations)
            .Include(o => o.Licenses.OrderByDescending(l => l.StartDate)).ThenInclude(l => l.CreatedBy)
            .FirstOrDefaultAsync(o => o.Id == id);
        if (org == null) return NotFound();

        var users = await _db.Users.Where(u => u.OrganisationId == id).OrderBy(u => u.FullName).ToListAsync();
        var withRoles = new List<(ApplicationUser User, IList<string> Roles)>();
        foreach (var u in users)
            withRoles.Add((u, await _userManager.GetRolesAsync(u)));

        // Number of users currently holding each custom role (for safe deletion)
        var roleUsage = new Dictionary<string, int>();
        foreach (var r in org.Roles)
            roleUsage[r.Name] = (await _userManager.GetUsersInRoleAsync(r.Name)).Count;

        ViewBag.Users = withRoles;
        ViewBag.RoleUsage = roleUsage;
        ViewBag.CourseCount = await _db.Courses.CountAsync(c => c.OrganisationId == id);
        return View(org);
    }

    /// <summary>Platform roles a custom organisation role may be mapped to,
    /// keyed by storage name with their tenant-facing labels.</summary>
    public static readonly (string Role, string Label)[] MappableRoles =
        { ("Student", "Trainee"), ("Instructor", "Trainer"), ("Principal", "Principal"), ("Admin", "Admin") };

    /// <summary>Resolves which organisation the caller may manage roles for: the Super
    /// User manages any organisation; an organisation Admin only their own.</summary>
    private async Task<Organisation?> ResolveRoleOrgAsync(int organisationId)
    {
        if (!User.IsInRole("SuperUser"))
        {
            var myOrgId = await _db.Users.Where(u => u.Id == User.GetUserId())
                .Select(u => u.OrganisationId).FirstOrDefaultAsync();
            if (myOrgId == null || myOrgId != organisationId) return null;
        }
        return await _db.Organisations.FindAsync(organisationId);
    }

    private IActionResult BackToRoles(int organisationId) =>
        User.IsInRole("SuperUser")
            ? RedirectToAction("Organisation", new { id = organisationId })
            : RedirectToAction("OrgRoles");

    /// <summary>Organisation-roles management page for the tenant's own Admin:
    /// create custom roles, map them to platform roles, and remove unused ones.
    /// (The Super User manages any tenant's roles from the organisation detail page.)</summary>
    public async Task<IActionResult> OrgRoles()
    {
        var myOrgId = await _db.Users.Where(u => u.Id == User.GetUserId())
            .Select(u => u.OrganisationId).FirstOrDefaultAsync();
        if (myOrgId == null)
        {
            TempData["Err"] = "Your account has no organisation.";
            return RedirectToAction("Dashboard");
        }
        var org = await _db.Organisations.Include(o => o.Roles).FirstAsync(o => o.Id == myOrgId);
        var roleUsage = new Dictionary<string, int>();
        foreach (var r in org.Roles)
            roleUsage[r.Name] = (await _userManager.GetUsersInRoleAsync(r.Name)).Count;
        ViewBag.RoleUsage = roleUsage;
        return View(org);
    }

    /// <summary>Creates a custom role for an organisation — backed by an Identity role
    /// of the same name and MAPPED to one of the four platform roles, which its
    /// holders inherit at authorisation time. Super User: any organisation;
    /// organisation Admin: own organisation only.</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AddOrganisationRole(int organisationId, string name, string? description, string mapsTo)
    {
        var org = await ResolveRoleOrgAsync(organisationId);
        if (org == null) return Forbid();

        name = name?.Trim() ?? "";
        if (name.Length == 0)
        {
            TempData["Err"] = "Role name is required.";
            return BackToRoles(organisationId);
        }
        if (!MappableRoles.Any(m => m.Role == mapsTo))
        {
            TempData["Err"] = "Choose which LMS role (Trainee, Trainer, Principal or Admin) this role is tagged to.";
            return BackToRoles(organisationId);
        }
        // An organisation may name its roles after the built-in LMS roles (e.g. its
        // own "Trainer" mapped to Trainer) — only SuperUser is reserved, as granting
        // that Identity role would hand over platform administration.
        if (string.Equals(name, "SuperUser", StringComparison.OrdinalIgnoreCase))
        {
            TempData["Err"] = "\"SuperUser\" is the platform owner's role and cannot be used for an organisation role.";
            return BackToRoles(organisationId);
        }
        if (await _db.OrganisationRoles.AnyAsync(r => r.OrganisationId == organisationId && r.Name.ToLower() == name.ToLower()))
        {
            TempData["Err"] = $"This organisation already has a role named \"{name}\".";
            return BackToRoles(organisationId);
        }

        if (!await _roleManager.RoleExistsAsync(name))
        {
            var created = await _roleManager.CreateAsync(new IdentityRole(name));
            if (!created.Succeeded)
            {
                TempData["Err"] = string.Join(" ", created.Errors.Select(e => e.Description));
                return BackToRoles(organisationId);
            }
        }
        _db.OrganisationRoles.Add(new OrganisationRole { OrganisationId = organisationId, Name = name, Description = description?.Trim(), MapsToRole = mapsTo });
        Notifier.Audit(_db, User.GetUserId(), User.Identity!.Name ?? "", "AddOrganisationRole", $"{org.Name}: {name} → {mapsTo}");
        await _db.SaveChangesAsync();
        TempData["Ok"] = $"Role \"{name}\" added for {org.Name}, tagged to {MappableRoles.First(m => m.Role == mapsTo).Label}. Assign it from the Users page.";
        return BackToRoles(organisationId);
    }

    /// <summary>Re-maps an existing custom role to a different platform role.
    /// Takes effect for holders on their next request (principal is rebuilt).</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> MapOrganisationRole(int id, string mapsTo)
    {
        var role = await _db.OrganisationRoles.Include(r => r.Organisation).FirstOrDefaultAsync(r => r.Id == id);
        if (role == null) return NotFound();
        if (await ResolveRoleOrgAsync(role.OrganisationId) == null) return Forbid();
        if (!MappableRoles.Any(m => m.Role == mapsTo))
        {
            TempData["Err"] = "Choose a valid LMS role to map to.";
            return BackToRoles(role.OrganisationId);
        }
        var old = role.MapsToRole;
        role.MapsToRole = mapsTo;
        Notifier.Audit(_db, User.GetUserId(), User.Identity!.Name ?? "", "MapOrganisationRole", $"{role.Organisation?.Name}: {role.Name} {old} → {mapsTo}");
        await _db.SaveChangesAsync();
        TempData["Ok"] = $"\"{role.Name}\" is now tagged to {MappableRoles.First(m => m.Role == mapsTo).Label}.";
        return BackToRoles(role.OrganisationId);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteOrganisationRole(int id)
    {
        var role = await _db.OrganisationRoles.Include(r => r.Organisation).FirstOrDefaultAsync(r => r.Id == id);
        if (role == null) return NotFound();
        if (await ResolveRoleOrgAsync(role.OrganisationId) == null) return Forbid();

        var holders = await _userManager.GetUsersInRoleAsync(role.Name);
        if (holders.Count > 0)
        {
            TempData["Err"] = $"Cannot delete \"{role.Name}\" — {holders.Count} user(s) still hold it. Remove it from those users first.";
            return BackToRoles(role.OrganisationId);
        }
        var identityRole = await _roleManager.FindByNameAsync(role.Name);
        if (identityRole != null) await _roleManager.DeleteAsync(identityRole);
        _db.OrganisationRoles.Remove(role);
        Notifier.Audit(_db, User.GetUserId(), User.Identity!.Name ?? "", "DeleteOrganisationRole", $"{role.Organisation?.Name}: {role.Name}");
        await _db.SaveChangesAsync();
        TempData["Ok"] = $"Role \"{role.Name}\" deleted.";
        return BackToRoles(role.OrganisationId);
    }

    /// <summary>Adds a training location (venue + room details) for an organisation.
    /// Captured at client onboarding; suggested in the Batch Set-up form.</summary>
    [Authorize(Roles = "SuperUser")]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AddTrainingLocation(int organisationId, string name, string? room)
    {
        var org = await _db.Organisations.FindAsync(organisationId);
        if (org == null) return NotFound();
        name = name?.Trim() ?? "";
        if (name.Length == 0)
        {
            TempData["Err"] = "Training location name is required.";
            return RedirectToAction("Organisation", new { id = organisationId });
        }
        room = string.IsNullOrWhiteSpace(room) ? null : room.Trim();
        if (await _db.TrainingLocations.AnyAsync(l => l.OrganisationId == organisationId
                && l.Name.ToLower() == name.ToLower() && (l.Room ?? "").ToLower() == (room ?? "").ToLower()))
        {
            TempData["Err"] = "That location/room is already on the list.";
            return RedirectToAction("Organisation", new { id = organisationId });
        }
        _db.TrainingLocations.Add(new TrainingLocation { OrganisationId = organisationId, Name = name, Room = room });
        Notifier.Audit(_db, User.GetUserId(), User.Identity!.Name ?? "", "AddTrainingLocation", $"{org.Name}: {name}{(room != null ? $" / {room}" : "")}");
        await _db.SaveChangesAsync();
        TempData["Ok"] = $"Training location \"{name}\" added for {org.Name}.";
        return RedirectToAction("Organisation", new { id = organisationId });
    }

    [Authorize(Roles = "SuperUser")]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteTrainingLocation(int id)
    {
        var loc = await _db.TrainingLocations.Include(l => l.Organisation).FirstOrDefaultAsync(l => l.Id == id);
        if (loc == null) return NotFound();
        _db.TrainingLocations.Remove(loc);
        Notifier.Audit(_db, User.GetUserId(), User.Identity!.Name ?? "", "DeleteTrainingLocation", $"{loc.Organisation?.Name}: {loc.Name}");
        await _db.SaveChangesAsync();
        TempData["Ok"] = $"Training location \"{loc.Name}\" removed.";
        return RedirectToAction("Organisation", new { id = loc.OrganisationId });
    }

    /// <summary>Uploads a signature image for a user (rendered on certificates where
    /// they sign, e.g. as Course Instructor or the organisation's certificate
    /// signatory). Admins manage their own organisation's users; Super User any.</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> UploadUserSignature(string id, IFormFile? signature)
    {
        var user = await _userManager.FindByIdAsync(id);
        if (user == null) return NotFound();
        if (!await CanManageAsync(user)) return Forbid();
        if (signature == null || signature.Length == 0)
        {
            TempData["Err"] = "Choose a signature image to upload.";
            return RedirectToAction("Users");
        }
        // Validate by decoding the pixels (not the extension) and re-encode to a clean PNG.
        var url = await UploadHelper.SaveImageAsync(signature, _env, "signatures", 1_000_000);
        if (url == null)
        {
            TempData["Err"] = "Signature must be a valid image (PNG, JPG or WebP) under 1 MB.";
            return RedirectToAction("Users");
        }
        var previousUrl = user.SignatureUrl;
        user.SignatureUrl = url;
        await _userManager.UpdateAsync(user);   // commits the SignatureUrl — the file now has an owning row
        UploadHelper.Commit(HttpContext);
        UploadHelper.TryDeleteStored(previousUrl, _env);   // replacement succeeded — remove the old file
        Notifier.Audit(_db, User.GetUserId(), User.Identity!.Name ?? "", "UploadUserSignature", user.Email);
        await _db.SaveChangesAsync();
        TempData["Ok"] = $"Signature uploaded for {user.FullName} — it now appears on certificates they sign.";
        return RedirectToAction("Users");
    }

    /// <summary>Selects (or clears) the organisation's certificate signatory — the
    /// staff member whose name and signature appear in the Training Director slot
    /// of the organisation's certificates.</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SetCertificateSignatory(string id)
    {
        var user = await _userManager.FindByIdAsync(id);
        if (user?.OrganisationId == null) return NotFound();
        if (!await CanManageAsync(user)) return Forbid();
        var org = await _db.Organisations.FindAsync(user.OrganisationId.Value);
        if (org == null) return NotFound();
        if (org.CertificateSignatoryId == user.Id)
        {
            org.CertificateSignatoryId = null;   // toggle off
            TempData["Ok"] = $"{user.FullName} is no longer {org.Name}'s certificate signatory.";
        }
        else
        {
            // A signatory must have a real signature on file — certificates carry
            // the image, so a placeholder signatory is not allowed.
            if (user.SignatureUrl == null)
            {
                TempData["Err"] = $"{user.FullName} has no signature uploaded — upload their signature image before selecting them as certificate signatory.";
                return RedirectToAction("Users");
            }
            org.CertificateSignatoryId = user.Id;
            TempData["Ok"] = $"{user.FullName} now signs {org.Name}'s certificates.";
        }
        Notifier.Audit(_db, User.GetUserId(), User.Identity!.Name ?? "", "SetCertificateSignatory", $"{org.Name}: {(org.CertificateSignatoryId == null ? "cleared" : user.Email)}");
        await _db.SaveChangesAsync();
        return RedirectToAction("Users");
    }

    /// <summary>Move a user into an organisation (or clear it with null).</summary>
    [Authorize(Roles = "SuperUser")]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SetUserOrganisation(string id, int? organisationId)
    {
        var user = await _userManager.FindByIdAsync(id);
        if (user == null) return NotFound();
        if (organisationId != null && !await _db.Organisations.AnyAsync(o => o.Id == organisationId)) return NotFound();
        if (await _userManager.IsInRoleAsync(user, "SuperUser") && organisationId != await OwnerOrgIdAsync())
        {
            TempData["Err"] = $"{user.Email} holds the Super User role — Super User accounts always belong to AbsoluteSYS.";
            return RedirectToAction("Users");
        }
        user.OrganisationId = organisationId;
        await _userManager.UpdateAsync(user);
        Notifier.Audit(_db, User.GetUserId(), User.Identity!.Name ?? "", "SetUserOrganisation", $"{user.Email} -> org {organisationId?.ToString() ?? "none"}");
        await _db.SaveChangesAsync();
        TempData["Ok"] = $"Organisation updated for {user.Email}.";
        return RedirectToAction("Users");
    }

    // ---------- Courses ----------
    public async Task<IActionResult> Courses()
    {
        var courses = await _db.Courses
            .Include(c => c.Instructor).Include(c => c.Category).Include(c => c.Enrollments)
            .OrderBy(c => c.Title).ToListAsync();
        ViewBag.Instructors = await _userManager.GetUsersInRoleAsync("Instructor");
        ViewBag.Categories = await _db.Categories.OrderBy(c => c.Name).ToListAsync();
        return View(courses);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> TogglePublish(int id)
    {
        var course = await _db.Courses.FindAsync(id);
        if (course == null) return NotFound();
        course.IsPublished = !course.IsPublished;
        Notifier.Audit(_db, User.GetUserId(), User.Identity!.Name ?? "", course.IsPublished ? "PublishCourse" : "UnpublishCourse", course.Title);
        await _db.SaveChangesAsync();
        return RedirectToAction("Courses");
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ReassignInstructor(int id, string instructorId)
    {
        var course = await _db.Courses.FindAsync(id);
        if (course == null) return NotFound();
        course.InstructorId = instructorId;
        await _db.SaveChangesAsync();
        TempData["Ok"] = "Instructor reassigned.";
        return RedirectToAction("Courses");
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteCourse(int id)
    {
        var course = await _db.Courses.Include(c => c.Enrollments).FirstOrDefaultAsync(c => c.Id == id);
        if (course == null) return NotFound();
        _db.Courses.Remove(course);
        Notifier.Audit(_db, User.GetUserId(), User.Identity!.Name ?? "", "DeleteCourse", course.Title);
        await _db.SaveChangesAsync();
        TempData["Ok"] = "Course deleted.";
        return RedirectToAction("Courses");
    }

    // ---------- Categories ----------
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AddCategory(string name)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            _db.Categories.Add(new Category { Name = name.Trim() });
            await _db.SaveChangesAsync();
        }
        return RedirectToAction("Courses");
    }

    // ---------- Enrollments ----------
    public async Task<IActionResult> Enrollments(int? courseId)
    {
        var query = _db.Enrollments.Include(e => e.Course).Include(e => e.Student).AsQueryable();
        if (courseId != null) query = query.Where(e => e.CourseId == courseId);
        ViewBag.Courses = await _db.Courses.OrderBy(c => c.Title).ToListAsync();
        ViewBag.CourseId = courseId;
        ViewBag.Students = await _userManager.GetUsersInRoleAsync("Student");
        return View(await query.OrderByDescending(e => e.EnrolledAt).ToListAsync());
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> EnrollStudent(int courseId, string studentId)
    {
        if (!await _db.Enrollments.AnyAsync(e => e.CourseId == courseId && e.StudentId == studentId))
        {
            _db.Enrollments.Add(new Enrollment { CourseId = courseId, StudentId = studentId });
            Notifier.Notify(_db, studentId, "You have been enrolled in a course by an administrator.", $"/Courses/Details/{courseId}");
            await _db.SaveChangesAsync();
            TempData["Ok"] = "Student enrolled.";
        }
        else TempData["Err"] = "Student is already enrolled.";
        return RedirectToAction("Enrollments", new { courseId });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveEnrollment(int id)
    {
        var e = await _db.Enrollments.FindAsync(id);
        if (e != null)
        {
            _db.Enrollments.Remove(e);
            await _db.SaveChangesAsync();
        }
        return RedirectToAction("Enrollments");
    }

    // ---------- Announcements ----------
    public async Task<IActionResult> Announcements()
    {
        var list = await _db.Announcements.Include(a => a.Author).Include(a => a.Course)
            .OrderByDescending(a => a.CreatedAt).ToListAsync();
        return View(list);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateAnnouncement(string title, string body)
    {
        _db.Announcements.Add(new Announcement { Title = title, Body = body, AuthorId = User.GetUserId() });
        var allUsers = await _db.Users.Select(u => u.Id).ToListAsync();
        Notifier.NotifyCourse(_db, allUsers, $"Announcement: {title}");
        await _db.SaveChangesAsync();
        TempData["Ok"] = "Announcement published to all users.";
        return RedirectToAction("Announcements");
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteAnnouncement(int id)
    {
        var a = await _db.Announcements.FindAsync(id);
        if (a != null) { _db.Announcements.Remove(a); await _db.SaveChangesAsync(); }
        return RedirectToAction("Announcements");
    }

    // ---------- Settings & Audit ----------
    public async Task<IActionResult> Settings()
    {
        return View(await _db.SiteSettings.OrderBy(s => s.Key).ToListAsync());
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveSettings(Dictionary<string, string> settings)
    {
        foreach (var kv in settings)
        {
            var s = await _db.SiteSettings.FindAsync(kv.Key);
            if (s != null) s.Value = kv.Value;
            else _db.SiteSettings.Add(new SiteSetting { Key = kv.Key, Value = kv.Value });
        }
        Notifier.Audit(_db, User.GetUserId(), User.Identity!.Name ?? "", "UpdateSettings");
        await _db.SaveChangesAsync();
        TempData["Ok"] = "Settings saved.";
        return RedirectToAction("Settings");
    }

    // ---------- Single sign-on configuration (§AUTH-09) ----------
    public async Task<IActionResult> Sso()
    {
        var sso = HttpContext.RequestServices.GetRequiredService<SsoService>();
        var orgId = await CallerOrganisationIdAsync();
        if (orgId == null) { TempData["Err"] = "Single sign-on is configured per organisation."; return RedirectToAction("Settings"); }
        ViewBag.OrganisationName = await _db.Organisations.IgnoreQueryFilters()
            .Where(o => o.Id == orgId).Select(o => o.Name).FirstOrDefaultAsync();
        ViewBag.CallbackUrl = $"{Request.Scheme}://{Request.Host}/signin-oidc";
        ViewBag.OrgRoles = await _db.OrganisationRoles.IgnoreQueryFilters()
            .Where(r => r.OrganisationId == orgId).Select(r => r.Name).OrderBy(n => n).ToListAsync();
        return View(await sso.GetForOrganisationAsync(orgId.Value) ?? new SsoConfiguration { OrganisationId = orgId.Value });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveSso(bool isEnabled, string displayName, string authority, string clientId,
        string? clientSecret, string emailDomains, string? roleClaimName, string? roleMappings,
        bool jitProvisioning, string defaultRole, bool allowLocalPassword)
    {
        var sso = HttpContext.RequestServices.GetRequiredService<SsoService>();
        var orgId = await CallerOrganisationIdAsync();
        if (orgId == null) return Forbid();

        var cfg = await sso.GetForOrganisationAsync(orgId.Value);
        if (cfg == null) { cfg = new SsoConfiguration { OrganisationId = orgId.Value }; _db.SsoConfigurations.Add(cfg); }

        cfg.IsEnabled = isEnabled;
        cfg.Protocol = SsoProtocol.Oidc;
        cfg.DisplayName = string.IsNullOrWhiteSpace(displayName) ? "your organisation account" : displayName.Trim();
        cfg.Authority = (authority ?? "").Trim();
        cfg.ClientId = (clientId ?? "").Trim();
        // Blank means "keep the stored secret" — the secret is never sent back to the browser.
        if (!string.IsNullOrWhiteSpace(clientSecret)) cfg.ClientSecretProtected = sso.Protect(clientSecret.Trim());
        cfg.EmailDomains = (emailDomains ?? "").Trim();
        cfg.RoleClaimName = string.IsNullOrWhiteSpace(roleClaimName) ? null : roleClaimName.Trim();
        cfg.RoleMappings = string.IsNullOrWhiteSpace(roleMappings) ? null : roleMappings.Trim();
        cfg.JitProvisioning = jitProvisioning;
        cfg.DefaultRole = string.IsNullOrWhiteSpace(defaultRole) ? "Trainee" : defaultRole.Trim();
        cfg.AllowLocalPassword = allowLocalPassword;
        cfg.UpdatedAt = DateTime.UtcNow;

        if (cfg.IsEnabled && (cfg.Authority.Length == 0 || cfg.ClientId.Length == 0 || cfg.EmailDomains.Length == 0))
        {
            TempData["Err"] = "Authority, Client ID and at least one email domain are required to enable single sign-on.";
            return RedirectToAction("Sso");
        }

        Notifier.Audit(_db, User.GetUserId(), User.Identity!.Name ?? "", "UpdateSsoConfiguration",
            $"{(cfg.IsEnabled ? "enabled" : "disabled")}: {cfg.Authority}");
        await _db.SaveChangesAsync();
        // Apply immediately — evict the cached OIDC options so the next request rebuilds them.
        HttpContext.RequestServices.GetRequiredService<SsoOptionsCache>().Invalidate();
        TempData["Ok"] = "Single sign-on settings saved.";
        return RedirectToAction("Sso");
    }

    private async Task<int?> CallerOrganisationIdAsync() =>
        await _db.Users.IgnoreQueryFilters().Where(u => u.Id == User.GetUserId())
            .Select(u => u.OrganisationId).FirstOrDefaultAsync();

    // Rebuild the subjective-grading knowledge index from course material (§AIG-02).
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ReindexKnowledge()
    {
        var svc = HttpContext.RequestServices.GetRequiredService<KnowledgeIndexService>();
        var callerOrg = await _db.Users.Where(u => u.Id == User.GetUserId()).Select(u => u.OrganisationId).FirstOrDefaultAsync();
        try
        {
            int total = 0;
            if (User.IsInRole("SuperUser") || callerOrg == null)
            {
                var orgIds = await _db.Organisations.IgnoreQueryFilters().Select(o => o.Id).ToListAsync();
                foreach (var id in orgIds) total += await svc.ReindexOrganisationAsync(id);
            }
            else
            {
                total = await svc.ReindexOrganisationAsync(callerOrg.Value);
            }
            Notifier.Audit(_db, User.GetUserId(), User.Identity!.Name ?? "", "ReindexKnowledge");
            await _db.SaveChangesAsync();
            TempData["Ok"] = $"Knowledge index rebuilt: {total} passages.";
        }
        catch (GradingUnavailableException)
        {
            TempData["Err"] = "Could not reach the local grading model (Ollama). Start the Ollama service and try again.";
        }
        return RedirectToAction("Settings");
    }

    // ---------- Learning Records (xAPI LRS) ----------
    public async Task<IActionResult> LearningRecords(int page = 1)
    {
        const int pageSize = 30;
        var statements = await _db.XapiStatements
            .OrderByDescending(s => s.Stored)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
        ViewBag.Page = page;
        ViewBag.HasMore = await _db.XapiStatements.CountAsync() > page * pageSize;
        ViewBag.Total = await _db.XapiStatements.CountAsync();
        return View(statements);
    }

    public async Task<IActionResult> AuditLog(int page = 1)
    {
        const int pageSize = 25;
        var logs = await _db.AuditLogs.OrderByDescending(a => a.Timestamp)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
        ViewBag.Page = page;
        ViewBag.HasMore = await _db.AuditLogs.CountAsync() > page * pageSize;
        return View(logs);
    }
}

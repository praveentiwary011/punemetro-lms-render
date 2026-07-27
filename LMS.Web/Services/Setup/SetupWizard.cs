using LMS.Web.Data;
using LMS.Web.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace LMS.Web.Services.Setup;

/// <summary>Steps of the first-run installation wizard, in order.</summary>
public enum SetupStep { Welcome = 0, Database = 1, Organisation = 2, Administrator = 3, Grading = 4, Review = 5 }

/// <summary>Answers collected across the wizard steps. Process-wide singleton: the wizard runs
/// once, before the LMS is usable, so there is no per-user state to keep.</summary>
public class SetupWizardState
{
    public SetupStep Step { get; set; } = SetupStep.Welcome;
    public bool DatabaseDone { get; set; }

    // Organisation
    public string OrgName { get; set; } = "";
    public string OrgCode { get; set; } = "";
    public string? OrgContactEmail { get; set; }
    public string? OrgContactPhone { get; set; }
    public string? OrgAddress { get; set; }
    public string SiteName { get; set; } = "Learning Management System";

    // Administrator
    public string AdminName { get; set; } = "";
    public string AdminEmail { get; set; } = "";
    public string AdminPassword { get; set; } = "";
    public bool AdminIsSuperUser { get; set; }

    // AI grading
    public bool GradingEnabled { get; set; } = true;
    public string OllamaUrl { get; set; } = "http://localhost:11434";
    public GradingMode GradingMode { get; set; } = GradingMode.Cpu;

    // Data
    public bool InstallDemoData { get; set; }

    public bool OrganisationDone => !string.IsNullOrWhiteSpace(OrgName) && !string.IsNullOrWhiteSpace(OrgCode);
    public bool AdministratorDone => !string.IsNullOrWhiteSpace(AdminEmail) && !string.IsNullOrWhiteSpace(AdminPassword);
}

/// <summary>Read-only checks shown on the wizard's welcome step so the installer can see the
/// server is fit before configuring anything.</summary>
public record PrereqCheck(string Name, bool Ok, string Detail, bool Warning = false);

public static class EnvironmentProbe
{
    public static List<PrereqCheck> Run(IWebHostEnvironment env, string setupDir)
    {
        var list = new List<PrereqCheck>
        {
            new(".NET runtime", true, Environment.Version.ToString()),
            new("Operating system", true, System.Runtime.InteropServices.RuntimeInformation.OSDescription),
            new("CPU cores", Environment.ProcessorCount >= 4, $"{Environment.ProcessorCount} logical cores" +
                (Environment.ProcessorCount < 4 ? " — 8+ recommended when grading on CPU" : ""),
                Warning: Environment.ProcessorCount < 4)
        };

        // Writable data location (database + setup file live here)
        bool writable;
        string detail = setupDir;
        try
        {
            Directory.CreateDirectory(setupDir);
            var probe = Path.Combine(setupDir, ".write-probe");
            File.WriteAllText(probe, "ok"); File.Delete(probe);
            writable = true;
        }
        catch (Exception ex) { writable = false; detail = $"{setupDir} — {ex.Message}"; }
        list.Add(new("Data folder writable", writable, detail));

        // Free disk space on the data volume
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(setupDir))!);
            var freeGb = drive.AvailableFreeSpace / 1024d / 1024d / 1024d;
            list.Add(new("Free disk space", freeGb >= 10, $"{freeGb:0.#} GB available" +
                (freeGb < 10 ? " — at least 10 GB recommended (grading models need ~5 GB)" : ""),
                Warning: freeGb >= 10 && freeGb < 40));
        }
        catch { /* not critical */ }

        return list;
    }
}

/// <summary>Probes the local Ollama server for availability and installed models.</summary>
public static class OllamaProbe
{
    public static async Task<(bool up, List<string> models, string message)> InspectAsync(string baseUrl)
    {
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(5) };
            var resp = await http.GetAsync("/api/tags");
            if (!resp.IsSuccessStatusCode) return (false, new(), $"Ollama responded with {(int)resp.StatusCode}.");
            using var doc = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var names = new List<string>();
            if (doc.RootElement.TryGetProperty("models", out var arr))
                foreach (var m in arr.EnumerateArray())
                    if (m.TryGetProperty("name", out var n)) names.Add(n.GetString() ?? "");
            return (true, names, "Connected.");
        }
        catch (Exception ex) { return (false, new(), ex.Message); }
    }
}

/// <summary>Creates a clean, production-ready installation from the wizard's answers: the
/// platform-owner organisation, the customer's organisation (with a perpetual licence and the
/// four default company roles), their administrator account and the site settings — instead of
/// the Pune Metro demo dataset.</summary>
public static class SetupSeeder
{
    public static async Task SeedCleanAsync(IServiceProvider services, SetupWizardState w)
    {
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<AppDbContext>();
        var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
        var roles = sp.GetRequiredService<RoleManager<IdentityRole>>();

        foreach (var r in new[] { "SuperUser", "Admin", "Principal", "Instructor", "Student" })
            if (!await roles.RoleExistsAsync(r)) await roles.CreateAsync(new IdentityRole(r));

        if (await db.Organisations.IgnoreQueryFilters().AnyAsync()) return;   // already installed

        // Platform-owner organisation (the vendor's tenant; keeps the multi-tenant model intact).
        var ownerOrg = new Organisation { Name = "AbsoluteSYS", Code = "ABSOLUTESYS", ContactEmail = "support@absolutesys.com" };
        var org = new Organisation
        {
            Name = w.OrgName.Trim(),
            Code = w.OrgCode.Trim().ToUpperInvariant(),
            ContactEmail = w.OrgContactEmail,
            ContactPhone = w.OrgContactPhone,
            Address = w.OrgAddress
        };
        db.Organisations.AddRange(ownerOrg, org);
        await db.SaveChangesAsync();

        // The four default company roles, each tagged to its built-in platform role.
        foreach (var (name, maps) in new[] { ("Trainee", "Student"), ("Trainer", "Instructor"), ("Principal", "Principal"), ("Admin", "Admin") })
        {
            db.OrganisationRoles.Add(new OrganisationRole
            {
                OrganisationId = org.Id, Name = name, MapsToRole = maps,
                Description = $"Default organisation role tagged to the built-in {name} LMS role"
            });
            if (!await roles.RoleExistsAsync(name)) await roles.CreateAsync(new IdentityRole(name));
        }
        await db.SaveChangesAsync();

        // The customer's administrator.
        var admin = new ApplicationUser
        {
            UserName = w.AdminEmail.Trim(), Email = w.AdminEmail.Trim(), FullName = w.AdminName.Trim(),
            EmailConfirmed = true, OrganisationId = org.Id, Department = "Administration"
        };
        var created = await users.CreateAsync(admin, w.AdminPassword);
        if (!created.Succeeded)
            throw new InvalidOperationException("Could not create the administrator account: " +
                string.Join("; ", created.Errors.Select(e => e.Description)));
        // The company role "Admin" reuses the built-in Admin Identity role, so one grant covers both.
        await users.AddToRoleAsync(admin, "Admin");
        if (w.AdminIsSuperUser) await users.AddToRoleAsync(admin, "SuperUser");

        // Perpetual licence so the organisation's users can sign in from day one.
        // (Created after the administrator exists — CreatedById is a required reference.)
        db.SubscriptionLicenses.Add(new SubscriptionLicense
        {
            OrganisationId = org.Id,
            StartDate = DateTime.UtcNow.Date,
            EndDate = SubscriptionLicense.PerpetualEndDate,
            ValidityType = LicenseValidityType.NeverExpires,
            Reference = "Initial installation",
            CreatedById = admin.Id
        });

        db.SiteSettings.AddRange(
            new SiteSetting { Key = "SiteName", Value = string.IsNullOrWhiteSpace(w.SiteName) ? org.Name : w.SiteName.Trim() },
            new SiteSetting { Key = "AllowSelfRegistration", Value = "false" },
            new SiteSetting { Key = "DefaultPassingGrade", Value = "60" },
            new SiteSetting { Key = "GradingMode", Value = w.GradingMode.ToString() },
            new SiteSetting { Key = "SeedVersion", Value = DbSeeder.SeedVersion });

        db.AuditLogs.Add(new AuditLog
        {
            UserId = admin.Id, UserName = admin.FullName, Action = "Install",
            Details = $"LMS installed via setup wizard for {org.Name} ({org.Code})"
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Applies wizard settings on top of the demo dataset (site name + grading mode).</summary>
    public static async Task ApplyDemoSettingsAsync(IServiceProvider services, SetupWizardState w)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        async Task Set(string key, string value)
        {
            var s = await db.SiteSettings.FindAsync(key);
            if (s != null) s.Value = value; else db.SiteSettings.Add(new SiteSetting { Key = key, Value = value });
        }
        if (!string.IsNullOrWhiteSpace(w.SiteName)) await Set("SiteName", w.SiteName.Trim());
        await Set("GradingMode", w.GradingMode.ToString());
        await db.SaveChangesAsync();
    }
}

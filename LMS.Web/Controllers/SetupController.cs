using LMS.Web.Models;
using LMS.Web.Services.Setup;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LMS.Web.Controllers;

/// <summary>First-run installation wizard: database → organisation → administrator → AI grading
/// → review & install. Reachable only until the installation is completed; afterwards every
/// action redirects to the LMS so the installer can never be re-opened.</summary>
[AllowAnonymous]
public class SetupController : Controller
{
    private readonly SetupState _state;
    private readonly SetupWizardState _w;
    private readonly DbSettings _settings;
    private readonly IWebHostEnvironment _env;
    private readonly IServiceProvider _sp;
    private readonly ILogger<SetupController> _log;

    public SetupController(SetupState state, SetupWizardState wizard, DbSettings settings,
        IWebHostEnvironment env, IServiceProvider sp, ILogger<SetupController> log)
    { _state = state; _w = wizard; _settings = settings; _env = env; _sp = sp; _log = log; }

    private IActionResult? Guard() => _state.SetupMode ? null : RedirectToAction("Index", "Home");

    private IActionResult StepView(string view, SetupStep step)
    {
        _w.Step = step;
        ViewBag.Step = step;
        ViewBag.Wizard = _w;
        return View(view, _w);
    }

    // Entry point — always resumes at the right step.
    public IActionResult Index()
    {
        if (Guard() is { } r) return r;
        if (!_w.DatabaseDone) return RedirectToAction(nameof(Welcome));
        if (!_w.OrganisationDone) return RedirectToAction(nameof(Organisation));
        if (!_w.AdministratorDone) return RedirectToAction(nameof(Administrator));
        return RedirectToAction(nameof(Review));
    }

    // ---------- 1. Welcome / prerequisites ----------
    public IActionResult Welcome()
    {
        if (Guard() is { } r) return r;
        ViewBag.Checks = EnvironmentProbe.Run(_env, Path.GetDirectoryName(_state.SetupFilePath) ?? ".");
        return StepView("Welcome", SetupStep.Welcome);
    }

    // ---------- 2. Database ----------
    public IActionResult Database()
    {
        if (Guard() is { } r) return r;
        return StepView("Database", SetupStep.Database);
    }

    private static (DbProvider p, string conn) Resolve(string provider, string? host, string? port,
        string? database, string? username, string? password, string? sqlitePath, string? rawConnectionString)
    {
        var p = SetupStore.ParseProvider(provider);
        var conn = !string.IsNullOrWhiteSpace(rawConnectionString)
            ? rawConnectionString!.Trim()
            : ConnStringFactory.Build(p, host, port, database, username, password, sqlitePath);
        return (p, conn);
    }

    /// <summary>AJAX: validate the database connection before committing.</summary>
    [HttpPost]
    public async Task<IActionResult> Test(string provider, string? host, string? port, string? database,
        string? username, string? password, string? sqlitePath, string? rawConnectionString)
    {
        if (!_state.SetupMode) return NotFound();
        var (p, conn) = Resolve(provider, host, port, database, username, password, sqlitePath, rawConnectionString);
        var (ok, message) = await ConnectionTester.TestAsync(p, conn);
        return Json(new { ok, message });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveDatabase(string provider, string? host, string? port, string? database,
        string? username, string? password, string? sqlitePath, string? rawConnectionString)
    {
        if (Guard() is { } r) return r;
        var (p, conn) = Resolve(provider, host, port, database, username, password, sqlitePath, rawConnectionString);

        var (ok, message) = await ConnectionTester.TestAsync(p, conn);
        if (!ok) { TempData["Err"] = "Could not connect to the database: " + message; return RedirectToAction(nameof(Database)); }

        // Persist the choice (not yet complete), apply it live, then create the schema.
        SetupStore.Save(_state.SetupFilePath, p, conn, completed: false, ollamaUrl: _w.OllamaUrl);
        _settings.Provider = p; _settings.ConnectionString = conn; _settings.MySqlVersion = null; _settings.Configured = true;
        try
        {
            await DbInitializer.EnsureSchemaAsync(_sp);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Schema creation failed during setup.");
            try { System.IO.File.Delete(_state.SetupFilePath); } catch { }
            _settings.Configured = false;
            TempData["Err"] = "Connected, but preparing the database failed: " + ex.Message;
            return RedirectToAction(nameof(Database));
        }
        _w.DatabaseDone = true;
        return RedirectToAction(nameof(Organisation));
    }

    // ---------- 3. Organisation ----------
    public IActionResult Organisation()
    {
        if (Guard() is { } r) return r;
        if (!_w.DatabaseDone) return RedirectToAction(nameof(Database));
        return StepView("Organisation", SetupStep.Organisation);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public IActionResult SaveOrganisation(string orgName, string orgCode, string? contactEmail,
        string? contactPhone, string? address, string? siteName)
    {
        if (Guard() is { } r) return r;
        if (string.IsNullOrWhiteSpace(orgName) || string.IsNullOrWhiteSpace(orgCode))
        { TempData["Err"] = "Organisation name and code are required."; return RedirectToAction(nameof(Organisation)); }

        _w.OrgName = orgName.Trim();
        _w.OrgCode = new string(orgCode.Trim().ToUpperInvariant().Where(c => char.IsLetterOrDigit(c)).ToArray());
        if (_w.OrgCode.Length == 0) { TempData["Err"] = "Organisation code must contain letters or digits."; return RedirectToAction(nameof(Organisation)); }
        _w.OrgContactEmail = contactEmail?.Trim();
        _w.OrgContactPhone = contactPhone?.Trim();
        _w.OrgAddress = address?.Trim();
        _w.SiteName = string.IsNullOrWhiteSpace(siteName) ? _w.OrgName : siteName.Trim();
        return RedirectToAction(nameof(Administrator));
    }

    // ---------- 4. Administrator ----------
    public IActionResult Administrator()
    {
        if (Guard() is { } r) return r;
        if (!_w.OrganisationDone) return RedirectToAction(nameof(Organisation));
        return StepView("Administrator", SetupStep.Administrator);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public IActionResult SaveAdministrator(string adminName, string adminEmail, string password,
        string confirmPassword, bool superUser = false)
    {
        if (Guard() is { } r) return r;
        if (string.IsNullOrWhiteSpace(adminName) || string.IsNullOrWhiteSpace(adminEmail))
        { TempData["Err"] = "Name and email are required."; return RedirectToAction(nameof(Administrator)); }
        if (password != confirmPassword)
        { TempData["Err"] = "The two passwords do not match."; return RedirectToAction(nameof(Administrator)); }
        if ((password ?? "").Length < 8)
        { TempData["Err"] = "The password must be at least 8 characters."; return RedirectToAction(nameof(Administrator)); }

        _w.AdminName = adminName.Trim();
        _w.AdminEmail = adminEmail.Trim();
        _w.AdminPassword = password!;
        _w.AdminIsSuperUser = superUser;
        return RedirectToAction(nameof(Grading));
    }

    // ---------- 5. AI grading ----------
    public IActionResult Grading()
    {
        if (Guard() is { } r) return r;
        if (!_w.AdministratorDone) return RedirectToAction(nameof(Administrator));
        return StepView("Grading", SetupStep.Grading);
    }

    /// <summary>AJAX: probe the local Ollama server and report which models are installed.</summary>
    [HttpPost]
    public async Task<IActionResult> TestOllama(string url)
    {
        if (!_state.SetupMode) return NotFound();
        var (up, models, message) = await OllamaProbe.InspectAsync(string.IsNullOrWhiteSpace(url) ? "http://localhost:11434" : url.Trim());
        bool hasGrader = models.Any(m => m.StartsWith("qwen2.5", StringComparison.OrdinalIgnoreCase));
        bool hasEmbed = models.Any(m => m.StartsWith("nomic-embed-text", StringComparison.OrdinalIgnoreCase));
        return Json(new { ok = up, message, models, hasGrader, hasEmbed });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public IActionResult SaveGrading(bool gradingEnabled, string? ollamaUrl, string gradingMode)
    {
        if (Guard() is { } r) return r;
        _w.GradingEnabled = gradingEnabled;
        _w.OllamaUrl = string.IsNullOrWhiteSpace(ollamaUrl) ? "http://localhost:11434" : ollamaUrl.Trim();
        _w.GradingMode = string.Equals(gradingMode, "Gpu", StringComparison.OrdinalIgnoreCase) ? GradingMode.Gpu : GradingMode.Cpu;
        return RedirectToAction(nameof(Review));
    }

    // ---------- 6. Review & install ----------
    public IActionResult Review()
    {
        if (Guard() is { } r) return r;
        if (!_w.AdministratorDone) return RedirectToAction(nameof(Administrator));
        return StepView("Review", SetupStep.Review);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Install(bool installDemoData = false)
    {
        if (Guard() is { } r) return r;
        _w.InstallDemoData = installDemoData;
        try
        {
            if (installDemoData)
            {
                await Data.DbSeeder.SeedAsync(_sp);                     // full demo dataset
                await SetupSeeder.ApplyDemoSettingsAsync(_sp, _w);
            }
            else
            {
                await SetupSeeder.SeedCleanAsync(_sp, _w);              // customer's own organisation + admin
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Installation failed at the final step.");
            TempData["Err"] = "Installation failed: " + ex.Message;
            return RedirectToAction(nameof(Review));
        }

        // Mark the installation complete and lock the wizard.
        SetupStore.Save(_state.SetupFilePath, _settings.Provider, _settings.ConnectionString,
            completed: true, ollamaUrl: _w.OllamaUrl);
        _state.SetupMode = false;
        _log.LogInformation("LMS installation completed: {Org} on {Provider}.", _w.OrgName, _settings.Provider);

        ViewBag.DemoData = installDemoData;
        ViewBag.SignInEmail = installDemoData ? "admin@punemetro.in" : _w.AdminEmail;
        ViewBag.SignInHint = installDemoData ? "Pass@123" : "the password you just set";
        return View("Done", _w);
    }
}

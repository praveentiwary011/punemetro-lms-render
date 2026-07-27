using LMS.Web.Data;
using LMS.Web.Models;
using LMS.Web.Services;
using LMS.Web.Services.Sso;
using Microsoft.AspNetCore.Authentication;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;

namespace LMS.Web.Controllers;

public class LoginVm
{
    [Required, EmailAddress] public string Email { get; set; } = "";
    [Required, DataType(DataType.Password)] public string Password { get; set; } = "";
    public bool RememberMe { get; set; }
}

public class RegisterVm
{
    [Required, MaxLength(100)] public string FullName { get; set; } = "";
    [Required, EmailAddress] public string Email { get; set; } = "";
    [Required, DataType(DataType.Password), MinLength(6)] public string Password { get; set; } = "";
    [DataType(DataType.Password), Compare("Password")] public string ConfirmPassword { get; set; } = "";
}

public class AccountController : Controller
{
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly AppDbContext _db;
    private readonly IWebHostEnvironment _env;
    private readonly SsoService _sso;
    private readonly ILogger<AccountController> _log;

    public AccountController(SignInManager<ApplicationUser> signInManager, UserManager<ApplicationUser> userManager,
        AppDbContext db, IWebHostEnvironment env, SsoService sso, ILogger<AccountController> log)
    {
        _signInManager = signInManager;
        _userManager = userManager;
        _db = db;
        _env = env;
        _sso = sso;
        _log = log;
    }

    /// <summary>The account, organisation and licence gates that every sign-in must pass —
    /// shared verbatim by password and SSO sign-in so the two can never drift apart and SSO
    /// can never become a way around deactivation or licensing (§AUTH-10).</summary>
    private async Task<string?> CheckSignInGatesAsync(ApplicationUser user)
    {
        if (!user.IsActive)
            return "This account has been deactivated. Contact your administrator.";

        if (user.OrganisationId != null)
        {
            var org = await _db.Organisations.IgnoreQueryFilters().AsNoTracking()
                .FirstOrDefaultAsync(o => o.Id == user.OrganisationId);
            if (org != null && !org.IsActive)
                return "Your organisation's access to this platform is deactivated. Contact the administrator.";

            if (org != null && org.Code != Branding.OwnerOrgCode && !await _userManager.IsInRoleAsync(user, "SuperUser"))
            {
                var today = DateTime.UtcNow.Date;
                var licensed = await _db.SubscriptionLicenses.IgnoreQueryFilters().AnyAsync(l =>
                    l.OrganisationId == org.Id && l.StartDate <= today &&
                    (l.ValidityType == LicenseValidityType.NeverExpires || l.EndDate >= today));
                if (!licensed)
                    return "Your organisation's subscription license has expired or is not yet active. Please contact the platform provider to renew.";
            }
        }
        return null;   // all gates passed
    }

    /// <summary>Establishes the session once the gates pass. Goes through SignInManager so
    /// MappedRolesClaimsFactory still stamps org_id and the mapped platform-role claims.</summary>
    private async Task EstablishSessionAsync(ApplicationUser user, bool rememberMe, string how)
    {
        await _signInManager.SignInAsync(user, rememberMe);
        var roles = await _userManager.GetRolesAsync(user);
        var primary = ActiveRole.Priority.FirstOrDefault(roles.Contains) ?? "Student";
        Response.Cookies.Append(ActiveRole.CookieName, primary, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Expires = DateTimeOffset.UtcNow.AddDays(30)
        });
        Notifier.Audit(_db, user.Id, user.FullName, how);
        await _db.SaveChangesAsync();
    }

    [HttpGet]
    public async Task<IActionResult> Login(string? returnUrl = null, int? ssoError = null)
    {
        ViewBag.ReturnUrl = returnUrl;
        var sso = await _sso.GetActiveAsync();
        ViewBag.Sso = sso;                                  // null = no SSO offered
        if (ssoError == 1)
            ModelState.AddModelError("", "Single sign-on did not complete. Try again, or sign in with your password.");
        return View();
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(LoginVm vm, string? returnUrl = null)
    {
        if (!ModelState.IsValid) return View(vm);
        var user = await _userManager.FindByEmailAsync(vm.Email);

        // Verify the password FIRST (with lockout). Account, organisation and licence
        // state must never be revealed to an unauthenticated caller, so those gates run
        // only after the credentials are proven — no pre-authentication oracle.
        if (user != null)
        {
            var check = await _signInManager.CheckPasswordSignInAsync(user, vm.Password, lockoutOnFailure: true);
            if (check.IsLockedOut)
            {
                ModelState.AddModelError("", "Account temporarily locked after repeated failed attempts. Try again in a few minutes.");
                return View(vm);
            }
            if (check.Succeeded)
            {
                // Credentials proven — the account/organisation gates may now speak freely.
                var denied = await CheckSignInGatesAsync(user);
                if (denied != null) { ModelState.AddModelError("", denied); ViewBag.Sso = await _sso.GetActiveAsync(); return View(vm); }

                // Break-glass rule: an organisation may require SSO, but a Super User can
                // always use a password so an identity-provider outage cannot lock everyone out.
                var ssoCfg = await _sso.GetActiveAsync();
                if (ssoCfg is { IsEnabled: true, AllowLocalPassword: false }
                    && user.OrganisationId == ssoCfg.OrganisationId
                    && !await _userManager.IsInRoleAsync(user, "SuperUser"))
                {
                    ModelState.AddModelError("", $"Your organisation requires single sign-on. Please use the \"{ssoCfg.DisplayName}\" button above.");
                    ViewBag.Sso = ssoCfg;
                    return View(vm);
                }

                await EstablishSessionAsync(user, vm.RememberMe, "Login");
                if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl)) return Redirect(returnUrl);
                return RedirectToAction("Index", "Home");
            }
        }
        // Unknown email or wrong password — one generic message, no account disclosure.
        ModelState.AddModelError("", "Invalid credentials.");
        ViewBag.Sso = await _sso.GetActiveAsync();
        return View(vm);
    }

    // ---------------- Single sign-on (§AUTH-09/10) ----------------

    /// <summary>Starts the OIDC flow: hands the browser to the organisation's identity provider.</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ExternalLogin(string? returnUrl = null)
    {
        var cfg = await _sso.GetActiveAsync();
        if (cfg == null) return RedirectToAction(nameof(Login));

        var redirectUrl = Url.Action(nameof(ExternalLoginCallback), "Account", new { returnUrl });
        var props = _signInManager.ConfigureExternalAuthenticationProperties(SsoService.Scheme, redirectUrl);
        return Challenge(props, SsoService.Scheme);
    }

    /// <summary>Returns from the identity provider. The external identity only proves WHO the
    /// person is — it grants nothing. The same account/organisation/licence gates as password
    /// sign-in run here, and the session is established through SignInManager so the mapped
    /// role claims and org_id are stamped exactly as usual.</summary>
    [HttpGet]
    public async Task<IActionResult> ExternalLoginCallback(string? returnUrl = null, string? remoteError = null)
    {
        if (remoteError != null)
        {
            _log.LogWarning("SSO returned an error: {Error}", remoteError);
            return RedirectToAction(nameof(Login), new { ssoError = 1 });
        }

        var cfg = await _sso.GetActiveAsync();
        var info = await _signInManager.GetExternalLoginInfoAsync();
        if (cfg == null || info == null) return RedirectToAction(nameof(Login), new { ssoError = 1 });

        var email = info.Principal.FindFirstValue(ClaimTypes.Email)
                 ?? info.Principal.FindFirstValue("email");
        var name  = info.Principal.FindFirstValue("name")
                 ?? info.Principal.FindFirstValue(ClaimTypes.Name) ?? email ?? "SSO user";

        // 1. Already linked to this identity provider subject?
        var user = await _userManager.FindByLoginAsync(info.LoginProvider, info.ProviderKey);

        // 2. Otherwise link by email — but only a verified address from a federated domain,
        //    so a provider cannot assert someone else's mailbox and take over their account.
        if (user == null)
        {
            var verified = info.Principal.FindFirstValue("email_verified");
            var emailIsTrusted = string.IsNullOrEmpty(verified) || verified.Equals("true", StringComparison.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(email) || !emailIsTrusted || !SsoService.DomainMatches(cfg, email))
            {
                await HttpContext.SignOutAsync(IdentityConstants.ExternalScheme);
                TempData["Err"] = "Single sign-on did not provide a verified email address from your organisation's domain.";
                return RedirectToAction(nameof(Login));
            }

            user = await _userManager.FindByEmailAsync(email);

            // 3. Still nothing — provision just-in-time when the organisation allows it.
            if (user == null)
            {
                if (!cfg.JitProvisioning)
                {
                    TempData["Err"] = "No LMS account exists for this sign-in. Please ask your administrator to create one.";
                    return RedirectToAction(nameof(Login));
                }
                user = new ApplicationUser
                {
                    UserName = email, Email = email, FullName = name,
                    EmailConfirmed = true, IsActive = true, OrganisationId = cfg.OrganisationId
                };
                var created = await _userManager.CreateAsync(user);
                if (!created.Succeeded)
                {
                    _log.LogError("SSO JIT provisioning failed for {Email}: {Errors}", email,
                        string.Join("; ", created.Errors.Select(e => e.Description)));
                    TempData["Err"] = "Your account could not be created automatically. Please contact your administrator.";
                    return RedirectToAction(nameof(Login));
                }
                await ApplyMappedRolesAsync(user, cfg, info);
                Notifier.Audit(_db, user.Id, user.FullName, "SsoUserProvisioned", $"{cfg.DisplayName}: {email}");
                await _db.SaveChangesAsync();
            }
            else if (user.OrganisationId != cfg.OrganisationId)
            {
                // The address exists but belongs to another tenant — never cross the boundary.
                TempData["Err"] = "This account belongs to a different organisation and cannot use this sign-in.";
                return RedirectToAction(nameof(Login));
            }

            await _userManager.AddLoginAsync(user, info);   // remember the link for next time
        }

        // 4. Identity established — now the SAME gates as password sign-in.
        var denied = await CheckSignInGatesAsync(user);
        if (denied != null)
        {
            await HttpContext.SignOutAsync(IdentityConstants.ExternalScheme);   // drop the temporary external cookie
            TempData["Err"] = denied;
            return RedirectToAction(nameof(Login));
        }

        // Drop Identity's temporary external cookie BEFORE issuing the application cookie,
        // so the sign-out cannot clear the session we are about to create.
        await HttpContext.SignOutAsync(IdentityConstants.ExternalScheme);
        await EstablishSessionAsync(user, rememberMe: false, how: "LoginSso");

        if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl)) return Redirect(returnUrl);
        return RedirectToAction("Index", "Home");
    }

    /// <summary>Applies the organisation's "IdP group = LMS role" mapping to a user.</summary>
    private async Task ApplyMappedRolesAsync(ApplicationUser user, SsoConfiguration cfg, ExternalLoginInfo info)
    {
        var groups = string.IsNullOrWhiteSpace(cfg.RoleClaimName)
            ? new List<string>()
            : info.Principal.FindAll(cfg.RoleClaimName!).Select(c => c.Value).ToList();

        foreach (var roleName in SsoService.MapRoles(cfg, groups))
        {
            // An organisation role carries its mapped platform role, so add both — exactly
            // what the Users page does when an admin assigns a company role.
            if (await _db.Roles.AnyAsync(r => r.Name == roleName))
                await _userManager.AddToRoleAsync(user, roleName);

            var orgRole = await _db.OrganisationRoles.IgnoreQueryFilters()
                .FirstOrDefaultAsync(r => r.OrganisationId == cfg.OrganisationId && r.Name == roleName);
            if (orgRole != null && await _db.Roles.AnyAsync(r => r.Name == orgRole.MapsToRole))
                await _userManager.AddToRoleAsync(user, orgRole.MapsToRole);
        }
    }

    // Public self-registration is disabled. This is a multi-tenant platform: accounts
    // are provisioned by an organisation Admin or the platform Super User so that every
    // user belongs to a tenant and is subject to its subscription licensing. A
    // self-registered account would belong to no organisation and bypass those gates.
    [HttpGet]
    public IActionResult Register()
    {
        TempData["Err"] = "Accounts are created by your organisation's administrator — please contact them for access.";
        return RedirectToAction("Login");
    }

    [HttpPost, ValidateAntiForgeryToken]
    public IActionResult Register(RegisterVm vm)
    {
        TempData["Err"] = "Public registration is disabled. Contact your organisation's administrator for access.";
        return RedirectToAction("Login");
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await _signInManager.SignOutAsync();
        Response.Cookies.Delete(ActiveRole.CookieName);
        return RedirectToAction("Index", "Home");
    }

    [Authorize]
    [HttpGet]
    public async Task<IActionResult> Profile()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return NotFound();
        return View(user);
    }

    [Authorize]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Profile(string fullName, string? bio, string? department)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return NotFound();
        user.FullName = fullName;
        user.Bio = bio;
        if (!string.IsNullOrWhiteSpace(department)) user.Department = department.Trim();
        await _userManager.UpdateAsync(user);
        TempData["Ok"] = "Profile updated.";
        return RedirectToAction("Profile");
    }

    /// <summary>Self-service signature upload — a trainer's signature appears in the
    /// Course Instructor slot of certificates for courses they teach, and the
    /// organisation's chosen signatory signs the Training Director slot.</summary>
    [Authorize(Roles = "Instructor,Principal,Admin,SuperUser")]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> UploadSignature(IFormFile? signature)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return NotFound();
        if (signature == null || signature.Length == 0)
        {
            TempData["Err"] = "Choose a signature image to upload.";
            return RedirectToAction("Profile");
        }
        // Validate by decoding the pixels (not the extension) and re-encode to a clean PNG.
        var url = await UploadHelper.SaveImageAsync(signature, _env, "signatures", 1_000_000);
        if (url == null)
        {
            TempData["Err"] = "Signature must be a valid image (PNG, JPG or WebP) under 1 MB.";
            return RedirectToAction("Profile");
        }
        var previousUrl = user.SignatureUrl;
        user.SignatureUrl = url;
        await _userManager.UpdateAsync(user);   // commits the SignatureUrl — the file now has an owning row
        UploadHelper.Commit(HttpContext);
        UploadHelper.TryDeleteStored(previousUrl, _env);   // replacement succeeded — remove the old file
        Notifier.Audit(_db, user.Id, user.Email ?? "", "UploadSignature", user.Email);
        await _db.SaveChangesAsync();
        TempData["Ok"] = "Signature uploaded — it now appears on certificates you sign.";
        return RedirectToAction("Profile");
    }

    [Authorize]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ChangePassword(string currentPassword, string newPassword)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return NotFound();
        var result = await _userManager.ChangePasswordAsync(user, currentPassword, newPassword);
        TempData[result.Succeeded ? "Ok" : "Err"] = result.Succeeded
            ? "Password changed."
            : string.Join(" ", result.Errors.Select(e => e.Description));
        return RedirectToAction("Profile");
    }

    [Authorize]
    [HttpPost, ValidateAntiForgeryToken]
    public IActionResult SwitchRole(string role)
    {
        if (ActiveRole.RolesOf(User).Contains(role))
        {
            Response.Cookies.Append(ActiveRole.CookieName, role, new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Lax,
                Expires = DateTimeOffset.UtcNow.AddDays(30)
            });
            TempData["Ok"] = $"Now viewing as {ActiveRole.Label(role)}.";
        }
        return RedirectToAction("Index", "Home");
    }

    public IActionResult AccessDenied() => View();
}

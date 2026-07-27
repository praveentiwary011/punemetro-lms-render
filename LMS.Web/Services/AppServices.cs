using LMS.Web.Data;
using LMS.Web.Models;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace LMS.Web.Services;

public static class UserExtensions
{
    public static string GetUserId(this ClaimsPrincipal user) =>
        user.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
}

/// <summary>Resolves the tenant name to show in body copy so a user always sees
/// their own organisation's branding, never a hard-coded "Pune Metro".</summary>
public static class Branding
{
    /// <summary>Code of the platform owner's organisation — exempt from
    /// subscription licensing and undeactivatable while it hosts Super Users.</summary>
    public const string OwnerOrgCode = "ABSOLUTESYS";

    /// <summary>The signed-in user's organisation name, or <paramref name="fallback"/>
    /// (a sensible platform default) when the user has no organisation.</summary>
    public static async Task<string> OrgNameAsync(AppDbContext db, ClaimsPrincipal user, string fallback = "your organisation")
    {
        if (user.Identity?.IsAuthenticated != true) return fallback;
        var name = await db.Users.Where(u => u.Id == user.GetUserId())
            .Select(u => u.Organisation!.Name).FirstOrDefaultAsync();
        return string.IsNullOrWhiteSpace(name) ? fallback : name;
    }
}

/// <summary>
/// A user may hold several roles; the "active role" decides which
/// dashboard and menu they currently see. Stored in a cookie and
/// always validated against the user's real roles.
/// </summary>
public static class ActiveRole
{
    public const string CookieName = "lms_active_role";
    public static readonly string[] Priority = { "SuperUser", "Admin", "Principal", "Instructor", "Student" };

    public static List<string> RolesOf(ClaimsPrincipal user) =>
        user.Claims.Where(c => c.Type == ClaimTypes.Role).Select(c => c.Value).Distinct().ToList();

    public static string Get(HttpContext ctx)
    {
        var roles = RolesOf(ctx.User);
        var cookie = ctx.Request.Cookies[CookieName];
        if (!string.IsNullOrEmpty(cookie) && roles.Contains(cookie)) return cookie;
        return Priority.FirstOrDefault(roles.Contains) ?? "Student";
    }

    public static string Label(string role) => role switch
    {
        "SuperUser" => "Super User",
        "Admin" => "Administrator",
        "Principal" => "Principal",
        "Instructor" => "Trainer",
        _ => "Trainee"
    };
}

/// <summary>
/// Allow-list HTML sanitizer for staff-authored HTML (lesson content, assignment
/// briefs). Backed by the AngleSharp-based Ganss HtmlSanitizer, which parses the input
/// into a real DOM and keeps only an explicit allow-list of tags, attributes and URL
/// schemes. This closes the classes of bypass a regex filter cannot: single-pass
/// nested-tag reassembly (<c>&lt;scr&lt;script&gt;ipt&gt;</c>), obfuscated
/// <c>javascript:</c> URLs (embedded whitespace/entities), SVG <c>xlink:href</c>
/// vectors, and malformed markup. A shared, immutably-configured instance is reused;
/// Sanitize is thread-safe.
/// </summary>
public static class HtmlSanitizer
{
    private static readonly Ganss.Xss.HtmlSanitizer Sanitizer = CreateSanitizer();

    private static Ganss.Xss.HtmlSanitizer CreateSanitizer()
    {
        // The library defaults already forbid script/style/iframe/object/embed/form,
        // inline event handlers, CSS expressions and every non-http(s) URL scheme
        // (javascript:, data:, vbscript: …). We only widen the tag allow-list slightly
        // for the presentational markup training content uses.
        var s = new Ganss.Xss.HtmlSanitizer();
        s.AllowedTags.Add("figure");
        s.AllowedTags.Add("figcaption");
        return s;
    }

    public static string? Clean(string? html) =>
        string.IsNullOrEmpty(html) ? html : Sanitizer.Sanitize(html);
}

public static class Notifier
{
    public static void Notify(AppDbContext db, string userId, string title, string? link = null)
    {
        db.Notifications.Add(new Notification { UserId = userId, Title = title, Link = link });
    }

    public static void NotifyCourse(AppDbContext db, IEnumerable<string> studentIds, string title, string? link = null)
    {
        foreach (var id in studentIds)
            db.Notifications.Add(new Notification { UserId = id, Title = title, Link = link });
    }

    public static void Audit(AppDbContext db, string? userId, string userName, string action, string? details = null)
    {
        db.AuditLogs.Add(new AuditLog { UserId = userId, UserName = userName, Action = action, Details = details });
    }
}

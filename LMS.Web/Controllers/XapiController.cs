using System.Text;
using System.Text.Json;
using LMS.Web.Data;
using LMS.Web.Models;
using LMS.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LMS.Web.Controllers;

/// <summary>
/// Minimal xAPI Learning Record Store used by cmi5/xAPI content.
/// Auth: the signed cmi5 auth-token (Basic) issued at launch, or the LMS session cookie.
/// </summary>
[AllowAnonymous]
[Route("xapi")]
[IgnoreAntiforgeryToken]
public class XapiController : Controller
{
    private readonly AppDbContext _db;
    private readonly IDataProtectionProvider _dp;
    public XapiController(AppDbContext db, IDataProtectionProvider dp)
    {
        _db = db;
        _dp = dp;
    }

    /// <summary>Resolve the learner from the signed Basic auth-token or the session
    /// cookie. The token is a Data Protection–protected, time-limited payload minted at
    /// launch (see ScormController); a tampered, forged or expired token fails to
    /// unprotect and is rejected — the identity is never taken from client-supplied
    /// plaintext.</summary>
    private async Task<(ApplicationUser? User, int PackageId)> ResolveAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var raw = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..].Trim()));
                // cmi5 sends the auth-token as the Basic username ("token:") or bare.
                var protectedToken = raw.Contains(':') ? raw.Split(':')[0] : raw;
                var payload = _dp.CreateProtector(Xapi.LaunchTokenPurpose)
                    .ToTimeLimitedDataProtector()
                    .Unprotect(protectedToken);
                var parts = payload.Split('|');
                if (parts.Length == 2 && int.TryParse(parts[1], out var pkgId))
                    return (await _db.Users.FindAsync(parts[0]), pkgId);
            }
            catch { /* invalid/expired/tampered token — fall through to cookie auth */ }
        }
        if (User.Identity?.IsAuthenticated == true)
            return (await _db.Users.FindAsync(User.GetUserId()), 0);
        return (null, 0);
    }

    // ---------------- Statements resource ----------------
    [HttpPost("statements")]
    [HttpPut("statements")]
    public async Task<IActionResult> Statements(string? statementId)
    {
        var (user, tokenPackageId) = await ResolveAsync();
        if (user == null) return Unauthorized();

        using var reader = new StreamReader(Request.Body);
        var body = await reader.ReadToEndAsync();
        if (string.IsNullOrWhiteSpace(body)) return BadRequest();

        var ids = new List<string>();
        var touchedCourses = new HashSet<int>();
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var st in doc.RootElement.EnumerateArray())
                ids.Add(await ProcessStatementAsync(st, user, tokenPackageId, touchedCourses));
        }
        else
        {
            ids.Add(await ProcessStatementAsync(doc.RootElement, user, tokenPackageId, touchedCourses));
        }
        await _db.SaveChangesAsync();

        // A cmi5 completion may be the last lesson of the course — auto-complete it.
        var completedAny = false;
        foreach (var courseId in touchedCourses)
            completedAny |= await CourseCompletion.CheckAsync(_db, courseId, user.Id);
        if (completedAny) await _db.SaveChangesAsync();

        return Request.Method == "PUT" ? NoContent() : Json(ids);
    }

    [HttpGet("statements")]
    public async Task<IActionResult> GetStatements(int limit = 25)
    {
        var (user, _) = await ResolveAsync();
        if (user == null) return Unauthorized();

        // Privacy scoping: only Admin/Principal may read the full statement stream;
        // everyone else (incl. content auth-tokens) sees only their own statements.
        var query = _db.XapiStatements.AsNoTracking().AsQueryable();
        var isPrivileged = User.Identity?.IsAuthenticated == true &&
                           (User.IsInRole("Admin") || User.IsInRole("Principal"));
        if (!isPrivileged)
            query = query.Where(s => s.ActorAccount == user.Email);

        var statements = await query
            .OrderByDescending(s => s.Stored).Take(Math.Clamp(limit, 1, 100))
            .Select(s => s.StatementJson).ToListAsync();
        return Content($"{{\"statements\":[{string.Join(",", statements)}],\"more\":\"\"}}", "application/json");
    }

    private async Task<string> ProcessStatementAsync(JsonElement statement, ApplicationUser user, int tokenPackageId, HashSet<int> touchedCourses)
    {
        var id = Xapi.Store(_db, statement);

        // cmi5 defined statements: completed / passed → lesson progress
        var verb = statement.TryGetProperty("verb", out var v) && v.TryGetProperty("id", out var vid) ? vid.GetString() : null;
        var activityId = statement.TryGetProperty("object", out var o) && o.TryGetProperty("id", out var oid) ? oid.GetString() : null;
        if ((verb == Xapi.VerbCompleted || verb == Xapi.VerbPassed) && activityId != null)
        {
            var marker = "/scorm/";
            var idx = activityId.LastIndexOf(marker, StringComparison.Ordinal);
            if (idx >= 0 && int.TryParse(activityId[(idx + marker.Length)..].TrimEnd('/'), out var pkgId))
            {
                // A launch token is scoped to one package — refuse to record completion
                // for any other package than the one this learner actually launched.
                if (tokenPackageId != 0 && pkgId != tokenPackageId) return id;

                var lesson = await _db.Lessons.Include(l => l.Module)
                    .FirstOrDefaultAsync(l => l.ContentPackageId == pkgId);
                // Only award progress/points when the learner is genuinely enrolled in
                // the course that owns the lesson.
                var enrolled = lesson?.Module != null && await _db.Enrollments.AnyAsync(e =>
                    e.CourseId == lesson.Module.CourseId && e.StudentId == user.Id && e.Status != EnrollmentStatus.Dropped);
                if (enrolled)
                {
                    touchedCourses.Add(lesson!.Module!.CourseId);   // re-evaluate course completion after save
                    if (!await _db.LessonProgress.AnyAsync(p => p.LessonId == lesson.Id && p.StudentId == user.Id))
                    {
                        _db.LessonProgress.Add(new LessonProgress { LessonId = lesson.Id, StudentId = user.Id });
                        user.Points += 10;
                    }
                }
            }
        }
        return id;
    }

    // ---------------- State resource (cmi5 LMS.LaunchData etc.) ----------------
    [HttpGet("activities/state")]
    public async Task<IActionResult> GetState(string stateId, string? registration)
    {
        var (user, _) = await ResolveAsync();
        if (user == null) return Unauthorized();

        if (stateId == "LMS.LaunchData")
        {
            return Json(new Dictionary<string, object?>
            {
                ["launchMode"] = "Normal",
                ["moveOn"] = "CompletedOrPassed",
                ["masteryScore"] = 0.6,
                ["contextTemplate"] = new Dictionary<string, object?>
                {
                    ["registration"] = registration,
                    ["contextActivities"] = new { grouping = new[] { new { id = "https://lms.punemetro.in" } } }
                }
            });
        }
        return NotFound();
    }

    [HttpPut("activities/state")]
    [HttpPost("activities/state")]
    public async Task<IActionResult> PutState()
    {
        var (user, _) = await ResolveAsync();
        return user == null ? Unauthorized() : NoContent();
    }

    // ---------------- Agents profile (optional cmi5 call) ----------------
    [HttpGet("agents/profile")]
    public IActionResult AgentsProfile() => Json(new { languagePreference = "en-US" });

    // ---------------- About ----------------
    [HttpGet("about")]
    public IActionResult About() => Json(new { version = new[] { "1.0.3" }, extensions = new { cmi5 = "supported", scorm12 = "supported" } });
}

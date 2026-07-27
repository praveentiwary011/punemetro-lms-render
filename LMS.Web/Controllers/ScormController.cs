using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;
using LMS.Web.Data;
using LMS.Web.Models;
using LMS.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LMS.Web.Controllers;

[Authorize]
public class ScormController : Controller
{
    private readonly AppDbContext _db;
    private readonly IWebHostEnvironment _env;
    private readonly IDataProtectionProvider _dp;

    public ScormController(AppDbContext db, IWebHostEnvironment env, IDataProtectionProvider dp)
    {
        _db = db;
        _env = env;
        _dp = dp;
    }

    // ------------------------------------------------------------------
    // Upload a SCORM 1.2 / cmi5 package zip and attach it as a lesson
    // ------------------------------------------------------------------
    [Authorize(Roles = "Instructor,Admin,Principal")]
    [HttpPost, ValidateAntiForgeryToken]
    [RequestSizeLimit(200_000_000)]
    public async Task<IActionResult> Upload(int moduleId, string title, IFormFile package)
    {
        var module = await _db.Modules.Include(m => m.Course).Include(m => m.Lessons)
            .FirstOrDefaultAsync(m => m.Id == moduleId);
        if (module == null || (!User.IsInRole("Admin") && !User.IsInRole("Principal") && module.Course!.InstructorId != User.GetUserId()))
            return NotFound();
        if (package == null || package.Length == 0 || !package.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            TempData["Err"] = "Upload a .zip SCORM 1.2 or cmi5 package.";
            return RedirectToAction("ManageCourse", "Instructor", new { id = module.CourseId });
        }

        var folder = Guid.NewGuid().ToString("N");
        var root = Path.Combine(_env.WebRootPath, "scorm", folder);
        Directory.CreateDirectory(root);
        var zipPath = Path.Combine(root, "package.zip");
        using (var fs = System.IO.File.Create(zipPath))
            await package.CopyToAsync(fs);

        // Zip-bomb guard: cap entry count and total uncompressed size before extracting.
        // (ExtractToDirectory itself already rejects path-traversal "zip slip" entries.)
        using (var archive = ZipFile.OpenRead(zipPath))
        {
            const int maxEntries = 2000;
            const long maxUncompressed = 500L * 1024 * 1024;
            if (archive.Entries.Count > maxEntries || archive.Entries.Sum(e => e.Length) > maxUncompressed)
            {
                System.IO.File.Delete(zipPath);
                Directory.Delete(root, recursive: true);
                TempData["Err"] = "Package rejected: too many files or excessive uncompressed size.";
                return RedirectToAction("ManageCourse", "Instructor", new { id = module.CourseId });
            }
        }
        ZipFile.ExtractToDirectory(zipPath, root, overwriteFiles: true);
        System.IO.File.Delete(zipPath);

        // Detect standard: cmi5.xml (cmi5) or imsmanifest.xml (SCORM 1.2)
        ContentStandard standard;
        string launchUrl;
        var cmi5Manifest = Directory.GetFiles(root, "cmi5.xml", SearchOption.AllDirectories).FirstOrDefault();
        var scormManifest = Directory.GetFiles(root, "imsmanifest.xml", SearchOption.AllDirectories).FirstOrDefault();

        if (cmi5Manifest != null)
        {
            standard = ContentStandard.Cmi5;
            var doc = XDocument.Load(cmi5Manifest);
            var au = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "au");
            var urlEl = au?.Descendants().FirstOrDefault(e => e.Name.LocalName == "url");
            launchUrl = urlEl?.Value.Trim() ?? "index.html";
        }
        else if (scormManifest != null)
        {
            standard = ContentStandard.Scorm12;
            var doc = XDocument.Load(scormManifest);
            var resource = doc.Descendants().FirstOrDefault(e =>
                e.Name.LocalName == "resource" && e.Attribute("href") != null);
            launchUrl = resource?.Attribute("href")?.Value ?? "index.html";
            // manifest can live in a subfolder — make launch relative to root
            var manifestDir = Path.GetDirectoryName(Path.GetRelativePath(root, scormManifest))!.Replace('\\', '/');
            if (!string.IsNullOrEmpty(manifestDir) && manifestDir != ".")
                launchUrl = $"{manifestDir}/{launchUrl}";
        }
        else
        {
            Directory.Delete(root, recursive: true);
            TempData["Err"] = "No imsmanifest.xml (SCORM 1.2) or cmi5.xml (cmi5) found in the package.";
            return RedirectToAction("ManageCourse", "Instructor", new { id = module.CourseId });
        }

        var pkg = new ContentPackage
        {
            Title = title, Standard = standard, RootPath = folder,
            LaunchUrl = launchUrl, UploadedById = User.GetUserId()
        };
        // If persisting the package/lesson fails, delete the extracted folder so no
        // orphaned content is left on disk without an owning ContentPackage row.
        try
        {
            _db.ContentPackages.Add(pkg);
            await _db.SaveChangesAsync();

            module.Lessons.Add(new Lesson
            {
                Title = title, Type = LessonType.Scorm, Order = module.Lessons.Count + 1,
                DurationMinutes = 20, ContentPackageId = pkg.Id,
                Content = $"<p>Interactive {(standard == ContentStandard.Cmi5 ? "cmi5" : "SCORM 1.2")} content.</p>"
            });
            Notifier.Audit(_db, User.GetUserId(), User.Identity!.Name ?? "", "UploadScorm", $"{title} ({standard})");
            await _db.SaveChangesAsync();
        }
        catch
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
            throw;
        }
        TempData["Ok"] = $"{(standard == ContentStandard.Cmi5 ? "cmi5" : "SCORM 1.2")} package imported as a lesson.";
        return RedirectToAction("ManageCourse", "Instructor", new { id = module.CourseId });
    }

    // ------------------------------------------------------------------
    // Launch player (SCORM 1.2 API adapter or cmi5 launch URL)
    // ------------------------------------------------------------------
    public async Task<IActionResult> Launch(int lessonId)
    {
        var lesson = await _db.Lessons
            .Include(l => l.ContentPackage)
            .Include(l => l.Module)!.ThenInclude(m => m!.Course)
            .FirstOrDefaultAsync(l => l.Id == lessonId && l.Type == LessonType.Scorm);
        if (lesson?.ContentPackage == null) return NotFound();

        var uid = User.GetUserId();
        var course = lesson.Module!.Course!;
        var isOwner = course.InstructorId == uid || User.IsInRole("Admin") || User.IsInRole("Principal");
        var enrolled = await _db.Enrollments.AnyAsync(e => e.CourseId == course.Id && e.StudentId == uid && e.Status != EnrollmentStatus.Dropped);
        if (!isOwner && !enrolled) return Forbid();

        var pkg = lesson.ContentPackage;
        var user = await _db.Users.FindAsync(uid);

        if (pkg.Standard == ContentStandard.Scorm12)
        {
            var runtime = await _db.ScormRuntimeData
                .FirstOrDefaultAsync(r => r.ContentPackageId == pkg.Id && r.StudentId == uid);
            ViewBag.CmiJson = runtime?.DataJson ?? "{}";
            Xapi.Emit(_db, user!, Xapi.VerbLaunched, "launched",
                $"https://lms.punemetro.in/scorm/{pkg.Id}", pkg.Title);
            await _db.SaveChangesAsync();
        }
        else
        {
            // cmi5 launch parameters (registration is stable per learner+package)
            var registration = DeterministicGuid($"{uid}|{pkg.Id}");
            // Signed, time-limited launch credential — NOT a plain base64 of the id,
            // which any learner could forge for themselves or another user. Data
            // Protection MACs the payload with the app key so the LRS can trust it.
            var token = _dp.CreateProtector(Xapi.LaunchTokenPurpose)
                .ToTimeLimitedDataProtector()
                .Protect($"{uid}|{pkg.Id}", TimeSpan.FromHours(12));
            var endpoint = $"{Request.Scheme}://{Request.Host}/xapi";
            var fetch = $"{Request.Scheme}://{Request.Host}/Scorm/Cmi5Fetch?token={Uri.EscapeDataString(token)}";
            var actor = JsonSerializer.Serialize(new
            {
                objectType = "Agent",
                name = user!.FullName,
                account = new { homePage = "https://lms.punemetro.in", name = user.Email }
            });
            var activityId = $"https://lms.punemetro.in/scorm/{pkg.Id}";
            var sep = pkg.LaunchUrl.Contains('?') ? "&" : "?";
            ViewBag.Cmi5Url = $"/scorm/{pkg.RootPath}/{pkg.LaunchUrl}{sep}endpoint={Uri.EscapeDataString(endpoint)}" +
                              $"&fetch={Uri.EscapeDataString(fetch)}&actor={Uri.EscapeDataString(actor)}" +
                              $"&registration={registration}&activityId={Uri.EscapeDataString(activityId)}";
            ViewBag.Registration = registration;
        }

        ViewBag.Package = pkg;
        ViewBag.User = user;
        ViewBag.ContentUrl = $"/scorm/{pkg.RootPath}/{pkg.LaunchUrl}";
        return View(lesson);
    }

    // ------------------------------------------------------------------
    // SCORM 1.2 runtime persistence (called by the JS API adapter)
    // ------------------------------------------------------------------
    [HttpPost, IgnoreAntiforgeryToken]
    public async Task<IActionResult> Commit(int packageId, int? lessonId)
    {
        // CSRF defence for this cookie-authenticated JSON endpoint: the SCORM
        // adapter always calls same-origin, so reject cross-origin requests.
        var origin = Request.Headers.Origin.ToString();
        if (!string.IsNullOrEmpty(origin) &&
            !origin.Equals($"{Request.Scheme}://{Request.Host}", StringComparison.OrdinalIgnoreCase))
            return Forbid();

        using var reader = new StreamReader(Request.Body);
        var json = await reader.ReadToEndAsync();
        Dictionary<string, string>? cmi;
        try { cmi = JsonSerializer.Deserialize<Dictionary<string, string>>(json); }
        catch { return BadRequest(); }
        if (cmi == null) return BadRequest();

        var uid = User.GetUserId();
        var pkg = await _db.ContentPackages.FindAsync(packageId);
        if (pkg == null) return NotFound();

        var runtime = await _db.ScormRuntimeData
            .FirstOrDefaultAsync(r => r.ContentPackageId == packageId && r.StudentId == uid);
        if (runtime == null)
        {
            runtime = new ScormRuntimeData { ContentPackageId = packageId, StudentId = uid, LessonId = lessonId };
            _db.ScormRuntimeData.Add(runtime);
        }

        runtime.DataJson = json;
        runtime.UpdatedAt = DateTime.UtcNow;
        if (cmi.TryGetValue("cmi.core.lesson_status", out var status))
            runtime.CompletionStatus = status;
        if (cmi.TryGetValue("cmi.core.score.raw", out var raw) && double.TryParse(raw, out var score))
            runtime.ScoreRaw = score;
        if (cmi.TryGetValue("cmi.core.session_time", out var time))
            runtime.TotalTimeSeconds += ParseScormTime(time);

        var user = await _db.Users.FindAsync(uid);
        var completed = status is "completed" or "passed";
        if (completed && lessonId != null &&
            !await _db.LessonProgress.AnyAsync(p => p.LessonId == lessonId && p.StudentId == uid))
        {
            _db.LessonProgress.Add(new LessonProgress { LessonId = lessonId.Value, StudentId = uid });
            if (user != null) user.Points += 10;
        }
        if (completed && user != null)
        {
            Xapi.Emit(_db, user, status == "passed" ? Xapi.VerbPassed : Xapi.VerbCompleted, status ?? "completed",
                $"https://lms.punemetro.in/scorm/{pkg.Id}", pkg.Title,
                runtime.ScoreRaw != null ? runtime.ScoreRaw / 100.0 : null);
        }

        await _db.SaveChangesAsync();

        // A SCORM lesson finishing may be the last lesson of the course.
        if (completed && lessonId != null &&
            await CourseCompletion.CheckByLessonAsync(_db, lessonId.Value, uid))
            await _db.SaveChangesAsync();

        return Json(new { ok = true });
    }

    /// <summary>cmi5 "fetch" URL: exchanges the one-time token for the auth token (per cmi5 spec, POST).</summary>
    [AllowAnonymous]
    [HttpPost, IgnoreAntiforgeryToken]
    public IActionResult Cmi5Fetch(string token) => Json(new Dictionary<string, string> { ["auth-token"] = token });

    private static int ParseScormTime(string t)
    {
        // SCORM 1.2 format HHHH:MM:SS(.ss)
        var parts = t.Split(':');
        if (parts.Length != 3) return 0;
        int.TryParse(parts[0], out var h);
        int.TryParse(parts[1], out var m);
        double.TryParse(parts[2], out var s);
        return h * 3600 + m * 60 + (int)s;
    }

    private static string DeterministicGuid(string input)
    {
        using var md5 = System.Security.Cryptography.MD5.Create();
        var hash = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(input));
        return new Guid(hash).ToString();
    }
}

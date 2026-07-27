using LMS.Web.Data;
using LMS.Web.Models;
using LMS.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LMS.Web.Controllers;

[Authorize]
public class NotificationsController : Controller
{
    private readonly AppDbContext _db;
    public NotificationsController(AppDbContext db) => _db = db;

    public async Task<IActionResult> Index()
    {
        var uid = User.GetUserId();
        var notifications = await _db.Notifications
            .Where(n => n.UserId == uid)
            .OrderByDescending(n => n.CreatedAt).Take(50).ToListAsync();
        return View(notifications);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkAllRead()
    {
        var uid = User.GetUserId();
        var unread = await _db.Notifications.Where(n => n.UserId == uid && n.ReadAt == null).ToListAsync();
        foreach (var n in unread) n.ReadAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return RedirectToAction("Index");
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ClearRead()
    {
        var uid = User.GetUserId();
        var read = await _db.Notifications.Where(n => n.UserId == uid && n.ReadAt != null).ToListAsync();
        _db.Notifications.RemoveRange(read);
        await _db.SaveChangesAsync();
        TempData["Ok"] = "Read notifications cleared.";
        return RedirectToAction("Index");
    }

    public async Task<IActionResult> Open(int id)
    {
        var uid = User.GetUserId();
        var n = await _db.Notifications.FirstOrDefaultAsync(x => x.Id == id && x.UserId == uid);
        if (n == null) return NotFound();
        n.ReadAt ??= DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return !string.IsNullOrEmpty(n.Link) && Url.IsLocalUrl(n.Link) ? Redirect(n.Link) : RedirectToAction("Index");
    }
}

[Authorize]
public class CalendarController : Controller
{
    private readonly AppDbContext _db;
    public CalendarController(AppDbContext db) => _db = db;

    public async Task<IActionResult> Index(int? year, int? month)
    {
        var uid = User.GetUserId();
        var now = DateTime.UtcNow;
        var y = year ?? now.Year;
        var m = month ?? now.Month;

        var courseIds = await _db.Enrollments
            .Where(e => e.StudentId == uid && e.Status != EnrollmentStatus.Dropped)
            .Select(e => e.CourseId).ToListAsync();
        var taughtIds = await _db.Courses.Where(c => c.InstructorId == uid).Select(c => c.Id).ToListAsync();
        courseIds.AddRange(taughtIds);

        var monthStart = new DateTime(y, m, 1);
        var monthEnd = monthStart.AddMonths(1);
        var events = await _db.CalendarEvents
            .Include(e => e.Course)
            .Where(e => e.Start >= monthStart && e.Start < monthEnd &&
                        (e.UserId == uid || (e.CourseId != null && courseIds.Contains(e.CourseId.Value))))
            .OrderBy(e => e.Start).ToListAsync();

        ViewBag.Year = y;
        ViewBag.Month = m;
        return View(events);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(string title, DateTime start, EventType type)
    {
        _db.CalendarEvents.Add(new CalendarEvent { Title = title, Start = start, Type = type, UserId = User.GetUserId() });
        await _db.SaveChangesAsync();
        return RedirectToAction("Index", new { year = start.Year, month = start.Month });
    }
}

[Authorize]
public class CertificatesController : Controller
{
    private readonly AppDbContext _db;
    public CertificatesController(AppDbContext db) => _db = db;

    public async Task<IActionResult> Index()
    {
        var uid = User.GetUserId();
        var certs = await _db.Certificates
            .Include(c => c.Enrollment)!.ThenInclude(e => e!.Course)!.ThenInclude(c => c!.Instructor)
            .Where(c => c.Enrollment!.StudentId == uid)
            .OrderByDescending(c => c.IssuedAt).ToListAsync();
        return View(certs);
    }

    public async Task<IActionResult> Show(int id)
    {
        var cert = await _db.Certificates
            .Include(c => c.Enrollment)!.ThenInclude(e => e!.Course)!.ThenInclude(c => c!.Instructor)
            .Include(c => c.Enrollment)!.ThenInclude(e => e!.Course)!.ThenInclude(c => c!.Organisation)!.ThenInclude(o => o!.CertificateSignatory)
            .Include(c => c.Enrollment!.Student)
            .FirstOrDefaultAsync(c => c.Id == id);
        if (cert == null) return NotFound();
        var uid = User.GetUserId();
        var isOwner = cert.Enrollment!.StudentId == uid;
        var isInstructor = cert.Enrollment.Course!.InstructorId == uid;
        // Staff may view only certificates issued within their OWN organisation —
        // Admin/Principal is not a licence to read every tenant's certificates.
        var certOrg = cert.Enrollment.Course!.OrganisationId;
        int? viewerOrg = int.TryParse(User.FindFirst(TenantClaims.OrganisationId)?.Value, out var vo) ? vo : null;
        var isSameOrgStaff = (User.IsInRole("Admin") || User.IsInRole("Principal")) && certOrg != null && viewerOrg == certOrg;
        if (!isOwner && !isInstructor && !isSameOrgStaff && !User.IsInRole("SuperUser"))
            return Forbid();
        return View(cert);
    }

    /// <summary>
    /// Public certificate verification — opened by scanning the QR code on the
    /// certificate. The serial number acts as the access key; no login required.
    /// </summary>
    [AllowAnonymous]
    public async Task<IActionResult> Verify(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return NotFound();
        var cert = await _db.Certificates
            .Include(c => c.Enrollment)!.ThenInclude(e => e!.Course)!.ThenInclude(c => c!.Instructor)
            .Include(c => c.Enrollment)!.ThenInclude(e => e!.Course)!.ThenInclude(c => c!.Organisation)!.ThenInclude(o => o!.CertificateSignatory)
            .Include(c => c.Enrollment!.Student)
            .FirstOrDefaultAsync(c => c.SerialNumber == id);
        if (cert == null) return NotFound();
        ViewBag.IsPublicVerify = true;
        return View("Show", cert);
    }
}

[Authorize(Roles = "Admin,Principal,Instructor")]
public class ReportsController : Controller
{
    private readonly AppDbContext _db;
    public ReportsController(AppDbContext db) => _db = db;

    public async Task<IActionResult> Index()
    {
        var uid = User.GetUserId();
        var isAdmin = User.IsInRole("Admin") || User.IsInRole("Principal");
        var courses = await (isAdmin ? _db.Courses : _db.Courses.Where(c => c.InstructorId == uid))
            .AsNoTracking()
            .Include(c => c.Enrollments)
            .Include(c => c.Modules).ThenInclude(m => m.Lessons)
            .Include(c => c.Quizzes).ThenInclude(q => q.Attempts)
            .Include(c => c.Assignments).ThenInclude(a => a.Submissions)
            .ToListAsync();

        // One query for all lesson progress instead of one per course (avoids N+1)
        var allProgress = await _db.LessonProgress.AsNoTracking()
            .Select(p => new { p.LessonId, p.StudentId }).ToListAsync();
        var progressByLesson = allProgress.ToLookup(p => p.LessonId, p => p.StudentId);

        var report = new List<CourseReportRow>();
        foreach (var c in courses)
        {
            var lessonIds = c.Modules.SelectMany(m => m.Lessons.Select(l => l.Id)).ToList();
            var enrolledIds = c.Enrollments.Select(e => e.StudentId).ToHashSet();
            var progressCount = lessonIds.Count == 0 || enrolledIds.Count == 0 ? 0 :
                lessonIds.Sum(id => progressByLesson[id].Count(sid => enrolledIds.Contains(sid)));
            var attempts = c.Quizzes.SelectMany(q => q.Attempts).Where(a => a.SubmittedAt != null).ToList();

            report.Add(new CourseReportRow
            {
                Course = c,
                Enrolled = c.Enrollments.Count,
                Completed = c.Enrollments.Count(e => e.Status == EnrollmentStatus.Completed),
                AvgProgress = lessonIds.Count == 0 || enrolledIds.Count == 0 ? 0 :
                    (double)progressCount / (lessonIds.Count * enrolledIds.Count) * 100,
                AvgQuizScore = attempts.Count == 0 ? 0 :
                    attempts.Average(a => a.MaxScore > 0 ? a.Score / a.MaxScore * 100 : 0),
                SubmissionsGraded = c.Assignments.SelectMany(a => a.Submissions).Count(s => s.Grade != null),
                SubmissionsPending = c.Assignments.SelectMany(a => a.Submissions).Count(s => s.Grade == null)
            });
        }
        return View(report);
    }
}

public class CourseReportRow
{
    public Course Course { get; set; } = null!;
    public int Enrolled { get; set; }
    public int Completed { get; set; }
    public double AvgProgress { get; set; }
    public double AvgQuizScore { get; set; }
    public int SubmissionsGraded { get; set; }
    public int SubmissionsPending { get; set; }
}

using LMS.Web.Data;
using LMS.Web.Models;
using LMS.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LMS.Web.Controllers;

[Authorize]
public class FeedbackController : Controller
{
    private readonly AppDbContext _db;
    public FeedbackController(AppDbContext db) => _db = db;

    public async Task<IActionResult> Index()
    {
        var uid = User.GetUserId();
        if (User.IsInRole("Student"))
        {
            ViewBag.Enrollments = await _db.Enrollments
                .Include(e => e.Course)
                .Where(e => e.StudentId == uid && e.Status != EnrollmentStatus.Dropped)
                .ToListAsync();
            var mine = await _db.CourseFeedbacks
                .Include(f => f.Course)
                .Where(f => f.StudentId == uid)
                .OrderByDescending(f => f.SubmittedAt).ToListAsync();
            return View("FeedbackStudent", mine);
        }

        var feedback = _db.CourseFeedbacks
            .Include(f => f.Course).Include(f => f.Student)
            .AsQueryable();
        if (User.IsInRole("Instructor"))
            feedback = feedback.Where(f => f.Course!.InstructorId == uid);
        var list = await feedback.OrderByDescending(f => f.SubmittedAt).ToListAsync();
        ViewBag.Summary = list.GroupBy(f => f.Course!)
            .Select(g => new FeedbackSummaryRow { Course = g.Key, Avg = g.Average(x => x.Rating), Count = g.Count() })
            .OrderByDescending(x => x.Avg)
            .ToList();
        return View("FeedbackStaff", list);
    }

    [Authorize(Roles = "Student")]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Submit(int courseId, int rating, string? comments)
    {
        var uid = User.GetUserId();
        var enrolled = await _db.Enrollments.AnyAsync(e => e.CourseId == courseId && e.StudentId == uid && e.Status != EnrollmentStatus.Dropped);
        if (!enrolled) return Forbid();

        var existing = await _db.CourseFeedbacks.FirstOrDefaultAsync(f => f.CourseId == courseId && f.StudentId == uid);
        if (existing != null)
        {
            existing.Rating = Math.Clamp(rating, 1, 5);
            existing.Comments = comments;
            existing.SubmittedAt = DateTime.UtcNow;
        }
        else
        {
            _db.CourseFeedbacks.Add(new CourseFeedback
            {
                CourseId = courseId, StudentId = uid,
                Rating = Math.Clamp(rating, 1, 5), Comments = comments
            });
        }
        await _db.SaveChangesAsync();
        TempData["Ok"] = "Thank you — feedback recorded.";
        return RedirectToAction("Index");
    }
}

[Authorize]
public class SupportController : Controller
{
    private readonly AppDbContext _db;
    public SupportController(AppDbContext db) => _db = db;

    public async Task<IActionResult> Index(TicketCategory? category)
    {
        var uid = User.GetUserId();
        var isAdmin = User.IsInRole("Admin");
        var tickets = _db.SupportTickets.Include(t => t.RaisedBy).AsQueryable();
        if (!isAdmin) tickets = tickets.Where(t => t.RaisedById == uid);
        if (category != null) tickets = tickets.Where(t => t.Category == category);
        ViewBag.IsAdmin = isAdmin;
        ViewBag.Category = category;
        return View(await tickets.OrderBy(t => t.Status).ThenByDescending(t => t.CreatedAt).ToListAsync());
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(TicketCategory category, string subject, string body)
    {
        _db.SupportTickets.Add(new SupportTicket
        {
            Category = category, Subject = subject, Body = body, RaisedById = User.GetUserId()
        });
        await _db.SaveChangesAsync();
        TempData["Ok"] = "Your request has been raised. The team will respond here.";
        return RedirectToAction("Index");
    }

    [Authorize(Roles = "Admin")]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Respond(int id, string response, bool close)
    {
        var ticket = await _db.SupportTickets.FindAsync(id);
        if (ticket == null) return NotFound();
        ticket.Response = response;
        ticket.RespondedAt = DateTime.UtcNow;
        ticket.Status = close ? TicketStatus.Closed : TicketStatus.Answered;
        Notifier.Notify(_db, ticket.RaisedById, $"Your support request \"{ticket.Subject}\" has a response.", "/Support");
        await _db.SaveChangesAsync();
        return RedirectToAction("Index");
    }
}

[Authorize]
public class ProgressController : Controller
{
    private readonly AppDbContext _db;
    public ProgressController(AppDbContext db) => _db = db;

    // Individual Progress Report
    public async Task<IActionResult> Individual(string? studentId)
    {
        var uid = User.GetUserId();
        var isStaff = User.IsInRole("Admin") || User.IsInRole("Principal") || User.IsInRole("Instructor");
        var targetId = isStaff && !string.IsNullOrEmpty(studentId) ? studentId : uid;
        if (!isStaff) targetId = uid;

        var student = await _db.Users.FindAsync(targetId);
        if (student == null) return NotFound();

        var enrollments = await _db.Enrollments
            .Include(e => e.Course)!.ThenInclude(c => c!.Modules).ThenInclude(m => m.Lessons)
            .Include(e => e.Course!.Quizzes).ThenInclude(q => q.Attempts)
            .Include(e => e.Course!.Assignments).ThenInclude(a => a.Submissions)
            .Where(e => e.StudentId == targetId)
            .ToListAsync();

        var lessonIds = enrollments.SelectMany(e => e.Course!.Modules.SelectMany(m => m.Lessons.Select(l => l.Id))).ToList();
        var completedLessons = await _db.LessonProgress
            .Where(p => p.StudentId == targetId && lessonIds.Contains(p.LessonId))
            .Select(p => p.LessonId).ToListAsync();
        var attendance = await _db.AttendanceRecords
            .Where(a => a.StudentId == targetId).ToListAsync();

        ViewBag.Student = student;
        ViewBag.CompletedLessonIds = completedLessons;
        ViewBag.AttendancePresent = attendance.Count(a => a.Status == AttendanceStatus.Present || a.Status == AttendanceStatus.Late);
        ViewBag.AttendanceTotal = attendance.Count;
        ViewBag.Certificates = await _db.Certificates
            .Include(c => c.Enrollment)
            .CountAsync(c => c.Enrollment!.StudentId == targetId);

        if (isStaff)
        {
            IQueryable<Enrollment> scope = _db.Enrollments.Include(e => e.Student);
            if (User.IsInRole("Instructor"))
                scope = scope.Where(e => e.Course!.InstructorId == uid);
            ViewBag.Students = await scope.Select(e => e.Student!).Distinct().OrderBy(s => s.FullName).ToListAsync();
        }
        ViewBag.TargetId = targetId;
        return View(enrollments);
    }

    // Departmental Performance Metrics
    [Authorize(Roles = "Admin,Principal,Instructor")]
    public async Task<IActionResult> Departments()
    {
        var students = await _db.Users.ToListAsync();
        var enrollments = await _db.Enrollments.Include(e => e.Student).ToListAsync();
        var attempts = await _db.QuizAttempts.Include(a => a.Student)
            .Where(a => a.SubmittedAt != null).ToListAsync();
        var certs = await _db.Certificates.Include(c => c.Enrollment)!.ThenInclude(e => e!.Student).ToListAsync();

        var rows = enrollments
            .GroupBy(e => e.Student?.Department ?? "General")
            .Select(g => new DepartmentMetricsRow
            {
                Department = g.Key,
                Staff = g.Select(e => e.StudentId).Distinct().Count(),
                Enrollments = g.Count(),
                Completed = g.Count(e => e.Status == EnrollmentStatus.Completed),
                AvgQuizScore = attempts.Where(a => (a.Student?.Department ?? "General") == g.Key)
                    .Select(a => a.MaxScore > 0 ? a.Score / a.MaxScore * 100 : 0)
                    .DefaultIfEmpty(0).Average(),
                Certificates = certs.Count(c => (c.Enrollment?.Student?.Department ?? "General") == g.Key)
            })
            .OrderByDescending(r => r.Enrollments)
            .ToList();
        return View(rows);
    }
}

public class FeedbackSummaryRow
{
    public Course Course { get; set; } = null!;
    public double Avg { get; set; }
    public int Count { get; set; }
}

public class DepartmentMetricsRow
{
    public string Department { get; set; } = "";
    public int Staff { get; set; }
    public int Enrollments { get; set; }
    public int Completed { get; set; }
    public double AvgQuizScore { get; set; }
    public int Certificates { get; set; }
}

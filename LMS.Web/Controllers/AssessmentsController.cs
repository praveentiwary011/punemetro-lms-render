using LMS.Web.Data;
using LMS.Web.Models;
using LMS.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LMS.Web.Controllers;

[Authorize]
public class AssessmentsController : Controller
{
    private readonly AppDbContext _db;
    public AssessmentsController(AppDbContext db) => _db = db;

    // Self Assessments — practice quizzes, unlimited attempts
    [Authorize(Roles = "Student")]
    public async Task<IActionResult> SelfAssessments()
    {
        var uid = User.GetUserId();
        var courseIds = await _db.Enrollments
            .Where(e => e.StudentId == uid && e.Status != EnrollmentStatus.Dropped)
            .Select(e => e.CourseId).ToListAsync();
        var quizzes = await _db.Quizzes
            .Include(q => q.Course).Include(q => q.Questions)
            .Where(q => q.IsPublished && q.IsSelfAssessment && courseIds.Contains(q.CourseId))
            .ToListAsync();
        ViewBag.MyAttempts = await _db.QuizAttempts
            .Where(a => a.StudentId == uid && a.SubmittedAt != null)
            .GroupBy(a => a.QuizId)
            .Select(g => new { QuizId = g.Key, Count = g.Count(), Best = g.Max(x => x.MaxScore > 0 ? x.Score / x.MaxScore * 100 : 0) })
            .ToDictionaryAsync(x => x.QuizId, x => new double[] { x.Count, x.Best });
        return View(quizzes);
    }

    // Scheduled Assessments — graded quizzes with deadlines
    public async Task<IActionResult> Scheduled()
    {
        var uid = User.GetUserId();
        IQueryable<Quiz> query = _db.Quizzes.Include(q => q.Course).Include(q => q.Questions)
            .Where(q => q.IsPublished && !q.IsSelfAssessment);

        if (User.IsInRole("Student"))
        {
            var courseIds = await _db.Enrollments
                .Where(e => e.StudentId == uid && e.Status != EnrollmentStatus.Dropped)
                .Select(e => e.CourseId).ToListAsync();
            query = query.Where(q => courseIds.Contains(q.CourseId));
        }
        else if (User.IsInRole("Instructor"))
        {
            query = query.Where(q => q.Course!.InstructorId == uid);
        }
        // Admin & Principal see all

        var quizzes = await query.OrderBy(q => q.DueDate == null).ThenBy(q => q.DueDate).ToListAsync();

        ViewBag.MyAttempts = User.IsInRole("Student")
            ? await _db.QuizAttempts
                .Where(a => a.StudentId == uid && a.SubmittedAt != null)
                .GroupBy(a => a.QuizId)
                .Select(g => new { QuizId = g.Key, Count = g.Count(), Best = g.Max(x => x.MaxScore > 0 ? x.Score / x.MaxScore * 100 : 0) })
                .ToDictionaryAsync(x => x.QuizId, x => new double[] { x.Count, x.Best })
            : new Dictionary<int, double[]>();
        return View(quizzes);
    }

    // Certification Tracker
    public async Task<IActionResult> CertificationTracker()
    {
        var uid = User.GetUserId();
        if (User.IsInRole("Student"))
        {
            var enrollments = await _db.Enrollments
                .Include(e => e.Course)
                .Where(e => e.StudentId == uid && e.Status != EnrollmentStatus.Dropped)
                .ToListAsync();
            ViewBag.Certificates = await _db.Certificates
                .Include(c => c.Enrollment)!.ThenInclude(e => e!.Course)
                .Where(c => c.Enrollment!.StudentId == uid)
                .ToListAsync();
            return View("CertificationTrackerStudent", enrollments);
        }

        // Trainer sees own courses; Admin/Principal see all
        var certs = _db.Certificates
            .Include(c => c.Enrollment)!.ThenInclude(e => e!.Course)
            .Include(c => c.Enrollment!.Student)
            .AsQueryable();
        if (User.IsInRole("Instructor"))
            certs = certs.Where(c => c.Enrollment!.Course!.InstructorId == uid);
        return View("CertificationTrackerStaff", await certs.OrderByDescending(c => c.IssuedAt).ToListAsync());
    }

    // ---------- Retake requests ----------
    public async Task<IActionResult> Retakes()
    {
        var uid = User.GetUserId();
        if (User.IsInRole("Student"))
        {
            var myRequests = await _db.RetakeRequests
                .Include(r => r.Quiz)!.ThenInclude(q => q!.Course)
                .Where(r => r.StudentId == uid)
                .OrderByDescending(r => r.RequestedAt).ToListAsync();

            // quizzes where the student has exhausted attempts
            var courseIds = await _db.Enrollments
                .Where(e => e.StudentId == uid && e.Status != EnrollmentStatus.Dropped)
                .Select(e => e.CourseId).ToListAsync();
            var quizzes = await _db.Quizzes.Include(q => q.Course)
                .Where(q => q.IsPublished && !q.IsSelfAssessment && courseIds.Contains(q.CourseId))
                .ToListAsync();
            var attemptCounts = await _db.QuizAttempts
                .Where(a => a.StudentId == uid && a.SubmittedAt != null)
                .GroupBy(a => a.QuizId)
                .Select(g => new { QuizId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.QuizId, x => x.Count);
            var approvedExtra = myRequests.Where(r => r.Status == RetakeStatus.Approved)
                .GroupBy(r => r.QuizId).ToDictionary(g => g.Key, g => g.Count());

            ViewBag.Exhausted = quizzes.Where(q =>
                attemptCounts.TryGetValue(q.Id, out var c) &&
                c >= q.MaxAttempts + (approvedExtra.TryGetValue(q.Id, out var e) ? e : 0) &&
                !myRequests.Any(r => r.QuizId == q.Id && r.Status == RetakeStatus.Pending)).ToList();
            return View("RetakesStudent", myRequests);
        }

        var requests = _db.RetakeRequests
            .Include(r => r.Quiz)!.ThenInclude(q => q!.Course)
            .Include(r => r.Student)
            .AsQueryable();
        if (User.IsInRole("Instructor"))
            requests = requests.Where(r => r.Quiz!.Course!.InstructorId == User.GetUserId());
        return View("RetakesStaff", await requests.OrderBy(r => r.Status).ThenByDescending(r => r.RequestedAt).ToListAsync());
    }

    [Authorize(Roles = "Student")]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> RequestRetake(int quizId, string reason)
    {
        var uid = User.GetUserId();
        var quiz = await _db.Quizzes.Include(q => q.Course).FirstOrDefaultAsync(q => q.Id == quizId);
        if (quiz == null) return NotFound();
        if (await _db.RetakeRequests.AnyAsync(r => r.QuizId == quizId && r.StudentId == uid && r.Status == RetakeStatus.Pending))
        {
            TempData["Err"] = "You already have a pending request for this quiz.";
            return RedirectToAction("Retakes");
        }
        _db.RetakeRequests.Add(new RetakeRequest { QuizId = quizId, StudentId = uid, Reason = reason });
        Notifier.Notify(_db, quiz.Course!.InstructorId, $"Retake requested for \"{quiz.Title}\".", "/Assessments/Retakes");
        await _db.SaveChangesAsync();
        TempData["Ok"] = "Retake request submitted.";
        return RedirectToAction("Retakes");
    }

    [Authorize(Roles = "Admin,Instructor")]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DecideRetake(int id, bool approve, string? note)
    {
        var request = await _db.RetakeRequests
            .Include(r => r.Quiz)!.ThenInclude(q => q!.Course)
            .FirstOrDefaultAsync(r => r.Id == id);
        if (request == null) return NotFound();
        if (User.IsInRole("Instructor") && !User.IsInRole("Admin") &&
            request.Quiz!.Course!.InstructorId != User.GetUserId()) return Forbid();

        request.Status = approve ? RetakeStatus.Approved : RetakeStatus.Rejected;
        request.DecisionNote = note;
        request.DecidedAt = DateTime.UtcNow;
        Notifier.Notify(_db, request.StudentId,
            $"Your retake request for \"{request.Quiz!.Title}\" was {(approve ? "approved — you have one extra attempt" : "rejected")}.",
            "/Assessments/Retakes");
        await _db.SaveChangesAsync();
        TempData["Ok"] = approve ? "Retake approved (one extra attempt granted)." : "Retake rejected.";
        return RedirectToAction("Retakes");
    }
}

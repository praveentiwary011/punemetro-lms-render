using LMS.Web.Data;
using LMS.Web.Models;
using LMS.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LMS.Web.Controllers;

[Authorize]
public class CoursesController : Controller
{
    private readonly AppDbContext _db;
    public CoursesController(AppDbContext db) => _db = db;

    /// <summary>Multi-tenancy: signed-in non-admin users see their own organisation's
    /// courses plus shared (no-organisation) ones; Admins and anonymous visitors see all.</summary>
    private async Task<IQueryable<Course>> ScopeToUserOrganisationAsync(IQueryable<Course> courses)
    {
        if (User.Identity?.IsAuthenticated != true || User.IsInRole("Admin")) return courses;
        var uid = User.GetUserId();
        var orgId = await _db.Users.Where(u => u.Id == uid).Select(u => u.OrganisationId).FirstOrDefaultAsync();
        return orgId == null ? courses : courses.Where(c => c.OrganisationId == null || c.OrganisationId == orgId);
    }

    /// <summary>Staff who may open a course while it is unpublished: its own trainer,
    /// plus Principal/Admin, who oversee every course. Lets them check a revision before
    /// republishing it (§CRS-06).</summary>
    private async Task<bool> CanPreviewDraftAsync(Course course) =>
        await CourseAccess.CanEditAsync(_db, User, course);

    /// <summary>An unpublished course is a DRAFT: it is being revised, so learners cannot
    /// open it or sit its assessments until it is republished. To take a course off the
    /// catalogue while enrolled learners carry on working through it, deactivate it
    /// instead (§CRS-07) — that is the distinction between the two controls.</summary>
    private const string DraftMessage =
        "This course is being updated and is temporarily unavailable. Please check back shortly.";

    [AllowAnonymous]
    public async Task<IActionResult> Catalog(string? q, int? categoryId)
    {
        var courses = await ScopeToUserOrganisationAsync(_db.Courses.AsNoTracking()
            .Include(c => c.Instructor).Include(c => c.Category).Include(c => c.Enrollments)
            .Where(c => c.IsPublished && c.IsActive));
        if (!string.IsNullOrWhiteSpace(q))
        {
            // Partial, case-insensitive match on title, description, code, and instructor name
            var term = q.Trim().ToLower();
            courses = courses.Where(c =>
                c.Title.ToLower().Contains(term) ||
                c.Description.ToLower().Contains(term) ||
                c.Code.ToLower().Contains(term) ||
                (c.Instructor != null && c.Instructor.FullName.ToLower().Contains(term)));
        }
        if (categoryId != null)
            courses = courses.Where(c => c.CategoryId == categoryId);

        ViewBag.Categories = await _db.Categories.OrderBy(c => c.Name).ToListAsync();
        ViewBag.Query = q;
        ViewBag.CategoryId = categoryId;

        var uid = User.Identity?.IsAuthenticated == true ? User.GetUserId() : null;
        ViewBag.MyEnrollments = uid == null
            ? new List<int>()
            : await _db.Enrollments.Where(e => e.StudentId == uid).Select(e => e.CourseId).ToListAsync();

        return View(await courses.OrderBy(c => c.Title).ToListAsync());
    }

    public async Task<IActionResult> Details(int id)
    {
        var course = await _db.Courses
            .Include(c => c.Instructor).Include(c => c.Category)
            .Include(c => c.Modules.OrderBy(m => m.Order)).ThenInclude(m => m.Lessons.OrderBy(l => l.Order))
            .Include(c => c.Assignments)
            .Include(c => c.Quizzes.Where(q => q.IsPublished))
            .Include(c => c.Announcements.OrderByDescending(a => a.CreatedAt)).ThenInclude(a => a.Author)
            .Include(c => c.Enrollments)
            .FirstOrDefaultAsync(c => c.Id == id);
        if (course == null) return NotFound();
        if (!course.IsPublished && !await CanPreviewDraftAsync(course))
        {
            TempData["Err"] = DraftMessage;
            return RedirectToAction("MyCourses");
        }

        var uid = User.GetUserId();
        var enrollment = course.Enrollments.FirstOrDefault(e => e.StudentId == uid);
        ViewBag.Enrollment = enrollment;
        ViewBag.IsDraftPreview = !course.IsPublished;

        if (enrollment != null)
        {
            var lessonIds = course.Modules.SelectMany(m => m.Lessons.Select(l => l.Id)).ToList();
            ViewBag.CompletedLessonIds = await _db.LessonProgress
                .Where(p => p.StudentId == uid && lessonIds.Contains(p.LessonId))
                .Select(p => p.LessonId).ToListAsync();
        }
        else ViewBag.CompletedLessonIds = new List<int>();

        return View(course);
    }

    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Roles = "Student")]
    public async Task<IActionResult> Enroll(int id)
    {
        var course = await _db.Courses.Include(c => c.Enrollments).FirstOrDefaultAsync(c => c.Id == id && c.IsPublished);
        if (course == null) return NotFound();
        if (!course.IsActive)
        {
            TempData["Err"] = "This course is currently deactivated and closed to new enrollments.";
            return RedirectToAction("Details", new { id });
        }
        var uid = User.GetUserId();
        if (course.Enrollments.Any(e => e.StudentId == uid))
        {
            TempData["Err"] = "You are already enrolled.";
        }
        else if (course.Enrollments.Count >= course.MaxEnrollment)
        {
            TempData["Err"] = "This course is full.";
        }
        else
        {
            _db.Enrollments.Add(new Enrollment { CourseId = id, StudentId = uid });
            Notifier.Notify(_db, course.InstructorId, $"New enrollment in {course.Title}.", $"/Instructor/ManageCourse/{id}");
            Notifier.Audit(_db, uid, User.Identity!.Name ?? "", "Enroll", course.Title);
            await _db.SaveChangesAsync();
            TempData["Ok"] = $"Enrolled in {course.Title}!";
        }
        return RedirectToAction("Details", new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Roles = "Student")]
    public async Task<IActionResult> Drop(int id)
    {
        var uid = User.GetUserId();
        var enrollment = await _db.Enrollments.FirstOrDefaultAsync(e => e.CourseId == id && e.StudentId == uid);
        if (enrollment != null)
        {
            enrollment.Status = EnrollmentStatus.Dropped;
            await _db.SaveChangesAsync();
            TempData["Ok"] = "You have dropped the course.";
        }
        return RedirectToAction("Catalog");
    }

    /// <summary>Deactivate / reactivate a published course. Trainers may toggle
    /// their own courses; Principal and Admin may toggle any course.</summary>
    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Roles = "Instructor,Principal,Admin")]
    public async Task<IActionResult> ToggleActive(int id, string? returnUrl)
    {
        var course = await _db.Courses.FirstOrDefaultAsync(c => c.Id == id);
        if (course == null) return NotFound();
        var uid = User.GetUserId();
        var isPrivileged = User.IsInRole("Admin") || User.IsInRole("Principal");
        if (!isPrivileged && course.InstructorId != uid) return Forbid();

        course.IsActive = !course.IsActive;
        Notifier.Audit(_db, uid, User.Identity!.Name ?? "",
            course.IsActive ? "ReactivateCourse" : "DeactivateCourse", course.Title);
        if (!course.IsActive && course.InstructorId != uid)
            Notifier.Notify(_db, course.InstructorId, $"Your course \"{course.Title}\" was deactivated.", $"/Instructor/ManageCourse/{id}");
        await _db.SaveChangesAsync();
        TempData["Ok"] = course.IsActive
            ? $"{course.Code} reactivated — visible in the catalog and open to enrollment."
            : $"{course.Code} deactivated — hidden from the catalog; enrolled learners keep access.";

        if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl)) return Redirect(returnUrl);
        return RedirectToAction("Details", new { id });
    }

    // Lesson player
    public async Task<IActionResult> Learn(int id, int? lessonId)
    {
        var course = await _db.Courses
            .Include(c => c.Modules.OrderBy(m => m.Order)).ThenInclude(m => m.Lessons.OrderBy(l => l.Order))
            .FirstOrDefaultAsync(c => c.Id == id);
        if (course == null) return NotFound();

        var uid = User.GetUserId();
        var isOwner = course.InstructorId == uid || User.IsInRole("Admin") || User.IsInRole("Principal");
        var enrolled = await _db.Enrollments.AnyAsync(e => e.CourseId == id && e.StudentId == uid && e.Status != EnrollmentStatus.Dropped);
        if (!isOwner && !enrolled) return RedirectToAction("Details", new { id });
        if (!course.IsPublished && !isOwner)
        {
            TempData["Err"] = DraftMessage;
            return RedirectToAction("MyCourses");
        }

        var lessons = course.Modules.SelectMany(m => m.Lessons).ToList();
        if (lessons.Count == 0) return RedirectToAction("Details", new { id });

        var lesson = lessonId != null ? lessons.FirstOrDefault(l => l.Id == lessonId) : lessons.First();
        if (lesson == null) return NotFound();

        var lessonIds = lessons.Select(l => l.Id).ToList();
        ViewBag.CompletedLessonIds = await _db.LessonProgress
            .Where(p => p.StudentId == uid && lessonIds.Contains(p.LessonId))
            .Select(p => p.LessonId).ToListAsync();
        ViewBag.Lesson = lesson;
        return View(course);
    }

    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Roles = "Student")]
    public async Task<IActionResult> CompleteLesson(int courseId, int lessonId)
    {
        var uid = User.GetUserId();
        if (!await _db.LessonProgress.AnyAsync(p => p.LessonId == lessonId && p.StudentId == uid))
        {
            _db.LessonProgress.Add(new LessonProgress { LessonId = lessonId, StudentId = uid });
            var user = await _db.Users.FindAsync(uid);
            if (user != null)
            {
                user.Points += 10;
                var lessonInfo = await _db.Lessons.FindAsync(lessonId);
                Xapi.Emit(_db, user, Xapi.VerbCompleted, "completed",
                    $"https://lms.punemetro.in/lessons/{lessonId}", lessonInfo?.Title ?? $"Lesson {lessonId}");
            }
            await _db.SaveChangesAsync();
        }

        // Completing the last lesson completes the course (progress bars read 100%).
        if (await CourseCompletion.CheckAsync(_db, courseId, uid))
            await _db.SaveChangesAsync();

        // Find next lesson
        var course = await _db.Courses
            .Include(c => c.Modules.OrderBy(m => m.Order)).ThenInclude(m => m.Lessons.OrderBy(l => l.Order))
            .Include(c => c.Quizzes)
            .Include(c => c.Assignments)
            .FirstOrDefaultAsync(c => c.Id == courseId);
        if (course != null)
        {
            var ordered = course.Modules.SelectMany(m => m.Lessons).ToList();
            var idx = ordered.FindIndex(l => l.Id == lessonId);
            if (idx >= 0 && idx < ordered.Count - 1)
                return RedirectToAction("Learn", new { id = courseId, lessonId = ordered[idx + 1].Id });

            // Last lesson done. If the course requires an assessment that is still
            // outstanding, open the assessment section so the trainee can take it.
            if (course.RequiresAssessment)
            {
                var lessonIds = ordered.Select(l => l.Id).ToList();
                var doneCount = await _db.LessonProgress.CountAsync(p => p.StudentId == uid && lessonIds.Contains(p.LessonId));
                var stillActive = await _db.Enrollments.AnyAsync(e => e.CourseId == courseId && e.StudentId == uid && e.Status == EnrollmentStatus.Active);
                if (stillActive && lessonIds.Count > 0 && doneCount >= lessonIds.Count)
                {
                    var quiz = course.Quizzes.FirstOrDefault(q => q.IsPublished && !q.IsSelfAssessment);
                    if (quiz != null)
                    {
                        TempData["Ok"] = "You've finished all the lessons — take the assessment to complete the course.";
                        return RedirectToAction("Take", "Quizzes", new { id = quiz.Id });
                    }
                    var assignment = course.Assignments.FirstOrDefault();
                    if (assignment != null)
                    {
                        TempData["Ok"] = "You've finished all the lessons — complete the assignment to finish the course.";
                        return RedirectToAction("Details", "Assignments", new { id = assignment.Id });
                    }
                }
            }
        }
        return RedirectToAction("Learn", new { id = courseId, lessonId });
    }

    // Ongoing / Completed courses
    [Authorize(Roles = "Student")]
    public async Task<IActionResult> MyCourses(string status = "ongoing")
    {
        var uid = User.GetUserId();
        var target = status == "completed" ? EnrollmentStatus.Completed : EnrollmentStatus.Active;
        var enrollments = await _db.Enrollments
            .Include(e => e.Course)!.ThenInclude(c => c!.Modules).ThenInclude(m => m.Lessons)
            .Include(e => e.Course!.Instructor)
            .Include(e => e.Course!.Quizzes)
            .Where(e => e.StudentId == uid && e.Status == target)
            .ToListAsync();
        var lessonIds = enrollments.SelectMany(e => e.Course!.Modules.SelectMany(m => m.Lessons.Select(l => l.Id))).ToList();
        ViewBag.CompletedLessonIds = await _db.LessonProgress
            .Where(p => p.StudentId == uid && lessonIds.Contains(p.LessonId))
            .Select(p => p.LessonId).ToListAsync();
        ViewBag.Status = status;
        return View(enrollments);
    }

    // Recommended Training — published courses (not yet enrolled), same categories first
    [Authorize(Roles = "Student")]
    public async Task<IActionResult> Recommended()
    {
        var uid = User.GetUserId();
        var enrolledIds = await _db.Enrollments.Where(e => e.StudentId == uid).Select(e => e.CourseId).ToListAsync();
        var myCategoryIds = await _db.Enrollments
            .Where(e => e.StudentId == uid && e.Course!.CategoryId != null)
            .Select(e => e.Course!.CategoryId!.Value).Distinct().ToListAsync();
        var courses = await (await ScopeToUserOrganisationAsync(_db.Courses
            .Include(c => c.Instructor).Include(c => c.Category).Include(c => c.Enrollments)
            .Where(c => c.IsPublished && c.IsActive && !enrolledIds.Contains(c.Id))))
            .ToListAsync();
        return View(courses
            .OrderByDescending(c => c.Kind == CourseKind.Compliance)
            .ThenByDescending(c => c.CategoryId != null && myCategoryIds.Contains(c.CategoryId.Value))
            .ToList());
    }

    // Learning Path — courses filtered by kind (Compliance / RoleSpecific / Leadership / Onboarding)
    public async Task<IActionResult> Path(CourseKind kind)
    {
        var courses = await (await ScopeToUserOrganisationAsync(_db.Courses
            .Include(c => c.Instructor).Include(c => c.Category).Include(c => c.Enrollments)
            .Where(c => c.IsPublished && c.IsActive && c.Kind == kind)))
            .OrderBy(c => c.Title).ToListAsync();
        var uid = User.GetUserId();
        ViewBag.MyEnrollments = await _db.Enrollments.Where(e => e.StudentId == uid).Select(e => e.CourseId).ToListAsync();
        ViewBag.Kind = kind;
        return View(courses);
    }

    // Partner courses (external providers)
    public async Task<IActionResult> Partner()
    {
        return View(await _db.PartnerCourses.OrderBy(p => p.Title).ToListAsync());
    }

    // Student grades overview
    [Authorize(Roles = "Student")]
    public async Task<IActionResult> MyGrades()
    {
        var uid = User.GetUserId();
        var enrollments = await _db.Enrollments
            .Include(e => e.Course)!.ThenInclude(c => c!.Assignments).ThenInclude(a => a.Submissions)
            .Include(e => e.Course)!.ThenInclude(c => c!.Quizzes).ThenInclude(q => q.Attempts)
            .Where(e => e.StudentId == uid)
            .ToListAsync();
        return View(enrollments);
    }
}

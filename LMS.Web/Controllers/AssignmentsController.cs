using LMS.Web.Data;
using LMS.Web.Models;
using LMS.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LMS.Web.Controllers;

[Authorize]
public class AssignmentsController : Controller
{
    private readonly AppDbContext _db;
    private readonly IWebHostEnvironment _env;

    public AssignmentsController(AppDbContext db, IWebHostEnvironment env)
    {
        _db = db;
        _env = env;
    }

    public async Task<IActionResult> Details(int id)
    {
        var assignment = await _db.Assignments
            .Include(a => a.Course)
            .Include(a => a.Submissions).ThenInclude(s => s.Student)
            .FirstOrDefaultAsync(a => a.Id == id);
        if (assignment == null) return NotFound();

        var uid = User.GetUserId();
        ViewBag.IsOwner = assignment.Course!.InstructorId == uid || User.IsInRole("Admin");
        ViewBag.MySubmission = assignment.Submissions.FirstOrDefault(s => s.StudentId == uid);
        return View(assignment);
    }

    [Authorize(Roles = "Instructor,Admin,Principal")]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(int courseId, string title, string description, DateTime? dueDate, double maxPoints, bool allowLate)
    {
        var course = await _db.Courses.Include(c => c.Enrollments).FirstOrDefaultAsync(c => c.Id == courseId);
        if (course == null || (!User.IsInRole("Admin") && !User.IsInRole("Principal") && course.InstructorId != User.GetUserId())) return NotFound();
        var assignment = new Assignment
        {
            CourseId = courseId, Title = title, Description = HtmlSanitizer.Clean(description) ?? "",
            DueDate = dueDate, MaxPoints = maxPoints <= 0 ? 100 : maxPoints, AllowLateSubmission = allowLate
        };
        _db.Assignments.Add(assignment);
        if (dueDate != null)
            _db.CalendarEvents.Add(new CalendarEvent { CourseId = courseId, Title = $"{course.Code}: {title} due", Start = dueDate.Value, Type = EventType.Assignment });
        Notifier.NotifyCourse(_db, course.Enrollments.Select(e => e.StudentId), $"New assignment in {course.Title}: {title}");
        await _db.SaveChangesAsync();
        TempData["Ok"] = "Assignment created.";
        return RedirectToAction("ManageCourse", "Instructor", new { id = courseId });
    }

    [Authorize(Roles = "Instructor,Admin,Principal")]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var assignment = await _db.Assignments.Include(a => a.Course).FirstOrDefaultAsync(a => a.Id == id);
        if (assignment == null || (!User.IsInRole("Admin") && !User.IsInRole("Principal") && assignment.Course!.InstructorId != User.GetUserId())) return NotFound();
        _db.Assignments.Remove(assignment);
        await _db.SaveChangesAsync();
        return RedirectToAction("ManageCourse", "Instructor", new { id = assignment.CourseId });
    }

    [Authorize(Roles = "Student")]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Submit(int id, string? text, IFormFile? file)
    {
        var assignment = await _db.Assignments.Include(a => a.Course).FirstOrDefaultAsync(a => a.Id == id);
        if (assignment == null) return NotFound();
        var uid = User.GetUserId();

        var enrolled = await _db.Enrollments.AnyAsync(e => e.CourseId == assignment.CourseId && e.StudentId == uid && e.Status != EnrollmentStatus.Dropped);
        if (!enrolled) return Forbid();

        if (assignment.DueDate != null && DateTime.UtcNow > assignment.DueDate && !assignment.AllowLateSubmission)
        {
            TempData["Err"] = "The deadline has passed and late submissions are not allowed.";
            return RedirectToAction("Details", new { id });
        }

        // Uploaded images (jpeg/png/…) are converted to PDF automatically
        var fileUrl = await UploadHelper.SaveAsync(file, _env, "submissions");

        var existing = await _db.Submissions.FirstOrDefaultAsync(s => s.AssignmentId == id && s.StudentId == uid);
        if (existing != null)
        {
            existing.Text = text ?? existing.Text;
            existing.FileUrl = fileUrl ?? existing.FileUrl;
            existing.SubmittedAt = DateTime.UtcNow;
            existing.Grade = null; existing.Feedback = null; existing.GradedAt = null;
        }
        else
        {
            _db.Submissions.Add(new Submission { AssignmentId = id, StudentId = uid, Text = text, FileUrl = fileUrl });
        }
        Notifier.Notify(_db, assignment.Course!.InstructorId, $"New submission for {assignment.Title}.", $"/Assignments/Details/{id}");
        await _db.SaveChangesAsync();
        TempData["Ok"] = "Submission received.";
        return RedirectToAction("Details", new { id });
    }

    [Authorize(Roles = "Instructor,Admin,Principal")]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Grade(int submissionId, double grade, string? feedback)
    {
        var submission = await _db.Submissions
            .Include(s => s.Assignment)!.ThenInclude(a => a!.Course)
            .FirstOrDefaultAsync(s => s.Id == submissionId);
        if (submission == null || (!User.IsInRole("Admin") && !User.IsInRole("Principal") && submission.Assignment!.Course!.InstructorId != User.GetUserId())) return NotFound();
        submission.Grade = grade;
        submission.Feedback = feedback;
        submission.GradedAt = DateTime.UtcNow;
        Notifier.Notify(_db, submission.StudentId, $"Your submission for \"{submission.Assignment!.Title}\" was graded: {grade:0.#}/{submission.Assignment.MaxPoints:0.#}", $"/Assignments/Details/{submission.AssignmentId}");
        await _db.SaveChangesAsync();

        // Grading this submission may satisfy the last completion requirement for the course.
        if (await CourseCompletion.CheckAsync(_db, submission.Assignment.CourseId, submission.StudentId))
            await _db.SaveChangesAsync();

        TempData["Ok"] = "Grade saved.";
        return RedirectToAction("Details", new { id = submission.AssignmentId });
    }
}

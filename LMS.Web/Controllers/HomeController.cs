using LMS.Web.Data;
using LMS.Web.Models;
using LMS.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LMS.Web.Controllers;

public class HomeController : Controller
{
    private readonly AppDbContext _db;
    public HomeController(AppDbContext db) => _db = db;

    public async Task<IActionResult> Index()
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            return ActiveRole.Get(HttpContext) switch
            {
                "SuperUser" => RedirectToAction("Dashboard", "Admin"),
                "Admin" => RedirectToAction("Dashboard", "Admin"),
                "Principal" => RedirectToAction("Dashboard", "Principal"),
                "Instructor" => RedirectToAction("Dashboard", "Instructor"),
                _ => RedirectToAction("Dashboard")
            };
        }
        ViewBag.CourseCount = await _db.Courses.CountAsync(c => c.IsPublished);
        ViewBag.StudentCount = await _db.Enrollments.Select(e => e.StudentId).Distinct().CountAsync();
        return View();
    }

    [Authorize(Roles = "Student")]
    public async Task<IActionResult> Dashboard()
    {
        var uid = User.GetUserId();
        var enrollments = await _db.Enrollments
            .Include(e => e.Course)!.ThenInclude(c => c!.Modules).ThenInclude(m => m.Lessons)
            .Where(e => e.StudentId == uid)
            .ToListAsync();

        var lessonIds = enrollments.SelectMany(e => e.Course!.Modules.SelectMany(m => m.Lessons.Select(l => l.Id))).ToList();
        var completed = await _db.LessonProgress
            .Where(p => p.StudentId == uid && lessonIds.Contains(p.LessonId))
            .Select(p => p.LessonId).ToListAsync();

        var courseIds = enrollments.Select(e => e.CourseId).ToList();
        ViewBag.Enrollments = enrollments;
        ViewBag.CompletedLessonIds = completed;
        ViewBag.UpcomingAssignments = await _db.Assignments
            .Include(a => a.Course)
            .Where(a => courseIds.Contains(a.CourseId) && a.DueDate != null && a.DueDate > DateTime.UtcNow)
            .OrderBy(a => a.DueDate).Take(5).ToListAsync();
        ViewBag.RecentAnnouncements = await _db.Announcements
            .Include(a => a.Author).Include(a => a.Course)
            .Where(a => a.CourseId == null || courseIds.Contains(a.CourseId.Value))
            .OrderByDescending(a => a.CreatedAt).Take(5).ToListAsync();
        ViewBag.TopCourses = await _db.Courses
            .Where(c => c.IsPublished && c.IsActive)
            .Select(c => new PopularCourseRow
            {
                Id = c.Id, Code = c.Code, Title = c.Title,
                Instructor = c.Instructor!.FullName,
                Enrolled = c.Enrollments.Count
            })
            .OrderByDescending(x => x.Enrolled).Take(5)
            .ToListAsync();
        return View();
    }

    public IActionResult Error() => View();
}

using LMS.Web.Data;
using LMS.Web.Models;
using LMS.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LMS.Web.Controllers;

[Authorize]
public class DiscussionsController : Controller
{
    private readonly AppDbContext _db;
    public DiscussionsController(AppDbContext db) => _db = db;

    public async Task<IActionResult> Index(int courseId)
    {
        var course = await _db.Courses
            .Include(c => c.Threads.OrderByDescending(t => t.IsPinned).ThenByDescending(t => t.CreatedAt))
                .ThenInclude(t => t.Author)
            .Include(c => c.Threads).ThenInclude(t => t.Posts)
            .FirstOrDefaultAsync(c => c.Id == courseId);
        if (course == null) return NotFound();
        return View(course);
    }

    public async Task<IActionResult> Thread(int id)
    {
        var thread = await _db.DiscussionThreads
            .Include(t => t.Course)
            .Include(t => t.Author)
            .Include(t => t.Posts.OrderBy(p => p.CreatedAt)).ThenInclude(p => p.Author)
            .FirstOrDefaultAsync(t => t.Id == id);
        if (thread == null) return NotFound();
        ViewBag.IsModerator = User.IsInRole("Admin") || thread.Course!.InstructorId == User.GetUserId();
        return View(thread);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateThread(int courseId, string title, string body)
    {
        var course = await _db.Courses.FirstOrDefaultAsync(c => c.Id == courseId);
        if (course == null) return NotFound();
        var thread = new DiscussionThread { CourseId = courseId, Title = title, Body = body, AuthorId = User.GetUserId() };
        _db.DiscussionThreads.Add(thread);
        Notifier.Notify(_db, course.InstructorId, $"New discussion in {course.Title}: {title}");
        await _db.SaveChangesAsync();
        return RedirectToAction("Thread", new { id = thread.Id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Reply(int threadId, string body)
    {
        var thread = await _db.DiscussionThreads.Include(t => t.Course).FirstOrDefaultAsync(t => t.Id == threadId);
        if (thread == null) return NotFound();
        if (thread.IsLocked && !User.IsInRole("Admin") && thread.Course!.InstructorId != User.GetUserId())
        {
            TempData["Err"] = "This thread is locked.";
            return RedirectToAction("Thread", new { id = threadId });
        }
        _db.DiscussionPosts.Add(new DiscussionPost { ThreadId = threadId, Body = body, AuthorId = User.GetUserId() });
        if (thread.AuthorId != User.GetUserId())
            Notifier.Notify(_db, thread.AuthorId, $"New reply in \"{thread.Title}\"", $"/Discussions/Thread/{threadId}");
        await _db.SaveChangesAsync();
        return RedirectToAction("Thread", new { id = threadId });
    }

    [Authorize(Roles = "Instructor,Admin")]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> TogglePin(int id)
    {
        var thread = await _db.DiscussionThreads.FindAsync(id);
        if (thread == null) return NotFound();
        thread.IsPinned = !thread.IsPinned;
        await _db.SaveChangesAsync();
        return RedirectToAction("Thread", new { id });
    }

    [Authorize(Roles = "Instructor,Admin")]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleLock(int id)
    {
        var thread = await _db.DiscussionThreads.FindAsync(id);
        if (thread == null) return NotFound();
        thread.IsLocked = !thread.IsLocked;
        await _db.SaveChangesAsync();
        return RedirectToAction("Thread", new { id });
    }

    [Authorize(Roles = "Instructor,Admin")]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteThread(int id)
    {
        var thread = await _db.DiscussionThreads.FindAsync(id);
        if (thread == null) return NotFound();
        var courseId = thread.CourseId;
        _db.DiscussionThreads.Remove(thread);
        await _db.SaveChangesAsync();
        return RedirectToAction("Index", new { courseId });
    }
}

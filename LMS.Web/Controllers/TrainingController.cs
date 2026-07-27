using LMS.Web.Data;
using LMS.Web.Models;
using LMS.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LMS.Web.Controllers;

[Authorize]
public class TrainingController : Controller
{
    private readonly AppDbContext _db;
    public TrainingController(AppDbContext db) => _db = db;

    // Upcoming Sessions (Online/Offline)
    public async Task<IActionResult> Sessions()
    {
        var sessions = await _db.TrainingSessions
            .Include(s => s.Course).Include(s => s.Trainer)
            .Where(s => s.End >= DateTime.UtcNow)
            .OrderBy(s => s.Start).ToListAsync();
        ViewBag.CanManage = User.IsInRole("Admin") || User.IsInRole("Instructor") || User.IsInRole("Principal");
        ViewBag.Courses = ViewBag.CanManage
            ? await _db.Courses.OrderBy(c => c.Title).ToListAsync()
            : new List<Course>();
        return View(sessions);
    }

    [Authorize(Roles = "Admin,Instructor,Principal")]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateSession(string title, int? courseId, SessionMode mode, string location, DateTime start, DateTime end, string? notes)
    {
        var session = new TrainingSession
        {
            Title = title, CourseId = courseId, Mode = mode, Location = location,
            Start = start, End = end <= start ? start.AddHours(1) : end, Notes = notes,
            TrainerId = User.GetUserId()
        };
        _db.TrainingSessions.Add(session);
        _db.CalendarEvents.Add(new CalendarEvent { CourseId = courseId, Title = $"Session: {title}", Start = start, End = session.End, Type = EventType.Course });

        if (courseId != null)
        {
            var studentIds = await _db.Enrollments
                .Where(e => e.CourseId == courseId && e.Status != EnrollmentStatus.Dropped)
                .Select(e => e.StudentId).ToListAsync();
            Notifier.NotifyCourse(_db, studentIds, $"New training session scheduled: {title}", "/Training/Sessions");
        }
        await _db.SaveChangesAsync();
        TempData["Ok"] = "Session scheduled.";
        return RedirectToAction("Sessions");
    }

    [Authorize(Roles = "Admin,Instructor,Principal")]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteSession(int id)
    {
        var session = await _db.TrainingSessions.FindAsync(id);
        if (session != null)
        {
            if (!User.IsInRole("Admin") && session.TrainerId != User.GetUserId()) return Forbid();
            _db.TrainingSessions.Remove(session);
            await _db.SaveChangesAsync();
        }
        return RedirectToAction("Sessions");
    }
}

[Authorize]
public class KnowledgeController : Controller
{
    private readonly AppDbContext _db;
    private readonly IWebHostEnvironment _env;

    public KnowledgeController(AppDbContext db, IWebHostEnvironment env)
    {
        _db = db;
        _env = env;
    }

    private bool CanManage => User.IsInRole("Admin") || User.IsInRole("Instructor") || User.IsInRole("Principal");

    public async Task<IActionResult> Documents(DocumentCategory? category, string? q)
    {
        var docs = _db.Documents.Include(d => d.UploadedBy).AsQueryable();
        if (category != null) docs = docs.Where(d => d.Category == category);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim().ToLower();
            docs = docs.Where(d =>
                d.Title.ToLower().Contains(term) ||
                (d.Description != null && d.Description.ToLower().Contains(term)));
        }
        ViewBag.Category = category;
        ViewBag.Query = q;
        ViewBag.CanManage = CanManage;
        return View(await docs.OrderByDescending(d => d.UploadedAt).ToListAsync());
    }

    [Authorize(Roles = "Admin,Instructor,Principal")]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AddDocument(string title, DocumentCategory category, string? description, string? url, IFormFile? file)
    {
        // Uploaded images (jpeg/png/…) are converted to PDF automatically
        var finalUrl = await UploadHelper.SaveAsync(file, _env, "documents") ?? url;
        if (string.IsNullOrWhiteSpace(finalUrl))
        {
            TempData["Err"] = "Provide a link or attach a file.";
            return RedirectToAction("Documents");
        }
        _db.Documents.Add(new DocumentItem
        {
            Title = title, Category = category, Description = description,
            Url = finalUrl, UploadedById = User.GetUserId()
        });
        await _db.SaveChangesAsync();
        TempData["Ok"] = "Document added to the library.";
        return RedirectToAction("Documents");
    }

    [Authorize(Roles = "Admin,Instructor,Principal")]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteDocument(int id)
    {
        var doc = await _db.Documents.FindAsync(id);
        if (doc != null)
        {
            var fileUrl = doc.Url;
            _db.Documents.Remove(doc);
            await _db.SaveChangesAsync();
            UploadHelper.TryDeleteStored(fileUrl, _env);   // row gone — remove its hosted file
        }
        return RedirectToAction("Documents");
    }

    public async Task<IActionResult> Videos(string? q)
    {
        var videos = _db.Videos.AsQueryable();
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim().ToLower();
            videos = videos.Where(v =>
                v.Title.ToLower().Contains(term) ||
                v.Topic.ToLower().Contains(term));
        }
        ViewBag.Query = q;
        ViewBag.CanManage = CanManage;
        return View(await videos.OrderByDescending(v => v.AddedAt).ToListAsync());
    }

    [Authorize(Roles = "Admin,Instructor,Principal")]
    [HttpPost, ValidateAntiForgeryToken]
    [RequestSizeLimit(500_000_000)]
    [RequestFormLimits(MultipartBodyLengthLimit = 500_000_000)]
    public async Task<IActionResult> AddVideo(string title, string? url, string topic, int durationMinutes, IFormFile? file)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            TempData["Err"] = "Enter a title for the video.";
            return RedirectToAction("Videos");
        }
        // A video may be uploaded from the user's machine/server (stored under
        // wwwroot/uploads/videos and streamed by an HTML5 player) or referenced by
        // a link (YouTube embed or any hosted video URL). Exactly one is required.
        string? finalUrl = null;
        if (file != null && file.Length > 0)
        {
            finalUrl = await UploadHelper.SaveAsync(file, _env, "videos");
            if (finalUrl == null)
            {
                TempData["Err"] = "Unsupported video file. Upload an MP4, WebM, MOV or OGV file.";
                return RedirectToAction("Videos");
            }
        }
        else if (!string.IsNullOrWhiteSpace(url))
        {
            finalUrl = url.Trim();
        }

        if (string.IsNullOrWhiteSpace(finalUrl))
        {
            TempData["Err"] = "Provide a video link or upload a video file.";
            return RedirectToAction("Videos");
        }

        // The simple-type model binder maps an empty Topic field to null; the column is
        // NOT NULL, so coalesce to an empty string (Topic is optional).
        _db.Videos.Add(new VideoItem { Title = title, Url = finalUrl, Topic = topic ?? "", DurationMinutes = durationMinutes });
        await _db.SaveChangesAsync();
        TempData["Ok"] = "Video added to the knowledge hub.";
        return RedirectToAction("Videos");
    }

    [Authorize(Roles = "Admin,Instructor,Principal")]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteVideo(int id)
    {
        var video = await _db.Videos.FindAsync(id);
        if (video != null)
        {
            var fileUrl = video.Url;
            _db.Videos.Remove(video);
            await _db.SaveChangesAsync();
            UploadHelper.TryDeleteStored(fileUrl, _env);   // row gone — remove its hosted file
        }
        return RedirectToAction("Videos");
    }

    public async Task<IActionResult> Faqs()
    {
        ViewBag.CanManage = User.IsInRole("Admin");
        return View(await _db.Faqs.OrderBy(f => f.Order).ToListAsync());
    }

    [Authorize(Roles = "Admin")]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AddFaq(string question, string answer)
    {
        var maxOrder = await _db.Faqs.MaxAsync(f => (int?)f.Order) ?? 0;
        _db.Faqs.Add(new Faq { Question = question, Answer = answer, Order = maxOrder + 1 });
        await _db.SaveChangesAsync();
        return RedirectToAction("Faqs");
    }

    [Authorize(Roles = "Admin")]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteFaq(int id)
    {
        var faq = await _db.Faqs.FindAsync(id);
        if (faq != null) { _db.Faqs.Remove(faq); await _db.SaveChangesAsync(); }
        return RedirectToAction("Faqs");
    }
}

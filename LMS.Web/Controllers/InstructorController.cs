using LMS.Web.Data;
using LMS.Web.Models;
using LMS.Web.Services;
using LMS.Web.Services.Grading;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LMS.Web.Controllers;

[Authorize(Roles = "Instructor,Admin,Principal")]
public class InstructorController : Controller
{
    private readonly AppDbContext _db;
    private readonly IWebHostEnvironment _env;

    public InstructorController(AppDbContext db, IWebHostEnvironment env)
    {
        _db = db;
        _env = env;
    }

    private Task<string?> SaveLessonFileAsync(IFormFile? file) =>
        UploadHelper.SaveAsync(file, _env, "lessons"); // images auto-convert to PDF

    /// <summary>Best-effort delete of a SCORM package's extracted folder under
    /// wwwroot/scorm, guarded so it can only remove a folder inside that root.</summary>
    private void DeleteScormFolder(string? rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath)) return;
        try
        {
            var full = Path.GetFullPath(Path.Combine(_env.WebRootPath, "scorm", rootPath));
            var scormRoot = Path.GetFullPath(Path.Combine(_env.WebRootPath, "scorm")) + Path.DirectorySeparatorChar;
            if (full.StartsWith(scormRoot, StringComparison.Ordinal) && Directory.Exists(full))
                Directory.Delete(full, recursive: true);
        }
        catch { /* best-effort */ }
    }

    /// <summary>Courses this caller may build and manage: everything for Admin/Principal,
    /// otherwise the trainer's own courses plus any they hold an approved edit grant for
    /// (§CRS-11). Every management action in this controller funnels through here, so the
    /// grant takes effect everywhere at once.</summary>
    private IQueryable<Course> MyCourses() => CourseAccess.Editable(_db, User);

    /// <summary>Courses this trainer authored — the "My Courses" listing.</summary>
    private IQueryable<Course> AuthoredCourses()
    {
        var uid = User.GetUserId();
        return _db.Courses.Where(c => c.InstructorId == uid);
    }

    /// <summary>"My Courses" — the courses this trainer authored, which they may always
    /// edit, update and unpublish. Admin/Principal see the courses they authored too;
    /// their oversight of everything else is the All Courses listing.</summary>
    public async Task<IActionResult> MyCourseList()
    {
        var courses = await AuthoredCourses()
            .Include(c => c.Category).Include(c => c.Enrollments)
            .OrderBy(c => c.Title).ToListAsync();
        ViewBag.GrantedIds = new List<int>();
        return View("MyCourseList", courses);
    }

    /// <summary>"All Courses" — every course in the organisation (the tenant query filter
    /// scopes this automatically). A trainer may only open a course they did not author
    /// once an Admin or Principal has approved their edit request (§CRS-11); until then
    /// the row is read-only and offers a "Request edit access" button.</summary>
    public async Task<IActionResult> AllCourses(string? q)
    {
        var courses = _db.Courses.Include(c => c.Category).Include(c => c.Instructor).Include(c => c.Enrollments)
            .AsQueryable();
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim().ToLower();
            courses = courses.Where(c => c.Title.ToLower().Contains(term)
                || c.Code.ToLower().Contains(term)
                || (c.Instructor != null && c.Instructor.FullName.ToLower().Contains(term)));
        }

        var uid = User.GetUserId();
        ViewBag.Query = q;
        ViewBag.MyId = uid;
        ViewBag.IsOversight = CourseAccess.IsOversight(User);
        ViewBag.GrantedIds = await CourseAccess.GrantedCourseIdsAsync(_db, uid);
        ViewBag.PendingIds = await CourseAccess.PendingCourseIdsAsync(_db, uid);
        return View(await courses.OrderBy(c => c.Title).ToListAsync());
    }

    /// <summary>A trainer asks an Admin/Principal for edit rights on someone else's course.</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> RequestEditAccess(int courseId, string? reason)
    {
        var course = await _db.Courses.FirstOrDefaultAsync(c => c.Id == courseId);
        if (course == null) return NotFound();

        var uid = User.GetUserId();
        if (await CourseAccess.CanEditAsync(_db, User, course))
        {
            TempData["Err"] = "You can already edit this course.";
            return RedirectToAction("AllCourses");
        }
        // One open request per course per trainer — asking twice must not queue twice.
        if (await _db.CourseEditRequests.AnyAsync(r => r.CourseId == courseId && r.TrainerId == uid
                && r.Status == CourseEditAccessStatus.Pending))
        {
            TempData["Err"] = "You already have a request awaiting a decision for this course.";
            return RedirectToAction("AllCourses");
        }

        _db.CourseEditRequests.Add(new CourseEditRequest
        {
            CourseId = courseId, TrainerId = uid, Reason = (reason ?? "").Trim(),
            Status = CourseEditAccessStatus.Pending
        });

        // Tell the people who can decide it — the tenant's Admins and Principals.
        var orgId = await _db.Users.Where(u => u.Id == uid).Select(u => u.OrganisationId).FirstOrDefaultAsync();
        var deciders = await _db.Users.Where(u => u.OrganisationId == orgId && u.IsActive)
            .Join(_db.UserRoles, u => u.Id, ur => ur.UserId, (u, ur) => new { u.Id, ur.RoleId })
            .Join(_db.Roles.Where(r => r.Name == "Admin" || r.Name == "Principal"),
                  x => x.RoleId, r => r.Id, (x, r) => x.Id)
            .Distinct().ToListAsync();
        foreach (var adminId in deciders)
            Notifier.Notify(_db, adminId, $"Edit access requested for \"{course.Title}\".", "/Instructor/CourseEditRequests");

        Notifier.Audit(_db, uid, User.Identity!.Name ?? "", "RequestCourseEditAccess", course.Title);
        await _db.SaveChangesAsync();
        TempData["Ok"] = "Request sent. You will be notified once an Admin or Principal decides.";
        return RedirectToAction("AllCourses");
    }

    // ---------- Edit-access approvals (Admin / Principal) ----------
    // Action-level roles narrow the controller's Instructor,Admin,Principal to the two
    // that may decide; a trainer reaching these URLs directly is refused.

    /// <summary>The decision queue: requests awaiting a decision, plus the grants currently
    /// in force so they can be revoked (§CRS-11).</summary>
    [Authorize(Roles = "Admin,Principal")]
    public async Task<IActionResult> CourseEditRequests()
    {
        var all = await _db.CourseEditRequests
            .Include(r => r.Course).ThenInclude(c => c!.Instructor)   // the queue shows who authored it
            .Include(r => r.Trainer).Include(r => r.DecidedBy)
            .OrderByDescending(r => r.RequestedAt).ToListAsync();
        ViewBag.Pending = all.Where(r => r.Status == CourseEditAccessStatus.Pending).ToList();
        ViewBag.Granted = all.Where(r => r.Status == CourseEditAccessStatus.Approved).ToList();
        return View(all.Where(r => r.Status is CourseEditAccessStatus.Rejected or CourseEditAccessStatus.Revoked).ToList());
    }

    [Authorize(Roles = "Admin,Principal")]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DecideCourseEditRequest(int id, bool approve, string? note)
    {
        var req = await _db.CourseEditRequests.Include(r => r.Course)
            .FirstOrDefaultAsync(r => r.Id == id && r.Status == CourseEditAccessStatus.Pending);
        if (req == null) return NotFound();

        req.Status = approve ? CourseEditAccessStatus.Approved : CourseEditAccessStatus.Rejected;
        req.DecisionNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        req.DecidedById = User.GetUserId();
        req.DecidedAt = DateTime.UtcNow;

        Notifier.Notify(_db, req.TrainerId,
            approve ? $"You can now edit \"{req.Course!.Title}\"."
                    : $"Your request to edit \"{req.Course!.Title}\" was declined.",
            approve ? $"/Instructor/ManageCourse/{req.CourseId}" : "/Instructor/AllCourses");
        Notifier.Audit(_db, User.GetUserId(), User.Identity!.Name ?? "",
            approve ? "ApproveCourseEditAccess" : "RejectCourseEditAccess", req.Course!.Title);
        await _db.SaveChangesAsync();
        TempData["Ok"] = approve ? "Edit access granted." : "Request declined.";
        return RedirectToAction("CourseEditRequests");
    }

    /// <summary>Withdraw a grant already in force. The row is kept as Revoked rather than
    /// deleted, so the history of who had access and when is never lost.</summary>
    [Authorize(Roles = "Admin,Principal")]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> RevokeCourseEditAccess(int id, string? note)
    {
        var req = await _db.CourseEditRequests.Include(r => r.Course)
            .FirstOrDefaultAsync(r => r.Id == id && r.Status == CourseEditAccessStatus.Approved);
        if (req == null) return NotFound();

        req.Status = CourseEditAccessStatus.Revoked;
        req.DecisionNote = string.IsNullOrWhiteSpace(note) ? req.DecisionNote : note.Trim();
        req.DecidedById = User.GetUserId();
        req.DecidedAt = DateTime.UtcNow;

        Notifier.Notify(_db, req.TrainerId, $"Your edit access to \"{req.Course!.Title}\" has been withdrawn.", "/Instructor/AllCourses");
        Notifier.Audit(_db, User.GetUserId(), User.Identity!.Name ?? "", "RevokeCourseEditAccess", req.Course!.Title);
        await _db.SaveChangesAsync();
        TempData["Ok"] = "Edit access withdrawn.";
        return RedirectToAction("CourseEditRequests");
    }

    public async Task<IActionResult> Dashboard()
    {
        var courses = await MyCourses()
            .Include(c => c.Enrollments)
            .Include(c => c.Assignments).ThenInclude(a => a.Submissions)
            .Include(c => c.Modules).ThenInclude(m => m.Lessons)
            .ToListAsync();
        ViewBag.Courses = courses;
        ViewBag.TotalStudents = courses.SelectMany(c => c.Enrollments.Select(e => e.StudentId)).Distinct().Count();
        ViewBag.PendingGrading = courses.SelectMany(c => c.Assignments).SelectMany(a => a.Submissions).Count(s => s.Grade == null);
        var courseIds = courses.Select(c => c.Id).ToList();
        ViewBag.RecentThreads = await _db.DiscussionThreads
            .Include(t => t.Author).Include(t => t.Course)
            .Where(t => courseIds.Contains(t.CourseId))
            .OrderByDescending(t => t.CreatedAt).Take(5).ToListAsync();

        var feedback = await _db.CourseFeedbacks.Where(f => courseIds.Contains(f.CourseId)).ToListAsync();
        ViewBag.AvgRating = feedback.Count == 0 ? 0 : feedback.Average(f => (double)f.Rating);
        ViewBag.FeedbackCount = feedback.Count;
        ViewBag.RecentFeedback = await _db.CourseFeedbacks
            .Include(f => f.Course).Include(f => f.Student)
            .Where(f => courseIds.Contains(f.CourseId))
            .OrderByDescending(f => f.SubmittedAt).Take(4).ToListAsync();
        ViewBag.UpcomingSessions = await _db.TrainingSessions
            .Include(s => s.Course)
            .Where(s => s.End >= DateTime.UtcNow && (s.CourseId == null || courseIds.Contains(s.CourseId.Value)))
            .OrderBy(s => s.Start).Take(5).ToListAsync();

        // completion % per course: lessons done / (lessons × enrolled)
        var lessonIdsByCourse = courses.ToDictionary(c => c.Id, c => c.Modules.SelectMany(m => m.Lessons.Select(l => l.Id)).ToList());
        var allLessonIds = lessonIdsByCourse.Values.SelectMany(x => x).ToList();
        var progress = await _db.LessonProgress.Where(p => allLessonIds.Contains(p.LessonId)).ToListAsync();
        ViewBag.CompletionRates = courses.ToDictionary(
            c => c.Id,
            c =>
            {
                var lessons = lessonIdsByCourse[c.Id];
                var active = c.Enrollments.Count(e => e.Status != EnrollmentStatus.Dropped);
                if (lessons.Count == 0 || active == 0) return 0.0;
                var done = progress.Count(p => lessons.Contains(p.LessonId));
                return Math.Min(100.0, (double)done / (lessons.Count * active) * 100);
            });
        return View();
    }

    // ---------- Course CRUD ----------
    [HttpGet]
    public async Task<IActionResult> CreateCourse()
    {
        ViewBag.Categories = await _db.Categories.OrderBy(c => c.Name).ToListAsync();
        return View();
    }

    [HttpPost, ValidateAntiForgeryToken]
    [RequestSizeLimit(200_000_000)]
    public async Task<IActionResult> CreateCourse(Course course,
        string? materialTitle, string? materialType, IFormFile? materialFile, string? materialUrl)
    {
        course.InstructorId = User.GetUserId();
        course.Id = 0;
        // Multi-tenancy: the course belongs to its creator's organisation
        course.OrganisationId = await _db.Users
            .Where(u => u.Id == course.InstructorId)
            .Select(u => u.OrganisationId).FirstOrDefaultAsync();

        // Optional initial training material (PDF document or video)
        if (!string.IsNullOrWhiteSpace(materialTitle))
        {
            var module = new Module { Title = "Module 1 — Course Material", Order = 1 };
            if (materialType == "Video" && !string.IsNullOrWhiteSpace(materialUrl))
            {
                module.Lessons.Add(new Lesson
                {
                    Title = materialTitle, Type = LessonType.Video, Order = 1,
                    Url = materialUrl.Trim(), DurationMinutes = 15
                });
            }
            else
            {
                var fileUrl = await SaveLessonFileAsync(materialFile);
                if (fileUrl != null)
                {
                    module.Lessons.Add(new Lesson
                    {
                        Title = materialTitle, Type = LessonType.File, Order = 1,
                        Url = fileUrl, DurationMinutes = 15
                    });
                }
            }
            if (module.Lessons.Any()) course.Modules.Add(module);
        }

        _db.Courses.Add(course);
        Notifier.Audit(_db, course.InstructorId, User.Identity!.Name ?? "", "CreateCourse", course.Title);
        await _db.SaveChangesAsync();
        TempData["Ok"] = course.Modules.Any()
            ? "Course created with the training material attached."
            : "Course created — add modules and lessons below.";
        return RedirectToAction("ManageCourse", new { id = course.Id });
    }

    public async Task<IActionResult> ManageCourse(int id)
    {
        var course = await MyCourses()
            .Include(c => c.Category)
            .Include(c => c.Modules.OrderBy(m => m.Order)).ThenInclude(m => m.Lessons.OrderBy(l => l.Order))
            .Include(c => c.Assignments)
            .Include(c => c.Quizzes).ThenInclude(q => q.Questions)
            .Include(c => c.Enrollments).ThenInclude(e => e.Student)
            .Include(c => c.Announcements)
            .FirstOrDefaultAsync(c => c.Id == id);
        if (course == null) return NotFound();
        ViewBag.Categories = await _db.Categories.OrderBy(c => c.Name).ToListAsync();
        return View(course);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateCourse(int id, string title, string code, string description, int? categoryId, double passingGrade, bool issuesCertificate, bool requiresAssessment, DateTime? startDate, DateTime? endDate)
    {
        var course = await MyCourses().FirstOrDefaultAsync(c => c.Id == id);
        if (course == null) return NotFound();
        course.Title = title; course.Code = code; course.Description = description;
        course.CategoryId = categoryId; course.PassingGrade = passingGrade;
        course.IssuesCertificate = issuesCertificate;
        course.RequiresAssessment = requiresAssessment;
        course.StartDate = startDate; course.EndDate = endDate;
        await _db.SaveChangesAsync();
        TempData["Ok"] = "Course updated.";
        return RedirectToAction("ManageCourse", new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> TogglePublish(int id)
    {
        var course = await MyCourses().FirstOrDefaultAsync(c => c.Id == id);
        if (course == null) return NotFound();
        // A course flagged as requiring an assessment cannot be published until one exists.
        if (!course.IsPublished && course.RequiresAssessment && !await HasAssessmentAsync(id))
        {
            TempData["Err"] = "Please set-up the Quiz/Assessment associated with this Course.";
            return RedirectToAction("ManageCourse", new { id });
        }
        course.IsPublished = !course.IsPublished;
        Notifier.Audit(_db, User.GetUserId(), User.Identity!.Name ?? "",
            course.IsPublished ? "PublishCourse" : "UnpublishCourse", course.Title);
        await _db.SaveChangesAsync();
        TempData["Ok"] = course.IsPublished
            ? "Course republished — it is back in the catalogue and open to learners."
            : "Course unpublished. Learners cannot open it or its assessments while you revise it.";
        return RedirectToAction("ManageCourse", new { id });
    }

    /// <summary>True when the course has a graded quiz (published, not self-assessment)
    /// or an assignment — the assessment the completion gate checks.</summary>
    private async Task<bool> HasAssessmentAsync(int courseId) =>
        await _db.Quizzes.AnyAsync(q => q.CourseId == courseId && q.IsPublished && !q.IsSelfAssessment)
        || await _db.Assignments.AnyAsync(a => a.CourseId == courseId);

    // ---------- Modules & Lessons ----------
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AddModule(int courseId, string title)
    {
        var course = await MyCourses().Include(c => c.Modules).FirstOrDefaultAsync(c => c.Id == courseId);
        if (course == null) return NotFound();
        course.Modules.Add(new Module { Title = title, Order = course.Modules.Count + 1 });
        await _db.SaveChangesAsync();
        return RedirectToAction("ManageCourse", new { id = courseId });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteModule(int id)
    {
        var module = await _db.Modules.Include(m => m.Course).FirstOrDefaultAsync(m => m.Id == id);
        if (module == null || !await CourseAccess.CanEditAsync(_db, User, module.Course!)) return NotFound();
        _db.Modules.Remove(module);
        await _db.SaveChangesAsync();
        return RedirectToAction("ManageCourse", new { id = module.CourseId });
    }

    [HttpPost, ValidateAntiForgeryToken]
    [RequestSizeLimit(200_000_000)]
    public async Task<IActionResult> AddLesson(int moduleId, string title, LessonType type, string? content, string? url, int durationMinutes, IFormFile? file, string? transcript = null, IFormFile? transcriptFile = null)
    {
        var module = await _db.Modules.Include(m => m.Course).Include(m => m.Lessons).FirstOrDefaultAsync(m => m.Id == moduleId);
        if (module == null || !await CourseAccess.CanEditAsync(_db, User, module.Course!)) return NotFound();

        // Uploaded document (PDF etc.) takes precedence over a typed URL
        var fileUrl = await SaveLessonFileAsync(file);
        if (fileUrl != null && type != LessonType.Video) type = LessonType.File;

        var lesson = new Lesson
        {
            Title = title, Type = type, Content = HtmlSanitizer.Clean(content), Url = fileUrl ?? url,
            DurationMinutes = durationMinutes <= 0 ? 10 : durationMinutes,
            Order = module.Lessons.Count + 1,
            ExtractedText = await MaterialTextAsync(fileUrl, transcript, transcriptFile)
        };
        module.Lessons.Add(lesson);
        await _db.SaveChangesAsync();

        // Tell the trainer plainly whether this material can be referenced by the AI — a scanned
        // PDF has no text layer and would otherwise be added silently and never used.
        if (fileUrl != null && string.IsNullOrWhiteSpace(lesson.ExtractedText))
            TempData["Err"] = $"\"{title}\" was uploaded, but no text could be read from it "
                + "(a scanned PDF has no text layer). The AI cannot reference it until you paste "
                + "its text in, using Material text on the lesson.";
        else if (!string.IsNullOrWhiteSpace(lesson.ExtractedText))
            TempData["Ok"] = $"\"{title}\" added — {lesson.ExtractedText.Split(' ').Length} words are now "
                + "available to the AI for grading and quiz generation. Re-index to apply immediately.";

        return RedirectToAction("ManageCourse", new { id = module.CourseId });
    }

    /// <summary>Text the AI can reference for this material (§AIG-14): extracted from an uploaded
    /// document, or the transcript a trainer supplies for a video (pasted, or a .vtt/.srt/.txt
    /// caption file exported from the video platform).</summary>
    private async Task<string?> MaterialTextAsync(string? fileUrl, string? transcript, IFormFile? transcriptFile)
    {
        if (transcriptFile is { Length: > 0 })
        {
            var saved = await UploadHelper.SaveAsync(transcriptFile, _env, "transcripts");
            if (saved != null)
            {
                var abs = Path.Combine(_env.WebRootPath, saved.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
                var text = MaterialTextExtractor.Extract(abs);
                if (!string.IsNullOrWhiteSpace(text)) return text;
            }
        }
        if (!string.IsNullOrWhiteSpace(transcript)) return transcript.Trim();

        if (!string.IsNullOrWhiteSpace(fileUrl))
        {
            var abs = Path.Combine(_env.WebRootPath, fileUrl.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
            if (MaterialTextExtractor.CanExtract(abs)) return MaterialTextExtractor.Extract(abs);
        }
        return null;
    }

    /// <summary>Set or replace the AI-readable text of an existing material — used to add a video
    /// transcript, or to supply the text of a scanned document extraction could not read.</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SetMaterialText(int id, string? transcript, IFormFile? transcriptFile)
    {
        var lesson = await _db.Lessons.Include(l => l.Module)!.ThenInclude(m => m!.Course)
            .FirstOrDefaultAsync(l => l.Id == id);
        if (lesson == null || !await CourseAccess.CanEditAsync(_db, User, lesson.Module!.Course!)) return NotFound();

        var text = await MaterialTextAsync(null, transcript, transcriptFile);
        if (string.IsNullOrWhiteSpace(text))
        {
            // Re-extract from the stored file when nothing new was supplied (e.g. after an upgrade
            // that added extraction to material uploaded before it existed).
            if (!string.IsNullOrWhiteSpace(lesson.Url) && !lesson.Url.StartsWith("http"))
            {
                var abs = Path.Combine(_env.WebRootPath, lesson.Url.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
                if (MaterialTextExtractor.CanExtract(abs)) text = MaterialTextExtractor.Extract(abs);
            }
        }

        lesson.ExtractedText = string.IsNullOrWhiteSpace(text) ? null : text;
        Notifier.Audit(_db, User.GetUserId(), User.Identity!.Name ?? "", "SetMaterialText",
            $"{lesson.Module!.Course!.Title} / {lesson.Title}: {(lesson.ExtractedText?.Split(' ').Length ?? 0)} words");
        await _db.SaveChangesAsync();
        TempData[lesson.ExtractedText == null ? "Err" : "Ok"] = lesson.ExtractedText == null
            ? "No text could be read. Paste the text in directly so the AI can reference this material."
            : $"Saved — {lesson.ExtractedText.Split(' ').Length} words are now available to the AI. Re-index to apply immediately.";
        return RedirectToAction("ManageCourse", new { id = lesson.Module!.CourseId });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteLesson(int id)
    {
        var lesson = await _db.Lessons.Include(l => l.Module)!.ThenInclude(m => m!.Course).FirstOrDefaultAsync(l => l.Id == id);
        if (lesson == null || !await CourseAccess.CanEditAsync(_db, User, lesson.Module!.Course!)) return NotFound();
        var courseId = lesson.Module!.CourseId;

        // Remove the lesson's own hosted file (e.g. an uploaded PDF) if it has one.
        var lessonFileUrl = lesson.Url;

        // For a SCORM/cmi5 lesson, also remove its content package and extracted folder —
        // but only if no other lesson still uses that package.
        ContentPackage? pkg = null;
        if (lesson.Type == LessonType.Scorm && lesson.ContentPackageId is int pkgId)
        {
            pkg = await _db.ContentPackages.FindAsync(pkgId);
            if (pkg != null && await _db.Lessons.AnyAsync(l => l.Id != lesson.Id && l.ContentPackageId == pkgId))
                pkg = null; // shared package — leave it and its folder in place
        }

        _db.Lessons.Remove(lesson);
        if (pkg != null) _db.ContentPackages.Remove(pkg);   // cascades ScormRuntimeData
        await _db.SaveChangesAsync();

        UploadHelper.TryDeleteStored(lessonFileUrl, _env);
        if (pkg != null) DeleteScormFolder(pkg.RootPath);
        return RedirectToAction("ManageCourse", new { id = courseId });
    }

    // ---------- Course announcements ----------
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AddAnnouncement(int courseId, string title, string body)
    {
        var course = await MyCourses().Include(c => c.Enrollments).FirstOrDefaultAsync(c => c.Id == courseId);
        if (course == null) return NotFound();
        _db.Announcements.Add(new Announcement { CourseId = courseId, Title = title, Body = body, AuthorId = User.GetUserId() });
        Notifier.NotifyCourse(_db, course.Enrollments.Select(e => e.StudentId), $"{course.Code}: {title}", $"/Courses/Details/{courseId}");
        await _db.SaveChangesAsync();
        return RedirectToAction("ManageCourse", new { id = courseId });
    }

    // ---------- Gradebook ----------
    public async Task<IActionResult> Gradebook(int id)
    {
        var course = await MyCourses()
            .Include(c => c.Enrollments).ThenInclude(e => e.Student)
            .Include(c => c.Assignments).ThenInclude(a => a.Submissions)
            .Include(c => c.Quizzes).ThenInclude(q => q.Attempts)
            .Include(c => c.Modules).ThenInclude(m => m.Lessons)
            .FirstOrDefaultAsync(c => c.Id == id);
        if (course == null) return NotFound();

        var lessonIds = course.Modules.SelectMany(m => m.Lessons.Select(l => l.Id)).ToList();
        ViewBag.Progress = await _db.LessonProgress
            .Where(p => lessonIds.Contains(p.LessonId))
            .GroupBy(p => p.StudentId)
            .Select(g => new { StudentId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.StudentId, x => x.Count);
        ViewBag.TotalLessons = lessonIds.Count;
        return View(course);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> FinalizeGrade(int enrollmentId, double finalGrade)
    {
        var enrollment = await _db.Enrollments.Include(e => e.Course).FirstOrDefaultAsync(e => e.Id == enrollmentId);
        if (enrollment == null || !await CourseAccess.CanEditAsync(_db, User, enrollment.Course!)) return NotFound();
        enrollment.FinalGrade = finalGrade;
        if (finalGrade >= enrollment.Course!.PassingGrade)
        {
            enrollment.Status = EnrollmentStatus.Completed;
            enrollment.CompletedAt = DateTime.UtcNow;
            if (enrollment.Course.IssuesCertificate && !await _db.Certificates.AnyAsync(c => c.EnrollmentId == enrollmentId))
            {
                _db.Certificates.Add(new Certificate
                {
                    EnrollmentId = enrollmentId,
                    SerialNumber = $"CERT-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString()[..8].ToUpper()}"
                });
                Notifier.Notify(_db, enrollment.StudentId, $"Congratulations! You earned a certificate for {enrollment.Course.Title}.", "/Certificates");
            }
        }
        Notifier.Notify(_db, enrollment.StudentId, $"Final grade posted for {enrollment.Course.Title}: {finalGrade:0.#}%");
        await _db.SaveChangesAsync();
        TempData["Ok"] = "Final grade saved.";
        return RedirectToAction("Gradebook", new { id = enrollment.CourseId });
    }

    // ---------- Attendance ----------
    public async Task<IActionResult> Attendance(int id, DateTime? date)
    {
        var course = await MyCourses()
            .Include(c => c.Enrollments).ThenInclude(e => e.Student)
            .FirstOrDefaultAsync(c => c.Id == id);
        if (course == null) return NotFound();
        var day = (date ?? DateTime.UtcNow).Date;
        ViewBag.Date = day;
        ViewBag.Existing = await _db.AttendanceRecords
            .Where(a => a.CourseId == id && a.Date == day)
            .ToDictionaryAsync(a => a.StudentId, a => a.Status);
        return View(course);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveAttendance(int courseId, DateTime date, Dictionary<string, AttendanceStatus> statuses)
    {
        var course = await MyCourses().FirstOrDefaultAsync(c => c.Id == courseId);
        if (course == null) return NotFound();
        var day = date.Date;
        var existing = await _db.AttendanceRecords.Where(a => a.CourseId == courseId && a.Date == day).ToListAsync();
        foreach (var kv in statuses)
        {
            var rec = existing.FirstOrDefault(a => a.StudentId == kv.Key);
            if (rec != null) rec.Status = kv.Value;
            else _db.AttendanceRecords.Add(new AttendanceRecord { CourseId = courseId, StudentId = kv.Key, Date = day, Status = kv.Value });
        }
        await _db.SaveChangesAsync();
        TempData["Ok"] = "Attendance saved.";
        return RedirectToAction("Attendance", new { id = courseId, date = day });
    }

    // ---------- Training batches ----------
    private IQueryable<TrainingBatch> MyBatches()
    {
        var uid = User.GetUserId();
        return User.IsInRole("Admin") || User.IsInRole("Principal")
            ? _db.TrainingBatches
            : _db.TrainingBatches.Where(b => b.CreatedById == uid);
    }

    public async Task<IActionResult> Batches(int? editId)
    {
        ViewBag.Courses = await MyCourses().Where(c => c.IsPublished && c.IsActive).OrderBy(c => c.Code).ToListAsync();
        // Venue/room suggestions defined for this user's organisation at onboarding
        var myOrgId = await _db.Users.Where(u => u.Id == User.GetUserId()).Select(u => u.OrganisationId).FirstOrDefaultAsync();
        ViewBag.Locations = await _db.TrainingLocations
            .Where(l => l.OrganisationId == myOrgId)
            .OrderBy(l => l.Name).ThenBy(l => l.Room).ToListAsync();
        ViewBag.Edit = editId == null ? null : await MyBatches().FirstOrDefaultAsync(b => b.Id == editId);
        var batches = await MyBatches()
            .Include(b => b.Course).Include(b => b.CreatedBy)
            .OrderByDescending(b => b.CreatedAt).ToListAsync();
        return View(batches);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveBatch(int? id, string name, int courseId, DateTime startDate, DateTime endDate, int maxIntake, string? location, string? room, string? description)
    {
        var course = await MyCourses().FirstOrDefaultAsync(c => c.Id == courseId);
        if (course == null) return NotFound();
        if (string.IsNullOrWhiteSpace(name) || endDate < startDate || maxIntake < 1)
        {
            TempData["Err"] = "Check the batch details: name is required, end date must not be before start date and intake must be at least 1.";
            return RedirectToAction("Batches");
        }

        var batch = id == null ? new TrainingBatch { CreatedById = User.GetUserId() } : await MyBatches().FirstOrDefaultAsync(b => b.Id == id);
        if (batch == null) return NotFound();
        batch.Name = name.Trim();
        batch.CourseId = courseId;
        batch.StartDate = startDate.Date;
        batch.EndDate = endDate.Date;
        batch.MaxIntake = maxIntake;
        batch.Location = string.IsNullOrWhiteSpace(location) ? null : location.Trim();
        batch.Room = string.IsNullOrWhiteSpace(room) ? null : room.Trim();
        batch.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        if (id == null) _db.TrainingBatches.Add(batch);
        Notifier.Audit(_db, User.GetUserId(), User.Identity!.Name ?? "", id == null ? "CreateBatch" : "UpdateBatch", $"{batch.Name} ({course.Code})");
        await _db.SaveChangesAsync();
        TempData["Ok"] = id == null ? "Batch created." : "Batch updated.";
        return RedirectToAction("Batches");
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteBatch(int id)
    {
        var batch = await MyBatches().Include(b => b.Course).FirstOrDefaultAsync(b => b.Id == id);
        if (batch == null) return NotFound();
        _db.TrainingBatches.Remove(batch);
        Notifier.Audit(_db, User.GetUserId(), User.Identity!.Name ?? "", "DeleteBatch", $"{batch.Name} ({batch.Course?.Code})");
        await _db.SaveChangesAsync();
        TempData["Ok"] = "Batch deleted.";
        return RedirectToAction("Batches");
    }
}

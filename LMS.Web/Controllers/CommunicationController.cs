using LMS.Web.Data;
using LMS.Web.Models;
using LMS.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LMS.Web.Controllers;

/// <summary>
/// Communication Center — role-aware messaging hub.
/// Trainee (Student): can contact their trainers, the Principal, and Admin.
/// Trainer (Instructor): can contact their trainees, fellow trainers, the Principal, and Admin; can broadcast to a course.
/// Principal: can contact everyone; can broadcast to all trainers, all trainees, or everyone.
/// Admin: full access, same broadcast powers as Principal.
/// </summary>
[Authorize]
public class CommunicationController : Controller
{
    private readonly AppDbContext _db;
    private readonly UserManager<ApplicationUser> _userManager;

    public CommunicationController(AppDbContext db, UserManager<ApplicationUser> userManager)
    {
        _db = db;
        _userManager = userManager;
    }

    private async Task<Dictionary<string, List<ApplicationUser>>> GetAllowedRecipientsAsync()
    {
        var uid = User.GetUserId();
        var groups = new Dictionary<string, List<ApplicationUser>>();

        List<ApplicationUser> Active(IEnumerable<ApplicationUser> users) =>
            users.Where(u => u.IsActive && u.Id != uid).OrderBy(u => u.FullName).ToList();

        var admins = Active(await _userManager.GetUsersInRoleAsync("Admin"));
        var principals = Active(await _userManager.GetUsersInRoleAsync("Principal"));
        var trainers = Active(await _userManager.GetUsersInRoleAsync("Instructor"));
        var trainees = Active(await _userManager.GetUsersInRoleAsync("Student"));

        if (User.IsInRole("Admin") || User.IsInRole("Principal"))
        {
            groups["Admins"] = admins;
            groups["Principals"] = principals;
            groups["Trainers"] = trainers;
            groups["Trainees"] = trainees;
        }
        else if (User.IsInRole("Instructor"))
        {
            var myStudentIds = await _db.Enrollments
                .Where(e => e.Course!.InstructorId == uid && e.Status != EnrollmentStatus.Dropped)
                .Select(e => e.StudentId).Distinct().ToListAsync();
            groups["My trainees"] = trainees.Where(t => myStudentIds.Contains(t.Id)).ToList();
            groups["Fellow trainers"] = trainers;
            groups["Principals"] = principals;
            groups["Admins"] = admins;
        }
        else // Trainee
        {
            var myInstructorIds = await _db.Enrollments
                .Where(e => e.StudentId == uid && e.Status != EnrollmentStatus.Dropped)
                .Select(e => e.Course!.InstructorId).Distinct().ToListAsync();
            groups["My trainers"] = trainers.Where(t => myInstructorIds.Contains(t.Id)).ToList();
            groups["Principals"] = principals;
            groups["Admins"] = admins;
        }

        return groups.Where(g => g.Value.Any()).ToDictionary(g => g.Key, g => g.Value);
    }

    public async Task<IActionResult> Index(string box = "inbox")
    {
        var uid = User.GetUserId();
        var messages = box == "sent"
            ? await _db.Messages.Include(m => m.Recipient).Where(m => m.SenderId == uid).OrderByDescending(m => m.SentAt).Take(100).ToListAsync()
            : await _db.Messages.Include(m => m.Sender).Where(m => m.RecipientId == uid).OrderByDescending(m => m.SentAt).Take(100).ToListAsync();

        ViewBag.Box = box;
        ViewBag.Recipients = await GetAllowedRecipientsAsync();
        ViewBag.CanBroadcast = User.IsInRole("Admin") || User.IsInRole("Principal") || User.IsInRole("Instructor");
        ViewBag.MyCourses = User.IsInRole("Instructor")
            ? await _db.Courses.Where(c => c.InstructorId == uid).OrderBy(c => c.Title).ToListAsync()
            : new List<Course>();
        return View(messages);
    }

    public async Task<IActionResult> Read(int id)
    {
        var uid = User.GetUserId();
        var message = await _db.Messages.Include(m => m.Sender).Include(m => m.Recipient)
            .FirstOrDefaultAsync(m => m.Id == id && (m.RecipientId == uid || m.SenderId == uid));
        if (message == null) return NotFound();
        if (message.RecipientId == uid && message.ReadAt == null)
        {
            message.ReadAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }
        // Reply is allowed back to the sender regardless of group filters
        ViewBag.ReplyToId = message.RecipientId == uid ? message.SenderId : message.RecipientId;
        ViewBag.ReplyToName = message.RecipientId == uid ? message.Sender?.FullName : message.Recipient?.FullName;
        return View(message);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Send(string recipientId, string subject, string body, bool isReply = false)
    {
        var uid = User.GetUserId();
        if (recipientId == uid)
        {
            TempData["Err"] = "You cannot message yourself.";
            return RedirectToAction("Index");
        }

        // Replies bypass the group filter (you can always answer someone who wrote to you)
        var allowed = isReply && await _db.Messages.AnyAsync(m =>
            (m.SenderId == recipientId && m.RecipientId == uid) || (m.SenderId == uid && m.RecipientId == recipientId));
        if (!allowed)
        {
            var groups = await GetAllowedRecipientsAsync();
            allowed = groups.Values.Any(g => g.Any(u => u.Id == recipientId));
        }
        if (!allowed)
        {
            TempData["Err"] = "You are not permitted to message this user.";
            return RedirectToAction("Index");
        }

        _db.Messages.Add(new Message { SenderId = uid, RecipientId = recipientId, Subject = subject, Body = body });
        await _db.SaveChangesAsync();
        TempData["Ok"] = "Message sent.";
        return RedirectToAction("Index", new { box = "sent" });
    }

    [Authorize(Roles = "Admin,Principal,Instructor")]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Broadcast(string audience, int? courseId, string subject, string body)
    {
        var uid = User.GetUserId();
        List<string> recipientIds;
        string audienceLabel;

        if (User.IsInRole("Instructor") && !User.IsInRole("Admin") && !User.IsInRole("Principal"))
        {
            // Trainers may only broadcast to trainees of their own course
            var course = await _db.Courses.Include(c => c.Enrollments)
                .FirstOrDefaultAsync(c => c.Id == courseId && c.InstructorId == uid);
            if (course == null)
            {
                TempData["Err"] = "Select one of your own courses to broadcast to.";
                return RedirectToAction("Index");
            }
            recipientIds = course.Enrollments
                .Where(e => e.Status != EnrollmentStatus.Dropped)
                .Select(e => e.StudentId).Distinct().ToList();
            audienceLabel = $"trainees of {course.Code}";
        }
        else
        {
            IList<ApplicationUser> users = audience switch
            {
                "trainers" => await _userManager.GetUsersInRoleAsync("Instructor"),
                "trainees" => await _userManager.GetUsersInRoleAsync("Student"),
                _ => await _db.Users.Where(u => u.IsActive).ToListAsync()
            };
            recipientIds = users.Where(u => u.IsActive && u.Id != uid).Select(u => u.Id).Distinct().ToList();
            audienceLabel = audience switch { "trainers" => "all trainers", "trainees" => "all trainees", _ => "everyone" };
        }

        foreach (var rid in recipientIds)
            _db.Messages.Add(new Message { SenderId = uid, RecipientId = rid, Subject = $"[Broadcast] {subject}", Body = body });

        Notifier.Audit(_db, uid, User.Identity!.Name ?? "", "Broadcast", $"To {audienceLabel}: {subject}");
        await _db.SaveChangesAsync();
        TempData["Ok"] = $"Broadcast sent to {recipientIds.Count} recipient(s) ({audienceLabel}).";
        return RedirectToAction("Index", new { box = "sent" });
    }
}

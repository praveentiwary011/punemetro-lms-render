using System.Globalization;
using LMS.Web.Data;
using LMS.Web.Models;
using Microsoft.EntityFrameworkCore;

namespace LMS.Web.Services.Email;

/// <summary>A scheduled email job. Both implementations decide for themselves whether
/// they are due, so the driving timer's interval affects promptness, never correctness.
/// <c>ignoreSchedule</c> is what the admin screen's "run now" uses.</summary>
public interface IEmailJob
{
    Task<int> RunAsync(CancellationToken ct, bool ignoreSchedule = false);
}

/// <summary>Shared helpers for the two scheduled notifiers.</summary>
internal static class NotifierHelpers
{
    /// <summary>Roles that make someone staff. Anyone holding one of these is excluded
    /// from the new-course digest even if they also hold the learner role — trainers,
    /// principals and administrators see the catalogue in the application and should not
    /// be told about courses they may have authored themselves (§NTF-03.2).</summary>
    private static readonly string[] StaffRoles = { "Admin", "Principal", "Instructor", "SuperUser" };

    /// <summary>Recipients of the weekly new-course digest: active members of one
    /// organisation who hold the Student platform role — which every organisation's
    /// Trainee company role maps to, so custom role names are covered automatically —
    /// and who hold NO staff role. Deliberately narrower than "everyone with the learner
    /// role", because staff accounts are commonly given it as well.</summary>
    public static async Task<List<ApplicationUser>> DigestRecipientsAsync(AppDbContext db, int orgId, CancellationToken ct)
    {
        var studentRoleId = await db.Roles.Where(r => r.Name == "Student").Select(r => r.Id).FirstOrDefaultAsync(ct);
        if (studentRoleId == null) return new List<ApplicationUser>();

        var staffRoleIds = await db.Roles.Where(r => StaffRoles.Contains(r.Name!))
            .Select(r => r.Id).ToListAsync(ct);

        return await db.Users.IgnoreQueryFilters()
            .Where(u => u.OrganisationId == orgId && u.IsActive && u.Email != null && u.Email != "")
            .Where(u => db.UserRoles.Any(ur => ur.UserId == u.Id && ur.RoleId == studentRoleId))
            .Where(u => !db.UserRoles.Any(ur => ur.UserId == u.Id && staffRoleIds.Contains(ur.RoleId)))
            .ToListAsync(ct);
    }

    public static string IsoWeek(DateTime d) =>
        $"{ISOWeek.GetYear(d)}-W{ISOWeek.GetWeekOfYear(d):00}";
}

/// <summary>Weekly digest of newly published courses (§NOT-06).
///
/// Runs once per ISO week per organisation, on the configured day and hour. Each
/// active learner — excluding staff, who see the catalogue in the application — is sent
/// the courses published in the last seven days that they are not already enrolled in; a learner with nothing new receives nothing, so the mail
/// only ever arrives when it has something to say. The week number is part of the
/// dedupe key, so a restart — or a second instance — cannot send the digest twice.</summary>
public class NewCourseDigestJob : IEmailJob
{
    public const DayOfWeek SendOn = DayOfWeek.Monday;
    public const int SendAtHour = 8;                        // local server time

    private readonly AppDbContext db;
    private readonly EmailQueue queue;
    private readonly MailSettingsStore store;
    private readonly ILogger<NewCourseDigestJob> _log;
    public NewCourseDigestJob(AppDbContext db, EmailQueue queue, MailSettingsStore store, ILogger<NewCourseDigestJob> log)
    { this.db = db; this.queue = queue; this.store = store; _log = log; }

    public async Task<int> RunAsync(CancellationToken ct, bool ignoreSchedule = false)
    {
        var now = DateTime.Now;
        if (!ignoreSchedule && (now.DayOfWeek != SendOn || now.Hour < SendAtHour)) return 0;

        var settings = await store.LoadAsync();
        if (!settings.IsUsable) return 0;

        var week = NotifierHelpers.IsoWeek(now);
        var since = DateTime.UtcNow.AddDays(-7);
        var queued = 0;

        var orgs = await db.Organisations.IgnoreQueryFilters().Where(o => o.IsActive).ToListAsync(ct);
        foreach (var org in orgs)
        {
            // Newly published courses visible to this tenant: its own, plus shared ones.
            var fresh = await db.Courses.IgnoreQueryFilters()
                .Where(c => c.IsPublished && c.IsActive
                            && (c.OrganisationId == org.Id || c.OrganisationId == null)
                            && c.CreatedAt >= since)
                .OrderBy(c => c.Title).ToListAsync(ct);
            if (fresh.Count == 0) continue;

            foreach (var learner in await NotifierHelpers.DigestRecipientsAsync(db, org.Id, ct))
            {
                var enrolled = await db.Enrollments.IgnoreQueryFilters()
                    .Where(e => e.StudentId == learner.Id).Select(e => e.CourseId).ToListAsync(ct);
                var forThem = fresh.Where(c => !enrolled.Contains(c.Id)).ToList();
                if (forThem.Count == 0) continue;           // nothing new for this person — stay quiet

                var (subject, html) = EmailTemplates.WeeklyNewCourses(org.Name, learner.FullName, forThem, settings.BaseUrl);
                if (await queue.EnqueueAsync(learner.Email!, learner.FullName, subject, html,
                        EmailKind.WeeklyNewCourses, $"weekly:{learner.Id}:{week}", org.Id, ct))
                    queued++;
            }
        }

        if (queued > 0) _log.LogInformation("Weekly new-course digest queued for {Count} learner(s)", queued);
        return queued;
    }
}

/// <summary>Day-before reminder for enrolled training (§NOT-07).
///
/// Covers both things a learner can be due for tomorrow: a course they are enrolled
/// in that starts that day, and any training session scheduled that day on a course
/// they are enrolled in. Everything due is gathered into ONE email per learner per
/// day — three sessions tomorrow is one message, not three — keyed on the date, so
/// repeated ticks and restarts cannot duplicate it.</summary>
public class UpcomingReminderJob : IEmailJob
{
    public const int SendAtHour = 17;                       // late afternoon, the day before

    private readonly AppDbContext db;
    private readonly EmailQueue queue;
    private readonly MailSettingsStore store;
    private readonly ILogger<UpcomingReminderJob> _log;
    public UpcomingReminderJob(AppDbContext db, EmailQueue queue, MailSettingsStore store, ILogger<UpcomingReminderJob> log)
    { this.db = db; this.queue = queue; this.store = store; _log = log; }

    public async Task<int> RunAsync(CancellationToken ct, bool ignoreSchedule = false)
    {
        var now = DateTime.Now;
        if (!ignoreSchedule && now.Hour < SendAtHour) return 0;

        var tomorrow = now.Date.AddDays(1);
        var dayAfter = tomorrow.AddDays(1);

        var settings = await store.LoadAsync();
        if (!settings.IsUsable) return 0;

        // Courses starting tomorrow, and sessions happening tomorrow.
        var startingCourses = await db.Courses.IgnoreQueryFilters()
            .Where(c => c.IsPublished && c.IsActive && c.StartDate >= tomorrow && c.StartDate < dayAfter)
            .ToListAsync(ct);
        var sessions = await db.TrainingSessions.IgnoreQueryFilters()
            .Include(s => s.Course)
            .Where(s => s.Start >= tomorrow && s.Start < dayAfter && s.CourseId != null)
            .ToListAsync(ct);

        var courseIds = startingCourses.Select(c => c.Id)
            .Concat(sessions.Select(s => s.CourseId!.Value)).Distinct().ToList();
        if (courseIds.Count == 0) return 0;

        var enrolments = await db.Enrollments.IgnoreQueryFilters()
            .Include(e => e.Student)
            .Where(e => courseIds.Contains(e.CourseId) && e.Status == EnrollmentStatus.Active)
            .ToListAsync(ct);

        var queued = 0;
        foreach (var group in enrolments.GroupBy(e => e.StudentId))
        {
            var learner = group.First().Student;
            if (learner == null || !learner.IsActive || string.IsNullOrWhiteSpace(learner.Email)) continue;

            var mine = group.Select(e => e.CourseId).ToHashSet();
            var items = new List<string>();
            foreach (var c in startingCourses.Where(c => mine.Contains(c.Id)).OrderBy(c => c.Title))
                items.Add($"{c.Code} — {c.Title} starts tomorrow");
            foreach (var s in sessions.Where(s => mine.Contains(s.CourseId!.Value)).OrderBy(s => s.Start))
                items.Add($"{s.Start:HH:mm} · {s.Title} ({s.Course?.Title}) — {(s.Mode == SessionMode.Online ? "online" : s.Location ?? "on site")}");
            if (items.Count == 0) continue;

            var orgId = learner.OrganisationId;
            var orgName = orgId == null ? "" :
                await db.Organisations.IgnoreQueryFilters().Where(o => o.Id == orgId).Select(o => o.Name).FirstOrDefaultAsync(ct) ?? "";

            var (subject, html) = EmailTemplates.UpcomingReminder(orgName, learner.FullName, items, tomorrow, settings.BaseUrl);
            if (await queue.EnqueueAsync(learner.Email!, learner.FullName, subject, html,
                    EmailKind.UpcomingCourseReminder, $"upcoming:{learner.Id}:{tomorrow:yyyy-MM-dd}", orgId, ct))
                queued++;
        }

        if (queued > 0) _log.LogInformation("Day-before reminders queued for {Count} learner(s)", queued);
        return queued;
    }
}


/// <summary>Timer that drives <see cref="NewCourseDigestJob"/>. The job itself decides
/// whether it is due, so the tick interval only affects promptness, never correctness.</summary>
public class WeeklyNewCoursesNotifier : ScheduledEmailWorker<NewCourseDigestJob>
{
    public WeeklyNewCoursesNotifier(IServiceScopeFactory s, ILogger<WeeklyNewCoursesNotifier> l) : base(s, l) { }
}

/// <summary>Timer that drives <see cref="UpcomingReminderJob"/>.</summary>
public class UpcomingCourseReminder : ScheduledEmailWorker<UpcomingReminderJob>
{
    public UpcomingCourseReminder(IServiceScopeFactory s, ILogger<UpcomingCourseReminder> l) : base(s, l) { }
}

/// <summary>Shared half-hourly loop: resolve the scoped job and run it. Both jobs are
/// idempotent through the outbox dedupe key, so an extra tick costs nothing.</summary>
public abstract class ScheduledEmailWorker<TJob> : BackgroundService where TJob : class, IEmailJob
{
    private static readonly TimeSpan Cycle = TimeSpan.FromMinutes(30);
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger _log;
    protected ScheduledEmailWorker(IServiceScopeFactory scopes, ILogger log) { _scopes = scopes; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<TJob>().RunAsync(ct);
            }
            catch (Exception ex) { _log.LogError(ex, "{Job} tick failed", typeof(TJob).Name); }
            try { await Task.Delay(Cycle, ct); } catch (TaskCanceledException) { break; }
        }
    }
}

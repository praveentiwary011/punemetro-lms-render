using System.Text;
using LMS.Web.Data;
using LMS.Web.Models;
using Microsoft.EntityFrameworkCore;

namespace LMS.Web.Services.Email;

/// <summary>Queues messages into <see cref="EmailOutbox"/>. Nothing in the application
/// talks to SMTP directly — callers queue, and <c>EmailDispatchWorker</c> delivers.
///
/// Every enqueue is guarded by the unique DedupeKey: if the same logical message is
/// queued twice (a retried worker tick, a double-submitted form, a restart mid-run)
/// the second insert violates the index and is discarded rather than producing a
/// duplicate in someone's inbox.</summary>
public class EmailQueue
{
    private readonly AppDbContext _db;
    private readonly ILogger<EmailQueue> _log;
    public EmailQueue(AppDbContext db, ILogger<EmailQueue> log) { _db = db; _log = log; }

    /// <summary>Adds a message unless one with the same key already exists.
    /// Returns true when a new row was written.</summary>
    public async Task<bool> EnqueueAsync(string toAddress, string toName, string subject, string html,
        EmailKind kind, string? dedupeKey, int? organisationId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(toAddress)) return false;

        if (dedupeKey != null &&
            await _db.EmailOutbox.AnyAsync(e => e.DedupeKey == dedupeKey, ct))
            return false;

        _db.EmailOutbox.Add(new EmailOutbox
        {
            ToAddress = toAddress.Trim(), ToName = toName ?? "", Subject = subject,
            HtmlBody = html, Kind = kind, DedupeKey = dedupeKey, OrganisationId = organisationId
        });

        try
        {
            await _db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException)
        {
            // Lost a race on the unique index — the message is already queued, which is
            // exactly the outcome we want. Drop our copy so the context stays usable.
            foreach (var entry in _db.ChangeTracker.Entries<EmailOutbox>().ToList())
                if (entry.State == EntityState.Added) entry.State = EntityState.Detached;
            _log.LogDebug("Email already queued for key {Key}", dedupeKey);
            return false;
        }
    }
}

/// <summary>Builds the HTML for each message. Kept deliberately plain — a single
/// centred card, inline styles, no external CSS or images — because mail clients
/// strip stylesheets and block remote content by default.</summary>
public static class EmailTemplates
{
    private static string Shell(string orgName, string heading, string bodyHtml, string? ctaText, string? ctaUrl)
    {
        var cta = (ctaText != null && !string.IsNullOrWhiteSpace(ctaUrl))
            ? $@"<p style=""margin:28px 0 8px""><a href=""{Esc(ctaUrl)}""
                   style=""background:#6576ff;color:#fff;text-decoration:none;padding:11px 22px;
                          border-radius:4px;display:inline-block;font-weight:600"">{Esc(ctaText)}</a></p>"
            : "";

        return $@"<!doctype html><html><body style=""margin:0;padding:24px;background:#f5f6fa;
            font-family:-apple-system,Segoe UI,Roboto,Helvetica,Arial,sans-serif;color:#364a63"">
          <div style=""max-width:600px;margin:0 auto;background:#fff;border-radius:6px;
                      border:1px solid #e5e9f2;padding:28px"">
            <p style=""margin:0 0 4px;font-size:13px;color:#8094ae"">{Esc(orgName)}</p>
            <h2 style=""margin:0 0 16px;font-size:20px;color:#1f2b3a"">{Esc(heading)}</h2>
            {bodyHtml}
            {cta}
            <hr style=""border:0;border-top:1px solid #e5e9f2;margin:26px 0 14px"" />
            <p style=""margin:0;font-size:12px;color:#8094ae"">
              You are receiving this because you have an account on {Esc(orgName)}'s learning platform.
            </p>
          </div></body></html>";
    }

    public static string Esc(string? s) => System.Net.WebUtility.HtmlEncode(s ?? "");

    public static (string Subject, string Html) EnrollmentConfirmed(
        string orgName, string learnerName, Course course, string? baseUrl)
    {
        var body = new StringBuilder();
        body.Append($@"<p style=""margin:0 0 12px"">Hello {Esc(learnerName)},</p>");
        body.Append($@"<p style=""margin:0 0 12px"">You are now enrolled in
            <strong>{Esc(course.Code)} — {Esc(course.Title)}</strong>.</p>");
        if (course.StartDate != null)
            body.Append($@"<p style=""margin:0 0 12px"">It starts on
                <strong>{course.StartDate:dd MMM yyyy}</strong>. We will remind you the day before.</p>");
        if (!string.IsNullOrWhiteSpace(course.Description))
            body.Append($@"<p style=""margin:0 0 12px;color:#526484"">{Esc(Trim(course.Description, 300))}</p>");

        return ($"Enrolled: {course.Title}",
                Shell(orgName, "Enrolment confirmed", body.ToString(),
                      baseUrl == null ? null : "Open the course", Url(baseUrl, $"/Courses/Details/{course.Id}")));
    }

    public static (string Subject, string Html) WeeklyNewCourses(
        string orgName, string learnerName, IReadOnlyList<Course> courses, string? baseUrl)
    {
        var body = new StringBuilder();
        body.Append($@"<p style=""margin:0 0 12px"">Hello {Esc(learnerName)},</p>");
        body.Append($@"<p style=""margin:0 0 16px"">{courses.Count} new
            {(courses.Count == 1 ? "course is" : "courses are")} available to you this week:</p>");
        body.Append(@"<table role=""presentation"" style=""width:100%;border-collapse:collapse"">");
        foreach (var c in courses)
        {
            body.Append($@"<tr><td style=""padding:10px 0;border-top:1px solid #e5e9f2"">
                <strong style=""color:#1f2b3a"">{Esc(c.Code)} — {Esc(c.Title)}</strong>");
            if (!string.IsNullOrWhiteSpace(c.Description))
                body.Append($@"<br /><span style=""font-size:13px;color:#8094ae"">{Esc(Trim(c.Description, 140))}</span>");
            body.Append("</td></tr>");
        }
        body.Append("</table>");

        return ($"{courses.Count} new course{(courses.Count == 1 ? "" : "s")} available",
                Shell(orgName, "New courses this week", body.ToString(),
                      baseUrl == null ? null : "Browse the catalogue", Url(baseUrl, "/Courses/Catalog")));
    }

    public static (string Subject, string Html) UpcomingReminder(
        string orgName, string learnerName, IReadOnlyList<string> items, DateTime day, string? baseUrl)
    {
        var body = new StringBuilder();
        body.Append($@"<p style=""margin:0 0 12px"">Hello {Esc(learnerName)},</p>");
        body.Append($@"<p style=""margin:0 0 16px"">A reminder of what you have scheduled
            <strong>tomorrow, {day:dddd d MMMM yyyy}</strong>:</p><ul style=""padding-left:18px;margin:0"">");
        foreach (var i in items)
            body.Append($@"<li style=""margin-bottom:8px"">{Esc(i)}</li>");
        body.Append("</ul>");

        return ($"Reminder: your training tomorrow ({day:dd MMM})",
                Shell(orgName, "Starting tomorrow", body.ToString(),
                      baseUrl == null ? null : "View my courses", Url(baseUrl, "/Courses/MyCourses?status=ongoing")));
    }

    public static (string Subject, string Html) Test(string orgName)
        => ("LMS test email",
            Shell(orgName, "Mail is configured correctly",
                  @"<p style=""margin:0"">If you can read this, the LMS can reach your mail server
                    and send notifications to your users.</p>", null, null));

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..n].TrimEnd() + "…";
    private static string? Url(string? baseUrl, string path) =>
        string.IsNullOrWhiteSpace(baseUrl) ? null : baseUrl.TrimEnd('/') + path;
}

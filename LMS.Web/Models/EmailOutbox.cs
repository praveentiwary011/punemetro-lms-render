namespace LMS.Web.Models;

/// <summary>What a queued message is for. Kept on the row so the outbox can be
/// filtered and audited by purpose, and so a failure in one kind is easy to spot.</summary>
public enum EmailKind
{
    EnrollmentConfirmed = 0,
    WeeklyNewCourses = 1,
    UpcomingCourseReminder = 2,
    Test = 3
}

/// <summary>A message waiting to be sent, or already sent.
///
/// Mail is queued rather than sent inline for two reasons: a slow or unreachable
/// SMTP server must never delay (or fail) the user action that triggered it — an
/// enrolment succeeds whether or not the confirmation goes out — and a queued row
/// gives retries and a visible failure trail.
///
/// <see cref="DedupeKey"/> carries a UNIQUE index and is the guard against sending
/// the same thing twice: a restart mid-run, an overlapping worker tick or a
/// double-submitted form all collide on the same key and the second insert is
/// discarded. Keys are built per kind, e.g.
/// "enrol:42", "weekly:7:2026-W31", "upcoming:&lt;userId&gt;:2026-07-29".</summary>
public class EmailOutbox
{
    public int Id { get; set; }
    public string ToAddress { get; set; } = "";
    public string ToName { get; set; } = "";
    public string Subject { get; set; } = "";
    public string HtmlBody { get; set; } = "";
    public EmailKind Kind { get; set; }

    /// <summary>Unique per logical message; null is allowed for one-offs (e.g. a test send).</summary>
    public string? DedupeKey { get; set; }

    /// <summary>Tenant the message belongs to — used for branding and for reporting.</summary>
    public int? OrganisationId { get; set; }
    public Organisation? Organisation { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? SentAt { get; set; }
    public int Attempts { get; set; }
    public DateTime? LastAttemptAt { get; set; }
    public string? LastError { get; set; }
}

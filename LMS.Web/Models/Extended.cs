using System.ComponentModel.DataAnnotations;

namespace LMS.Web.Models;

public enum SessionMode { Online = 0, Offline = 1 }

public class TrainingSession
{
    public int Id { get; set; }
    public int? CourseId { get; set; }
    public Course? Course { get; set; }
    [Required, MaxLength(200)]
    public string Title { get; set; } = "";
    public SessionMode Mode { get; set; }
    /// <summary>Meeting link (online) or venue (offline).</summary>
    public string Location { get; set; } = "";
    public string TrainerId { get; set; } = "";
    public ApplicationUser? Trainer { get; set; }
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    public string? Notes { get; set; }
}

/// <summary>A scheduled intake of trainees for a course, managed by trainers/principal/admin.</summary>
public class TrainingBatch
{
    public int Id { get; set; }
    [Required, MaxLength(200)]
    public string Name { get; set; } = "";
    public int CourseId { get; set; }
    public Course? Course { get; set; }
    public string CreatedById { get; set; } = "";
    public ApplicationUser? CreatedBy { get; set; }
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public int MaxIntake { get; set; }
    /// <summary>Training venue/centre, e.g. "Range Hills Training Centre".</summary>
    [MaxLength(200)]
    public string? Location { get; set; }
    /// <summary>Room details within the venue, e.g. "Room 2 · projector · 30 seats".</summary>
    [MaxLength(200)]
    public string? Room { get; set; }
    [MaxLength(1000)]
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public enum DocumentCategory { Policy = 0, Procedure = 1, Manual = 2, Other = 3 }

public class DocumentItem
{
    public int Id { get; set; }
    [Required, MaxLength(200)]
    public string Title { get; set; } = "";
    public DocumentCategory Category { get; set; }
    public string Url { get; set; } = "";
    public string? Description { get; set; }
    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
    public string UploadedById { get; set; } = "";
    public ApplicationUser? UploadedBy { get; set; }
}

public class VideoItem
{
    public int Id { get; set; }
    [Required, MaxLength(200)]
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    [MaxLength(100)]
    public string Topic { get; set; } = "";
    public int DurationMinutes { get; set; }
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
}

public class Faq
{
    public int Id { get; set; }
    [Required, MaxLength(300)]
    public string Question { get; set; } = "";
    public string Answer { get; set; } = "";
    public int Order { get; set; }
}

public class PartnerCourse
{
    public int Id { get; set; }
    [Required, MaxLength(200)]
    public string Title { get; set; } = "";
    [MaxLength(150)]
    public string Provider { get; set; } = "";
    public string Url { get; set; } = "";
    public string? Description { get; set; }
    public int DurationHours { get; set; }
}

public class CourseFeedback
{
    public int Id { get; set; }
    public int CourseId { get; set; }
    public Course? Course { get; set; }
    public string StudentId { get; set; } = "";
    public ApplicationUser? Student { get; set; }
    [Range(1, 5)]
    public int Rating { get; set; }
    public string? Comments { get; set; }
    public DateTime SubmittedAt { get; set; } = DateTime.UtcNow;
}

public enum RetakeStatus { Pending = 0, Approved = 1, Rejected = 2 }

public class RetakeRequest
{
    public int Id { get; set; }
    public int QuizId { get; set; }
    public Quiz? Quiz { get; set; }
    public string StudentId { get; set; } = "";
    public ApplicationUser? Student { get; set; }
    public string Reason { get; set; } = "";
    public RetakeStatus Status { get; set; }
    public string? DecisionNote { get; set; }
    public DateTime RequestedAt { get; set; } = DateTime.UtcNow;
    public DateTime? DecidedAt { get; set; }
}

public enum TicketCategory { TechnicalAssistance = 0, LearningQuery = 1, HRLearningTeam = 2 }
public enum TicketStatus { Open = 0, Answered = 1, Closed = 2 }

public class SupportTicket
{
    public int Id { get; set; }
    public string RaisedById { get; set; } = "";
    public ApplicationUser? RaisedBy { get; set; }
    public TicketCategory Category { get; set; }
    [Required, MaxLength(200)]
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";
    public TicketStatus Status { get; set; }
    public string? Response { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? RespondedAt { get; set; }
}

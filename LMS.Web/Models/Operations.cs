using System.ComponentModel.DataAnnotations;

namespace LMS.Web.Models;

public enum EventType { Course = 0, Assignment = 1, Quiz = 2, Personal = 3, Other = 4 }

public class CalendarEvent
{
    public int Id { get; set; }
    public string? UserId { get; set; }   // owner (null when course-wide)
    public ApplicationUser? User { get; set; }
    public int? CourseId { get; set; }
    public Course? Course { get; set; }
    [Required, MaxLength(200)]
    public string Title { get; set; } = "";
    public DateTime Start { get; set; }
    public DateTime? End { get; set; }
    public EventType Type { get; set; }
}

public class Certificate
{
    public int Id { get; set; }
    public int EnrollmentId { get; set; }
    public Enrollment? Enrollment { get; set; }
    public string SerialNumber { get; set; } = "";
    public DateTime IssuedAt { get; set; } = DateTime.UtcNow;
}

public enum AttendanceStatus { Present = 0, Absent = 1, Late = 2, Excused = 3 }

public class AttendanceRecord
{
    public int Id { get; set; }
    public int CourseId { get; set; }
    public Course? Course { get; set; }
    public string StudentId { get; set; } = "";
    public ApplicationUser? Student { get; set; }
    public DateTime Date { get; set; }
    public AttendanceStatus Status { get; set; }
}

public class AuditLog
{
    public int Id { get; set; }
    public string? UserId { get; set; }
    public string UserName { get; set; } = "";
    public string Action { get; set; } = "";
    public string? Details { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

public class SiteSetting
{
    [Key, MaxLength(100)]
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}

using System.ComponentModel.DataAnnotations;

namespace LMS.Web.Models;

public enum ContentStandard { Scorm12 = 0, Cmi5 = 1 }

/// <summary>An uploaded e-learning content package (SCORM 1.2 zip or cmi5 package).</summary>
public class ContentPackage
{
    public int Id { get; set; }
    [Required, MaxLength(200)]
    public string Title { get; set; } = "";
    public ContentStandard Standard { get; set; }
    /// <summary>Folder under /wwwroot/scorm/ where the package is extracted.</summary>
    public string RootPath { get; set; } = "";
    /// <summary>Relative launch file (SCORM resource href or cmi5 AU url).</summary>
    public string LaunchUrl { get; set; } = "";
    public string UploadedById { get; set; } = "";
    public ApplicationUser? UploadedBy { get; set; }
    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Per-learner SCORM 1.2 runtime (CMI) data for a package.</summary>
public class ScormRuntimeData
{
    public int Id { get; set; }
    public int ContentPackageId { get; set; }
    public ContentPackage? ContentPackage { get; set; }
    public string StudentId { get; set; } = "";
    public ApplicationUser? Student { get; set; }
    public int? LessonId { get; set; }
    /// <summary>Full CMI element map persisted as JSON.</summary>
    public string DataJson { get; set; } = "{}";
    public string CompletionStatus { get; set; } = "not attempted"; // SCORM 1.2 lesson_status
    public double? ScoreRaw { get; set; }
    public int TotalTimeSeconds { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>A stored xAPI statement (the app acts as a minimal LRS).</summary>
public class XapiStatementRecord
{
    [Key, MaxLength(36)]
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string StatementJson { get; set; } = "{}";
    [MaxLength(200)] public string ActorName { get; set; } = "";
    [MaxLength(200)] public string ActorAccount { get; set; } = "";
    [MaxLength(300)] public string Verb { get; set; } = "";
    [MaxLength(400)] public string ActivityId { get; set; } = "";
    [MaxLength(36)] public string? Registration { get; set; }
    public DateTime Stored { get; set; } = DateTime.UtcNow;
}

using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;

namespace LMS.Web.Models;

public class ApplicationUser : IdentityUser
{
    /// <summary>The source system's own identifier, set only by data migration (§MIG-05).
    /// It is what makes a re-run of the same extract reconcile with what it created before
    /// rather than insert a twin, and what lets enrolment rows reference people and courses
    /// by the client's keys instead of guessing on email or title. Null for records created
    /// in the LMS itself. Unique per organisation.</summary>
    [MaxLength(128)]
    public string? ExternalId { get; set; }

    public string FullName { get; set; } = "";
    public string Department { get; set; } = "General";
    public string? Bio { get; set; }
    public string? AvatarUrl { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public bool IsActive { get; set; } = true;
    public int Points { get; set; }
    /// <summary>Uploaded signature image (path under wwwroot) shown on certificates
    /// where this user signs (e.g. as Course Instructor); null = placeholder signature.</summary>
    public string? SignatureUrl { get; set; }
    /// <summary>Tenant the user belongs to; null = platform-level account.</summary>
    public int? OrganisationId { get; set; }
    public Organisation? Organisation { get; set; }

    public ICollection<Enrollment> Enrollments { get; set; } = new List<Enrollment>();
    public ICollection<Course> CoursesTaught { get; set; } = new List<Course>();
}

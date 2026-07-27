using Microsoft.AspNetCore.Identity;

namespace LMS.Web.Models;

public class ApplicationUser : IdentityUser
{
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

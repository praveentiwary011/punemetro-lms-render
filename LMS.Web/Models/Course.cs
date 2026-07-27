using System.ComponentModel.DataAnnotations;

namespace LMS.Web.Models;

public class Category
{
    public int Id { get; set; }
    [Required, MaxLength(100)]
    public string Name { get; set; } = "";
    public ICollection<Course> Courses { get; set; } = new List<Course>();
}

public enum CourseKind { Technical = 0, Leadership = 1, Compliance = 2, Onboarding = 3, RoleSpecific = 4 }

public class Course
{
    public int Id { get; set; }
    public CourseKind Kind { get; set; } = CourseKind.Technical;
    [Required, MaxLength(200)]
    public string Title { get; set; } = "";
    [MaxLength(20)]
    public string Code { get; set; } = "";
    public string Description { get; set; } = "";
    public int? CategoryId { get; set; }
    public Category? Category { get; set; }
    public string InstructorId { get; set; } = "";
    public ApplicationUser? Instructor { get; set; }
    public bool IsPublished { get; set; }
    /// <summary>Deactivated courses are hidden from the catalog and closed to new
    /// enrollments; learners already enrolled keep access. Trainers (own courses)
    /// and the Principal can toggle this.</summary>
    public bool IsActive { get; set; } = true;
    public int MaxEnrollment { get; set; } = 100;
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public double PassingGrade { get; set; } = 60;
    public bool IssuesCertificate { get; set; } = true;
    /// <summary>When true, the course is only marked Completed (and its certificate issued)
    /// after the learner has passed the course's graded quiz(zes)/assignment(s) in addition to
    /// finishing all lessons. When false, completion is based on lessons alone. Set by the
    /// trainer/Principal/Admin at course set-up.</summary>
    public bool RequiresAssessment { get; set; }
    /// <summary>Tenant the course belongs to; null = shared across all organisations.</summary>
    public int? OrganisationId { get; set; }
    public Organisation? Organisation { get; set; }

    public ICollection<Module> Modules { get; set; } = new List<Module>();
    public ICollection<Enrollment> Enrollments { get; set; } = new List<Enrollment>();
    public ICollection<Assignment> Assignments { get; set; } = new List<Assignment>();
    public ICollection<Quiz> Quizzes { get; set; } = new List<Quiz>();
    public ICollection<Announcement> Announcements { get; set; } = new List<Announcement>();
    public ICollection<DiscussionThread> Threads { get; set; } = new List<DiscussionThread>();
}

public class Module
{
    public int Id { get; set; }
    public int CourseId { get; set; }
    public Course? Course { get; set; }
    [Required, MaxLength(200)]
    public string Title { get; set; } = "";
    public int Order { get; set; }
    public ICollection<Lesson> Lessons { get; set; } = new List<Lesson>();
}

public enum LessonType { Text = 0, Video = 1, File = 2, Link = 3, Scorm = 4 }

public class Lesson
{
    public int Id { get; set; }
    public int ModuleId { get; set; }
    public Module? Module { get; set; }
    [Required, MaxLength(200)]
    public string Title { get; set; } = "";
    public LessonType Type { get; set; }
    public string? Content { get; set; }
    public string? Url { get; set; }
    public int Order { get; set; }
    public int DurationMinutes { get; set; } = 10;
    /// <summary>Set when Type == Scorm: the SCORM 1.2 / cmi5 package to launch.</summary>
    public int? ContentPackageId { get; set; }
    public ContentPackage? ContentPackage { get; set; }
}

public enum EnrollmentStatus { Active = 0, Completed = 1, Dropped = 2 }

public class Enrollment
{
    public int Id { get; set; }
    public int CourseId { get; set; }
    public Course? Course { get; set; }
    public string StudentId { get; set; } = "";
    public ApplicationUser? Student { get; set; }
    public DateTime EnrolledAt { get; set; } = DateTime.UtcNow;
    public EnrollmentStatus Status { get; set; }
    public DateTime? CompletedAt { get; set; }
    public double? FinalGrade { get; set; }
}

public class LessonProgress
{
    public int Id { get; set; }
    public int LessonId { get; set; }
    public Lesson? Lesson { get; set; }
    public string StudentId { get; set; } = "";
    public ApplicationUser? Student { get; set; }
    public DateTime CompletedAt { get; set; } = DateTime.UtcNow;
}

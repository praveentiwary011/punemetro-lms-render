using LMS.Web.Models;
using LMS.Web.Services;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace LMS.Web.Data;

public class AppDbContext : IdentityDbContext<ApplicationUser>
{
    private readonly ITenantContext? _tenant;

    public AppDbContext(DbContextOptions<AppDbContext> options, ITenantContext? tenant = null) : base(options)
        => _tenant = tenant;

    /// <summary>True when the current caller may see across all tenants (platform
    /// owner, or trusted server code with no request context). When true, the tenant
    /// query filters are satisfied unconditionally.</summary>
    private bool TenantUnrestricted => _tenant == null || _tenant.Unrestricted;

    /// <summary>Current tenant id (null when unrestricted / unknown). Referenced by the
    /// global query filters; kept null-safe so design-time / filter evaluation never
    /// dereferences a missing tenant context.</summary>
    private int? TenantId => _tenant?.OrganisationId;

    /// <summary>Write-side tenant scope for <see cref="TenantSaveChangesInterceptor"/>.</summary>
    internal (bool Unrestricted, int? TenantId) TenantWriteScope => (TenantUnrestricted, TenantId);

    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Course> Courses => Set<Course>();
    public DbSet<Module> Modules => Set<Module>();
    public DbSet<Lesson> Lessons => Set<Lesson>();
    public DbSet<Enrollment> Enrollments => Set<Enrollment>();
    public DbSet<LessonProgress> LessonProgress => Set<LessonProgress>();
    public DbSet<Assignment> Assignments => Set<Assignment>();
    public DbSet<Submission> Submissions => Set<Submission>();
    public DbSet<Quiz> Quizzes => Set<Quiz>();
    public DbSet<Question> Questions => Set<Question>();
    public DbSet<QuestionOption> QuestionOptions => Set<QuestionOption>();
    public DbSet<QuizAttempt> QuizAttempts => Set<QuizAttempt>();
    public DbSet<QuizAnswer> QuizAnswers => Set<QuizAnswer>();
    public DbSet<Announcement> Announcements => Set<Announcement>();
    public DbSet<DiscussionThread> DiscussionThreads => Set<DiscussionThread>();
    public DbSet<DiscussionPost> DiscussionPosts => Set<DiscussionPost>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<CalendarEvent> CalendarEvents => Set<CalendarEvent>();
    public DbSet<Certificate> Certificates => Set<Certificate>();
    public DbSet<AttendanceRecord> AttendanceRecords => Set<AttendanceRecord>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<SiteSetting> SiteSettings => Set<SiteSetting>();
    public DbSet<TrainingSession> TrainingSessions => Set<TrainingSession>();
    public DbSet<TrainingBatch> TrainingBatches => Set<TrainingBatch>();
    public DbSet<DocumentItem> Documents => Set<DocumentItem>();
    public DbSet<VideoItem> Videos => Set<VideoItem>();
    public DbSet<Faq> Faqs => Set<Faq>();
    public DbSet<PartnerCourse> PartnerCourses => Set<PartnerCourse>();
    public DbSet<CourseFeedback> CourseFeedbacks => Set<CourseFeedback>();
    public DbSet<RetakeRequest> RetakeRequests => Set<RetakeRequest>();
    public DbSet<SupportTicket> SupportTickets => Set<SupportTicket>();
    public DbSet<ContentPackage> ContentPackages => Set<ContentPackage>();
    public DbSet<ScormRuntimeData> ScormRuntimeData => Set<ScormRuntimeData>();
    public DbSet<XapiStatementRecord> XapiStatements => Set<XapiStatementRecord>();
    public DbSet<Organisation> Organisations => Set<Organisation>();
    public DbSet<OrganisationRole> OrganisationRoles => Set<OrganisationRole>();
    public DbSet<TrainingLocation> TrainingLocations => Set<TrainingLocation>();
    public DbSet<SubscriptionLicense> SubscriptionLicenses => Set<SubscriptionLicense>();
    public DbSet<SsoConfiguration> SsoConfigurations => Set<SsoConfiguration>();
    public DbSet<CourseEditRequest> CourseEditRequests => Set<CourseEditRequest>();
    public DbSet<KnowledgeChunk> KnowledgeChunks => Set<KnowledgeChunk>();
    public DbSet<SubjectiveGradeResult> SubjectiveGradeResults => Set<SubjectiveGradeResult>();
    public DbSet<SubjectiveGradingReview> SubjectiveGradingReviews => Set<SubjectiveGradingReview>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<Organisation>()
            .HasIndex(o => o.Code)
            .IsUnique();

        builder.Entity<Organisation>()
            .HasOne(o => o.CertificateSignatory)
            .WithMany()
            .HasForeignKey(o => o.CertificateSignatoryId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.Entity<SubscriptionLicense>()
            .HasOne(l => l.Organisation)
            .WithMany(o => o.Licenses)
            .HasForeignKey(l => l.OrganisationId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<SubscriptionLicense>()
            .HasOne(l => l.CreatedBy)
            .WithMany()
            .HasForeignKey(l => l.CreatedById)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Entity<SubscriptionLicense>()
            .HasIndex(l => new { l.OrganisationId, l.EndDate });

        builder.Entity<OrganisationRole>()
            .HasIndex(r => new { r.OrganisationId, r.Name })
            .IsUnique();

        builder.Entity<ApplicationUser>()
            .HasOne(u => u.Organisation)
            .WithMany(o => o.Users)
            .HasForeignKey(u => u.OrganisationId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Entity<Course>()
            .HasOne(c => c.Organisation)
            .WithMany(o => o.Courses)
            .HasForeignKey(c => c.OrganisationId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Entity<Course>()
            .HasOne(c => c.Instructor)
            .WithMany(u => u.CoursesTaught)
            .HasForeignKey(c => c.InstructorId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Entity<Enrollment>()
            .HasOne(e => e.Student)
            .WithMany(u => u.Enrollments)
            .HasForeignKey(e => e.StudentId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<Enrollment>()
            .HasIndex(e => new { e.CourseId, e.StudentId })
            .IsUnique();

        builder.Entity<LessonProgress>()
            .HasIndex(p => new { p.LessonId, p.StudentId })
            .IsUnique();

        builder.Entity<Message>()
            .HasOne(m => m.Sender)
            .WithMany()
            .HasForeignKey(m => m.SenderId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Entity<Message>()
            .HasOne(m => m.Recipient)
            .WithMany()
            .HasForeignKey(m => m.RecipientId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Entity<DiscussionPost>()
            .HasOne(p => p.Thread)
            .WithMany(t => t.Posts)
            .HasForeignKey(p => p.ThreadId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<QuizAnswer>()
            .HasOne(a => a.Question)
            .WithMany()
            .HasForeignKey(a => a.QuestionId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Entity<AttendanceRecord>()
            .HasIndex(a => new { a.CourseId, a.StudentId, a.Date })
            .IsUnique();

        builder.Entity<ScormRuntimeData>()
            .HasIndex(s => new { s.ContentPackageId, s.StudentId })
            .IsUnique();

        // ---- Single sign-on (§AUTH-09) ----
        builder.Entity<SsoConfiguration>()
            .HasOne(s => s.Organisation).WithMany()
            .HasForeignKey(s => s.OrganisationId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<SsoConfiguration>()
            .HasIndex(s => s.OrganisationId).IsUnique();

        // ---- Delegated course editing (§CRS-11) ----
        builder.Entity<CourseEditRequest>()
            .HasOne(r => r.Course).WithMany()
            .HasForeignKey(r => r.CourseId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<CourseEditRequest>()
            .HasOne(r => r.Trainer).WithMany()
            .HasForeignKey(r => r.TrainerId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<CourseEditRequest>()
            .HasOne(r => r.DecidedBy).WithMany()
            .HasForeignKey(r => r.DecidedById).OnDelete(DeleteBehavior.Restrict);
        // The permission lookup on every course-edit action filters by all three.
        builder.Entity<CourseEditRequest>()
            .HasIndex(r => new { r.CourseId, r.TrainerId, r.Status });

        // ---- Subjective auto-grading (§AIG) ----
        builder.Entity<KnowledgeChunk>()
            .HasOne(k => k.Organisation).WithMany()
            .HasForeignKey(k => k.OrganisationId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<KnowledgeChunk>()
            .HasOne(k => k.Course).WithMany()
            .HasForeignKey(k => k.CourseId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<KnowledgeChunk>()
            .HasIndex(k => new { k.OrganisationId, k.CourseId });

        builder.Entity<SubjectiveGradeResult>()
            .HasOne(r => r.QuizAnswer).WithMany()
            .HasForeignKey(r => r.QuizAnswerId).OnDelete(DeleteBehavior.Cascade);

        builder.Entity<SubjectiveGradingReview>()
            .HasOne(r => r.QuizAnswer).WithMany()
            .HasForeignKey(r => r.QuizAnswerId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<SubjectiveGradingReview>()
            .HasOne(r => r.Reviewer).WithMany()
            .HasForeignKey(r => r.ReviewerUserId).OnDelete(DeleteBehavior.Restrict);

        // ---- Multi-tenant global query filters (default-deny isolation) ----
        // Every ordinary LINQ read of a tenant-owned entity is automatically scoped
        // to the caller's organisation. The platform owner (Super User) and trusted
        // server code run unrestricted; crossing tenants for them is an explicit
        // IgnoreQueryFilters() at the call site. NOTE: Identity's FindByIdAsync uses
        // DbSet.FindAsync, which BYPASSES these filters — by-id user administration
        // therefore keeps its own explicit same-tenant checks (see AdminController).
        builder.Entity<Organisation>()
            .HasQueryFilter(o => TenantUnrestricted || o.Id == TenantId);
        builder.Entity<ApplicationUser>()
            .HasQueryFilter(u => TenantUnrestricted || u.OrganisationId == TenantId);
        // Courses may be tenant-owned or shared (null organisation = platform-wide).
        builder.Entity<Course>()
            .HasQueryFilter(c => TenantUnrestricted || c.OrganisationId == TenantId || c.OrganisationId == null);
        builder.Entity<OrganisationRole>()
            .HasQueryFilter(r => TenantUnrestricted || r.OrganisationId == TenantId);
        builder.Entity<TrainingLocation>()
            .HasQueryFilter(l => TenantUnrestricted || l.OrganisationId == TenantId);
        builder.Entity<SubscriptionLicense>()
            .HasQueryFilter(s => TenantUnrestricted || s.OrganisationId == TenantId);
        builder.Entity<KnowledgeChunk>()
            .HasQueryFilter(k => TenantUnrestricted || k.OrganisationId == TenantId);
    }
}

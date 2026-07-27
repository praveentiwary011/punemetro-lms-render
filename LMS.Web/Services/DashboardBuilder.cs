using LMS.Web.Data;
using LMS.Web.Models;
using Microsoft.EntityFrameworkCore;

namespace LMS.Web.Services;

public class NameCountRow { public string Name { get; set; } = ""; public int Count { get; set; } }
public class TopCourseRow
{
    public string Code { get; set; } = "";
    public string Title { get; set; } = "";
    public int Enrolled { get; set; }
    public int Completed { get; set; }
    public double AvgRating { get; set; }
}
public class PopularCourseRow
{
    public int Id { get; set; }
    public string Code { get; set; } = "";
    public string Title { get; set; } = "";
    public string Instructor { get; set; } = "";
    public int Enrolled { get; set; }
}

public class TopInstructorRow
{
    public string Name { get; set; } = "";
    public int Courses { get; set; }
    public int Students { get; set; }
    public double AvgRating { get; set; }
}

public class AdminDashboardVm
{
    public int Users { get; set; }
    public int Courses { get; set; }
    public int Enrollments { get; set; }
    public int Completions { get; set; }
    public int PendingGrading { get; set; }
    public int ActiveTrainees { get; set; }
    public int CertificatesIssued { get; set; }
    public int SessionsOnline { get; set; }
    public int SessionsOffline { get; set; }
    public List<NameCountRow> EnrollTrend { get; set; } = new();
    public List<NameCountRow> CompletionTrend { get; set; } = new();
    public List<TopCourseRow> TopCourses { get; set; } = new();
    public List<TopInstructorRow> TopInstructors { get; set; } = new();
    public List<NameCountRow> TopCategories { get; set; } = new();
    public bool ShowAudit { get; set; }
    public List<AuditLog> RecentAudit { get; set; } = new();
    public List<ApplicationUser> RecentUsers { get; set; } = new();
}

public static class DashboardBuilder
{
    public static async Task<AdminDashboardVm> BuildAdminAsync(AppDbContext db, bool showAudit)
    {
        var vm = new AdminDashboardVm { ShowAudit = showAudit };

        vm.Users = await db.Users.CountAsync();
        vm.Courses = await db.Courses.CountAsync();
        vm.Enrollments = await db.Enrollments.CountAsync();
        vm.Completions = await db.Enrollments.CountAsync(e => e.Status == EnrollmentStatus.Completed);
        vm.PendingGrading = await db.Submissions.CountAsync(s => s.Grade == null);
        vm.CertificatesIssued = await db.Certificates.CountAsync();
        vm.SessionsOnline = await db.TrainingSessions.CountAsync(s => s.Mode == SessionMode.Online);
        vm.SessionsOffline = await db.TrainingSessions.CountAsync(s => s.Mode == SessionMode.Offline);

        var cutoff = DateTime.UtcNow.AddDays(-30);
        vm.ActiveTrainees = await db.LessonProgress
            .Where(p => p.CompletedAt >= cutoff)
            .Select(p => p.StudentId).Distinct().CountAsync();

        // Enrollment / completion trend: last 6 months
        var enrolledDates = await db.Enrollments.Select(e => e.EnrolledAt).ToListAsync();
        var completedDates = await db.Enrollments.Where(e => e.CompletedAt != null).Select(e => e.CompletedAt!.Value).ToListAsync();
        for (int i = 5; i >= 0; i--)
        {
            var month = DateTime.UtcNow.AddMonths(-i);
            var label = month.ToString("MMM");
            vm.EnrollTrend.Add(new NameCountRow { Name = label, Count = enrolledDates.Count(d => d.Year == month.Year && d.Month == month.Month) });
            vm.CompletionTrend.Add(new NameCountRow { Name = label, Count = completedDates.Count(d => d.Year == month.Year && d.Month == month.Month) });
        }

        var feedback = await db.CourseFeedbacks.AsNoTracking().ToListAsync();

        vm.TopCourses = (await db.Courses.AsNoTracking()
                .Include(c => c.Enrollments)
                .ToListAsync())
            .Select(c => new TopCourseRow
            {
                Code = c.Code, Title = c.Title,
                Enrolled = c.Enrollments.Count,
                Completed = c.Enrollments.Count(e => e.Status == EnrollmentStatus.Completed),
                AvgRating = feedback.Where(f => f.CourseId == c.Id).Select(f => (double)f.Rating).DefaultIfEmpty(0).Average()
            })
            .OrderByDescending(c => c.Enrolled).Take(5).ToList();

        vm.TopInstructors = (await db.Courses.AsNoTracking()
                .Include(c => c.Instructor).Include(c => c.Enrollments)
                .ToListAsync())
            .GroupBy(c => c.Instructor?.FullName ?? "—")
            .Select(g => new TopInstructorRow
            {
                Name = g.Key,
                Courses = g.Count(),
                Students = g.SelectMany(c => c.Enrollments.Select(e => e.StudentId)).Distinct().Count(),
                AvgRating = g.SelectMany(c => feedback.Where(f => f.CourseId == c.Id)).Select(f => (double)f.Rating).DefaultIfEmpty(0).Average()
            })
            .OrderByDescending(i => i.Students).Take(5).ToList();

        vm.TopCategories = (await db.Courses.AsNoTracking().Include(c => c.Category).Include(c => c.Enrollments).ToListAsync())
            .GroupBy(c => c.Category?.Name ?? "Uncategorised")
            .Select(g => new NameCountRow { Name = g.Key, Count = g.Sum(c => c.Enrollments.Count) })
            .OrderByDescending(x => x.Count).Take(5).ToList();

        if (showAudit)
        {
            vm.RecentAudit = await db.AuditLogs.OrderByDescending(a => a.Timestamp).Take(6).ToListAsync();
            vm.RecentUsers = await db.Users.OrderByDescending(u => u.CreatedAt).Take(5).ToListAsync();
        }
        return vm;
    }
}

using System.Security.Claims;
using LMS.Web.Data;
using LMS.Web.Models;
using Microsoft.EntityFrameworkCore;

namespace LMS.Web.Services;

/// <summary>Who may edit, update and unpublish a given course (§CRS-11).
///
/// Three ways to qualify:
///   • Admin and Principal — oversight of every course in the organisation;
///   • the course's own author (trainer);
///   • a trainer holding an <b>approved</b> <see cref="CourseEditRequest"/> for that
///     specific course, granted by an Admin or Principal and revocable at any time.
///
/// Both forms are offered so authorisation is decided in one place: a composable
/// query filter for listings, and a single-course check for actions.</summary>
public static class CourseAccess
{
    public static bool IsOversight(ClaimsPrincipal user) =>
        user.IsInRole("Admin") || user.IsInRole("Principal");

    /// <summary>Courses the caller may edit, as a query. Kept as an EF subquery rather
    /// than a pre-fetched id list so it composes with the existing course queries and
    /// stays a single round trip.</summary>
    public static IQueryable<Course> Editable(AppDbContext db, ClaimsPrincipal user)
    {
        if (IsOversight(user)) return db.Courses;
        var uid = user.GetUserId();
        return db.Courses.Where(c =>
            c.InstructorId == uid ||
            db.CourseEditRequests.Any(r =>
                r.CourseId == c.Id && r.TrainerId == uid &&
                r.Status == CourseEditAccessStatus.Approved));
    }

    /// <summary>Whether the caller may edit one already-loaded course.</summary>
    public static async Task<bool> CanEditAsync(AppDbContext db, ClaimsPrincipal user, Course course)
    {
        if (IsOversight(user)) return true;
        var uid = user.GetUserId();
        if (course.InstructorId == uid) return true;
        return await db.CourseEditRequests.AnyAsync(r =>
            r.CourseId == course.Id && r.TrainerId == uid &&
            r.Status == CourseEditAccessStatus.Approved);
    }

    /// <summary>Course ids the trainer holds an approved grant for — used to badge the
    /// All Courses listing without a query per row.</summary>
    public static Task<List<int>> GrantedCourseIdsAsync(AppDbContext db, string userId) =>
        db.CourseEditRequests
            .Where(r => r.TrainerId == userId && r.Status == CourseEditAccessStatus.Approved)
            .Select(r => r.CourseId).ToListAsync();

    /// <summary>Course ids with a request from this trainer still awaiting a decision,
    /// so the listing can show "requested" instead of offering the button again.</summary>
    public static Task<List<int>> PendingCourseIdsAsync(AppDbContext db, string userId) =>
        db.CourseEditRequests
            .Where(r => r.TrainerId == userId && r.Status == CourseEditAccessStatus.Pending)
            .Select(r => r.CourseId).ToListAsync();
}

using LMS.Web.Data;
using LMS.Web.Models;
using Microsoft.EntityFrameworkCore;

namespace LMS.Web.Services;

/// <summary>
/// Marks an enrollment Completed once the learner has both (a) finished every lesson of
/// the course — the 100%-content figure the progress bars show — and (b) passed all of the
/// course's graded assessments: every published, non-self-assessment quiz (best attempt ≥
/// the quiz's passing score) and every assignment (graded submission ≥ the course passing
/// grade). A course with no lessons is not auto-completed here (it completes via the
/// instructor's final-grade action). It stamps CompletedAt and issues a certificate when
/// the course grants one. Idempotent and safe to call after any lesson-progress, quiz-submit
/// or assignment-grade event; the caller persists via SaveChanges (save the triggering row
/// first so it is counted).
/// </summary>
public static class CourseCompletion
{
    /// <summary>Returns true if this call transitioned the enrollment to Completed.</summary>
    public static async Task<bool> CheckAsync(AppDbContext db, int courseId, string studentId)
    {
        var enrollment = await db.Enrollments
            .FirstOrDefaultAsync(e => e.CourseId == courseId && e.StudentId == studentId);
        if (enrollment == null || enrollment.Status != EnrollmentStatus.Active) return false;

        var course = await db.Courses.FindAsync(courseId);
        if (course == null) return false;

        var lessonIds = await db.Lessons
            .Where(l => l.Module!.CourseId == courseId)
            .Select(l => l.Id).ToListAsync();
        if (lessonIds.Count == 0) return false;   // nothing to complete against

        var done = await db.LessonProgress
            .CountAsync(p => p.StudentId == studentId && lessonIds.Contains(p.LessonId));
        if (done < lessonIds.Count) return false; // not yet 100% content

        // When the course is flagged as requiring assessment, the learner must also have
        // passed its graded quiz(zes)/assignment(s) before it counts as complete.
        if (course.RequiresAssessment && !await AssessmentsPassedAsync(db, course, studentId))
            return false;

        enrollment.Status = EnrollmentStatus.Completed;
        enrollment.CompletedAt = DateTime.UtcNow;

        if (course.IssuesCertificate &&
            !await db.Certificates.AnyAsync(c => c.EnrollmentId == enrollment.Id))
        {
            db.Certificates.Add(new Certificate
            {
                EnrollmentId = enrollment.Id,
                SerialNumber = $"CERT-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString()[..8].ToUpper()}"
            });
            Notifier.Notify(db, studentId,
                $"Congratulations! You've completed {course.Title} and earned a certificate.", "/Certificates");
        }
        else
        {
            Notifier.Notify(db, studentId,
                $"You've completed {course.Title}. Well done!", "/Courses/MyCourses?status=completed");
        }
        return true;
    }

    /// <summary>True when the learner has passed every graded assessment of the course:
    /// each published, non-self-assessment quiz (best attempt ≥ its passing score) and each
    /// assignment (a graded submission scoring ≥ the course passing grade). Courses with no
    /// such assessments pass this trivially.</summary>
    public static async Task<bool> AssessmentsPassedAsync(AppDbContext db, Course course, string studentId)
    {
        var quizzes = await db.Quizzes
            .Where(q => q.CourseId == course.Id && q.IsPublished && !q.IsSelfAssessment)
            .Select(q => new { q.Id, q.PassingScore }).ToListAsync();
        foreach (var q in quizzes)
        {
            var passed = await db.QuizAttempts.AnyAsync(a =>
                a.QuizId == q.Id && a.StudentId == studentId && a.SubmittedAt != null &&
                a.MaxScore > 0 && a.Score / a.MaxScore * 100 >= q.PassingScore);
            if (!passed) return false;
        }

        var assignments = await db.Assignments
            .Where(a => a.CourseId == course.Id)
            .Select(a => new { a.Id, a.MaxPoints }).ToListAsync();
        foreach (var a in assignments)
        {
            var passed = await db.Submissions.AnyAsync(s =>
                s.AssignmentId == a.Id && s.StudentId == studentId && s.Grade != null &&
                (a.MaxPoints <= 0 || s.Grade!.Value / a.MaxPoints * 100 >= course.PassingGrade));
            if (!passed) return false;
        }
        return true;
    }

    /// <summary>Convenience overload that resolves the course from a lesson id.</summary>
    public static async Task<bool> CheckByLessonAsync(AppDbContext db, int lessonId, string studentId)
    {
        var courseId = await db.Lessons.Where(l => l.Id == lessonId)
            .Select(l => (int?)l.Module!.CourseId).FirstOrDefaultAsync();
        return courseId != null && await CheckAsync(db, courseId.Value, studentId);
    }
}

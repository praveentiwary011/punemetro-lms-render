using LMS.Web.Data;
using LMS.Web.Models;
using Microsoft.EntityFrameworkCore;

namespace LMS.Web.Services.Grading;

/// <summary>Orchestrates subjective grading for an attempt (§AIG): retrieves context, grades
/// each pending subjective answer, writes the audit result, decides auto-apply vs. trainer-review
/// hold, recomputes the attempt score, and re-runs the completion gate (LSN-04) when finalised.</summary>
public class GradingService
{
    private readonly AppDbContext _db;
    private readonly RetrievalService _retrieval;
    private readonly ISubjectiveGrader _grader;
    private readonly ILogger<GradingService> _log;

    // Attempts whose total lands within this margin of the pass line are sent to review.
    private const double BoundaryMarginPct = 5.0;

    public GradingService(AppDbContext db, RetrievalService retrieval, ISubjectiveGrader grader, ILogger<GradingService> log)
    { _db = db; _retrieval = retrieval; _grader = grader; _log = log; }

    /// <summary>Grade every still-pending subjective answer on an attempt. Safe to re-run
    /// (worker retry); answers that failed with an outage stay pending for the next pass.</summary>
    public async Task GradeAttemptPendingAsync(int attemptId, CancellationToken ct = default)
    {
        var attempt = await _db.QuizAttempts.IgnoreQueryFilters()
            .Include(a => a.Answers)
            .FirstOrDefaultAsync(a => a.Id == attemptId, ct);
        if (attempt == null) return;

        var quiz = await _db.Quizzes.IgnoreQueryFilters()
            .Include(q => q.Course)
            .FirstOrDefaultAsync(q => q.Id == attempt.QuizId, ct);
        if (quiz?.Course == null) return;
        var orgId = quiz.Course.OrganisationId;
        var passing = quiz.PassingScore;

        var questionIds = attempt.Answers.Select(a => a.QuestionId).ToList();
        var questions = await _db.Questions.IgnoreQueryFilters()
            .Where(q => questionIds.Contains(q.Id)).ToDictionaryAsync(q => q.Id, ct);

        bool anyStillPending = false;
        foreach (var answer in attempt.Answers.Where(a => a.GradingPending))
        {
            if (!questions.TryGetValue(answer.QuestionId, out var q) || q.Type != QuestionType.Subjective)
            { answer.GradingPending = false; continue; }

            try
            {
                var context = orgId != null
                    ? await _retrieval.RetrieveContextAsync(q.Text, answer.TextAnswer ?? "", orgId.Value, quiz.CourseId, 5, ct)
                    : (IReadOnlyList<RetrievedChunk>)System.Array.Empty<RetrievedChunk>();

                var outcome = await _grader.GradeAsync(q, answer.TextAnswer ?? "", context, ct);

                _db.SubjectiveGradeResults.Add(new SubjectiveGradeResult
                {
                    QuizAnswerId = answer.Id, Score = outcome.Score, MaxScore = outcome.MaxScore,
                    Verdict = outcome.Verdict, Confidence = outcome.Confidence, Feedback = outcome.Feedback,
                    Citations = string.Join("; ", outcome.Citations), Model = outcome.Model, Mode = outcome.Mode,
                    RetrievedChunkLabels = string.Join("; ", context.Select(c => c.SourceLabel).Distinct()),
                    RawResult = outcome.RawJson, NeedsReview = outcome.NeedsReview
                });

                bool review = outcome.NeedsReview || outcome.Confidence != GradeConfidence.High
                              || NearBoundary(attempt, answer, outcome.Score, passing);

                answer.GradingPending = false;
                if (review)
                {
                    answer.PointsAwarded = 0; // not awarded until a trainer confirms (§AIG-07)
                    _db.SubjectiveGradingReviews.Add(new SubjectiveGradingReview
                    { QuizAnswerId = answer.Id, ProposedScore = outcome.Score, Status = GradingReviewStatus.Pending });
                }
                else
                {
                    answer.PointsAwarded = outcome.Score;
                }
            }
            catch (GradingUnavailableException ex)
            {
                anyStillPending = true; // keep pending; worker retries (§AIG-09)
                _log.LogWarning(ex, "Grading deferred for answer {AnswerId} (Ollama unavailable).", answer.Id);
            }
        }

        attempt.Score = attempt.Answers.Sum(a => a.PointsAwarded);
        await _db.SaveChangesAsync(ct);

        await MaybeFinaliseAsync(attempt.Id, quiz.CourseId, attempt.StudentId, anyStillPending, ct);
    }

    /// <summary>Trainer confirms/overrides a held subjective result (§AIG-07); finalises the
    /// attempt and runs the completion gate when nothing else is outstanding.</summary>
    public async Task ResolveReviewAsync(int reviewId, double resolvedScore, string? feedback, string reviewerUserId, CancellationToken ct = default)
    {
        var review = await _db.SubjectiveGradingReviews.IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.Id == reviewId && r.Status == GradingReviewStatus.Pending, ct);
        if (review == null) return;

        var answer = await _db.QuizAnswers.IgnoreQueryFilters().FirstOrDefaultAsync(a => a.Id == review.QuizAnswerId, ct);
        if (answer == null) return;

        review.ResolvedScore = resolvedScore;
        review.TrainerFeedback = feedback;
        review.ReviewerUserId = reviewerUserId;
        review.Status = GradingReviewStatus.Resolved;
        review.ResolvedAt = DateTime.UtcNow;
        answer.PointsAwarded = resolvedScore;

        var attempt = await _db.QuizAttempts.IgnoreQueryFilters()
            .Include(a => a.Answers).FirstOrDefaultAsync(a => a.Id == answer.QuizAttemptId, ct);
        if (attempt != null) attempt.Score = attempt.Answers.Sum(a => a.PointsAwarded);
        await _db.SaveChangesAsync(ct);

        if (attempt != null)
        {
            var quiz = await _db.Quizzes.IgnoreQueryFilters().FirstOrDefaultAsync(q => q.Id == attempt.QuizId, ct);
            if (quiz != null) await MaybeFinaliseAsync(attempt.Id, quiz.CourseId, attempt.StudentId, false, ct);
        }
    }

    /// <summary>Distinct attempt ids that still have ungraded subjective answers (worker queue).</summary>
    public async Task<List<int>> PendingAttemptIdsAsync(int max, CancellationToken ct = default) =>
        await _db.QuizAnswers.IgnoreQueryFilters()
            .Where(a => a.GradingPending)
            .Select(a => a.QuizAttemptId).Distinct().Take(max).ToListAsync(ct);

    private async Task MaybeFinaliseAsync(int attemptId, int courseId, string studentId, bool anyStillPending, CancellationToken ct)
    {
        if (anyStillPending) return;
        var answerIds = await _db.QuizAnswers.IgnoreQueryFilters()
            .Where(a => a.QuizAttemptId == attemptId).Select(a => a.Id).ToListAsync(ct);
        bool hasPending = await _db.QuizAnswers.IgnoreQueryFilters().AnyAsync(a => a.QuizAttemptId == attemptId && a.GradingPending, ct);
        bool hasOpenReview = await _db.SubjectiveGradingReviews.IgnoreQueryFilters()
            .AnyAsync(r => answerIds.Contains(r.QuizAnswerId) && r.Status == GradingReviewStatus.Pending, ct);
        if (hasPending || hasOpenReview) return;

        if (await CourseCompletion.CheckAsync(_db, courseId, studentId))
            await _db.SaveChangesAsync(ct);
    }

    private static bool NearBoundary(QuizAttempt attempt, QuizAnswer thisAnswer, double proposed, double passing)
    {
        if (attempt.MaxScore <= 0) return false;
        var tentative = attempt.Answers.Where(a => a.Id != thisAnswer.Id).Sum(a => a.PointsAwarded) + proposed;
        var pct = tentative / attempt.MaxScore * 100.0;
        return Math.Abs(pct - passing) <= BoundaryMarginPct;
    }
}

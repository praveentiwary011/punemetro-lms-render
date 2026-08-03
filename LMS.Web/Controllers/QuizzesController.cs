using LMS.Web.Data;
using LMS.Web.Models;
using LMS.Web.Services;
using LMS.Web.Services.Grading;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LMS.Web.Controllers;

[Authorize]
public class QuizzesController : Controller
{
    private readonly AppDbContext _db;
    private readonly GradingOptions _gradingOptions;
    private readonly GradingService _grading;
    private readonly QuizGenerator _generator;
    public QuizzesController(AppDbContext db, GradingOptions gradingOptions, GradingService grading, QuizGenerator generator)
    { _db = db; _gradingOptions = gradingOptions; _grading = grading; _generator = generator; }

    /// <summary>Authoring rights over a course's assessments: Admin/Principal, the course's
    /// own trainer, or a trainer holding an approved edit grant for it (§CRS-11).</summary>
    private Task<bool> OwnsCourseAsync(Course course) => CourseAccess.CanEditAsync(_db, User, course);

    /// <summary>Base attempts plus one per approved retake request.</summary>
    private async Task<int> AllowedAttemptsAsync(Quiz quiz, string studentId)
    {
        var approvedRetakes = await _db.RetakeRequests.CountAsync(r =>
            r.QuizId == quiz.Id && r.StudentId == studentId && r.Status == RetakeStatus.Approved);
        return quiz.MaxAttempts + approvedRetakes;
    }

    // ---------- Authoring ----------
    [Authorize(Roles = "Instructor,Admin,Principal")]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(int courseId, string title, string description, int timeLimitMinutes, int maxAttempts, double passingScore, DateTime? dueDate)
    {
        var course = await _db.Courses.FirstOrDefaultAsync(c => c.Id == courseId);
        if (course == null || !await OwnsCourseAsync(course)) return NotFound();
        var quiz = new Quiz
        {
            CourseId = courseId, Title = title, Description = description ?? "",
            TimeLimitMinutes = timeLimitMinutes <= 0 ? 30 : timeLimitMinutes,
            MaxAttempts = maxAttempts <= 0 ? 1 : maxAttempts,
            PassingScore = passingScore, DueDate = dueDate,
            // Starts offline: a quiz must never be sat before its questions exist.
            // The trainer publishes it from the editor when the paper is ready.
            IsPublished = false
        };
        _db.Quizzes.Add(quiz);
        await _db.SaveChangesAsync();
        return RedirectToAction("Edit", new { id = quiz.Id });
    }

    [Authorize(Roles = "Instructor,Admin,Principal")]
    public async Task<IActionResult> Edit(int id)
    {
        var quiz = await _db.Quizzes
            .Include(q => q.Course)
            .Include(q => q.Questions.OrderBy(x => x.Order)).ThenInclude(x => x.Options)
            .FirstOrDefaultAsync(q => q.Id == id);
        if (quiz == null || !await OwnsCourseAsync(quiz.Course!)) return NotFound();
        return View(quiz);
    }

    /// <summary>Take a single assessment offline, or put it live, without touching the rest
    /// of the course — so a paper can be rewritten while the course itself stays open to
    /// learners. A newly created quiz starts offline (draft) so it is never sat before its
    /// questions exist; publishing it is the trainer's explicit decision.</summary>
    [Authorize(Roles = "Instructor,Admin,Principal")]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> TogglePublish(int id)
    {
        var quiz = await _db.Quizzes.Include(q => q.Course).FirstOrDefaultAsync(q => q.Id == id);
        if (quiz == null || !await OwnsCourseAsync(quiz.Course!)) return NotFound();

        if (!quiz.IsPublished && !await _db.Questions.AnyAsync(x => x.QuizId == id))
        {
            TempData["Err"] = "Add at least one question before publishing this assessment.";
            return RedirectToAction("Edit", new { id });
        }

        quiz.IsPublished = !quiz.IsPublished;
        Notifier.Audit(_db, User.GetUserId(), User.Identity!.Name ?? "",
            quiz.IsPublished ? "PublishQuiz" : "UnpublishQuiz", $"{quiz.Course!.Title} — {quiz.Title}");
        await _db.SaveChangesAsync();
        TempData["Ok"] = quiz.IsPublished
            ? "Assessment published — enrolled learners can now sit it."
            : "Assessment taken offline. Learners cannot start it until you publish it again.";
        return RedirectToAction("Edit", new { id });
    }

    /// <summary>Turn per-trainee question shuffling on or off for this paper (§QUZ-11).</summary>
    [Authorize(Roles = "Instructor,Admin,Principal")]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleShuffle(int id)
    {
        var quiz = await _db.Quizzes.Include(q => q.Course).FirstOrDefaultAsync(q => q.Id == id);
        if (quiz == null || !await OwnsCourseAsync(quiz.Course!)) return NotFound();

        quiz.ShuffleQuestions = !quiz.ShuffleQuestions;
        Notifier.Audit(_db, User.GetUserId(), User.Identity!.Name ?? "",
            quiz.ShuffleQuestions ? "EnableQuizShuffle" : "DisableQuizShuffle", $"{quiz.Course!.Title} — {quiz.Title}");
        await _db.SaveChangesAsync();
        TempData["Ok"] = quiz.ShuffleQuestions
            ? "Question order is randomised — each trainee sees this paper in a different order."
            : "Question order is fixed — every trainee sees the questions in the order below.";
        return RedirectToAction("Edit", new { id });
    }

    /// <summary>Create a quiz whose questions the AI drafts from this course's own material,
    /// sized to the trainer's time limit (§AIG-11/12). The trainer reviews before use.</summary>
    [Authorize(Roles = "Instructor,Admin,Principal")]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateWithAi(int courseId, string title, string description,
        int timeLimitMinutes, int maxAttempts, double passingScore, DateTime? dueDate, bool includeWritten = false)
    {
        var course = await _db.Courses.FirstOrDefaultAsync(c => c.Id == courseId);
        if (course == null || !await OwnsCourseAsync(course)) return NotFound();

        var minutes = timeLimitMinutes <= 0 ? 30 : timeLimitMinutes;
        var blueprint = QuizBlueprint.ForTimeLimit(minutes, includeWritten);

        var quiz = new Quiz
        {
            CourseId = courseId,
            Title = string.IsNullOrWhiteSpace(title) ? $"{course.Title} — assessment" : title.Trim(),
            Description = description ?? "",
            TimeLimitMinutes = minutes,
            MaxAttempts = maxAttempts <= 0 ? 1 : maxAttempts,
            PassingScore = passingScore,
            DueDate = dueDate,
            GeneratedByAi = true,
            // AI-drafted questions are reviewed before use (§AIG-11), so the paper
            // starts offline and the trainer publishes it once satisfied.
            IsPublished = false
        };
        _db.Quizzes.Add(quiz);
        await _db.SaveChangesAsync();

        GeneratedQuizResult result;
        try
        {
            result = await _generator.GenerateAsync(quiz, blueprint);
        }
        catch (Exception ex)
        {
            _db.Quizzes.Remove(quiz);
            await _db.SaveChangesAsync();
            TempData["Err"] = "The quiz could not be generated: " + ex.Message;
            return RedirectToAction("ManageCourse", "Instructor", new { id = courseId });
        }

        if (result.Created == 0)
        {
            _db.Quizzes.Remove(quiz);           // nothing usable — don't leave an empty quiz behind
            await _db.SaveChangesAsync();
            TempData["Err"] = result.Warning ?? "No questions could be generated from this course's material.";
            return RedirectToAction("ManageCourse", "Instructor", new { id = courseId });
        }

        Notifier.Audit(_db, User.GetUserId(), User.Identity!.Name ?? "", "GenerateQuizWithAi",
            $"{course.Title}: {result.Created} question(s), {minutes} min");
        await _db.SaveChangesAsync();

        TempData["Ok"] = $"Generated {result.Created} question(s) from this course's material — please review them below before trainees take the assessment."
            + (result.Warning == null ? "" : " Note: " + result.Warning);
        return RedirectToAction("Edit", new { id = quiz.Id });
    }

    [Authorize(Roles = "Instructor,Admin,Principal")]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AddQuestion(int quizId, string text, QuestionType type, double points,
        string? optionA, string? optionB, string? optionC, string? optionD, string? correctOption, string? answerKey,
        string? rubricText, string? referenceAnswer)
    {
        var quiz = await _db.Quizzes.Include(q => q.Course).Include(q => q.Questions).FirstOrDefaultAsync(q => q.Id == quizId);
        if (quiz == null || !await OwnsCourseAsync(quiz.Course!)) return NotFound();

        var question = new Question
        {
            Text = text, Type = type, Points = points <= 0 ? 1 : points,
            Order = quiz.Questions.Count + 1
        };

        if (type == QuestionType.MultipleChoice)
        {
            var opts = new[] { ("A", optionA), ("B", optionB), ("C", optionC), ("D", optionD) };
            foreach (var (key, val) in opts)
                if (!string.IsNullOrWhiteSpace(val))
                    question.Options.Add(new QuestionOption { Text = val.Trim(), IsCorrect = key == correctOption });
        }
        else if (type == QuestionType.TrueFalse)
        {
            question.Options.Add(new QuestionOption { Text = "True", IsCorrect = correctOption == "True" });
            question.Options.Add(new QuestionOption { Text = "False", IsCorrect = correctOption == "False" });
        }
        else if (type == QuestionType.Subjective)
        {
            // Free-text, auto-graded against the rubric/reference + retrieved manual passages (§AIG-01).
            question.RubricText = string.IsNullOrWhiteSpace(rubricText) ? null : rubricText.Trim();
            question.ReferenceAnswer = string.IsNullOrWhiteSpace(referenceAnswer) ? null : referenceAnswer.Trim();
        }
        else
        {
            question.AnswerKey = answerKey?.Trim();
        }

        quiz.Questions.Add(question);
        await _db.SaveChangesAsync();
        return RedirectToAction("Edit", new { id = quizId });
    }

    /// <summary>Add more AI-drafted questions to an existing quiz, from the same course
    /// material, without repeating what the quiz already contains (§AIG-13).</summary>
    [Authorize(Roles = "Instructor,Admin,Principal")]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> TopUpWithAi(int quizId, int addCount = 5, bool includeWritten = false)
    {
        var quiz = await _db.Quizzes.Include(q => q.Course).Include(q => q.Questions)
            .FirstOrDefaultAsync(q => q.Id == quizId);
        if (quiz == null || !await OwnsCourseAsync(quiz.Course!)) return NotFound();

        var blueprint = QuizBlueprint.ForExtra(addCount, includeWritten);
        GeneratedQuizResult result;
        try
        {
            result = await _generator.GenerateAsync(quiz, blueprint);
        }
        catch (Exception ex)
        {
            TempData["Err"] = "Could not generate more questions: " + ex.Message;
            return RedirectToAction("Edit", new { id = quizId });
        }

        if (result.Created > 0)
        {
            quiz.GeneratedByAi = true;
            Notifier.Audit(_db, User.GetUserId(), User.Identity!.Name ?? "", "TopUpQuizWithAi",
                $"{quiz.Title}: +{result.Created} question(s)");
            await _db.SaveChangesAsync();
            TempData["Ok"] = $"Added {result.Created} new question(s) — please review them."
                + (result.Warning == null ? "" : " Note: " + result.Warning);
        }
        else
        {
            TempData["Err"] = result.Warning ?? "No further questions could be drawn from this course's material.";
        }
        return RedirectToAction("Edit", new { id = quizId });
    }

    /// <summary>Edit an existing question in place — the review path for AI-drafted
    /// questions as well as for the trainer's own (§AIG-11).</summary>
    [Authorize(Roles = "Instructor,Admin,Principal")]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateQuestion(int id, string text, double points,
        string? optionA, string? optionB, string? optionC, string? optionD, string? correctOption,
        string? answerKey, string? rubricText, string? referenceAnswer)
    {
        var question = await _db.Questions
            .Include(q => q.Options)
            .Include(q => q.Quiz)!.ThenInclude(z => z!.Course)
            .FirstOrDefaultAsync(q => q.Id == id);
        if (question == null || !await OwnsCourseAsync(question.Quiz!.Course!)) return NotFound();
        if (string.IsNullOrWhiteSpace(text))
        {
            TempData["Err"] = "The question text cannot be empty.";
            return RedirectToAction("Edit", new { id = question.QuizId });
        }

        question.Text = text.Trim();
        question.Points = points <= 0 ? 1 : points;

        switch (question.Type)
        {
            case QuestionType.MultipleChoice:
                _db.QuestionOptions.RemoveRange(question.Options);
                question.Options.Clear();
                foreach (var (key, val) in new[] { ("A", optionA), ("B", optionB), ("C", optionC), ("D", optionD) })
                    if (!string.IsNullOrWhiteSpace(val))
                        question.Options.Add(new QuestionOption { Text = val.Trim(), IsCorrect = key == correctOption });
                if (question.Options.Any() && !question.Options.Any(o => o.IsCorrect))
                    question.Options.First().IsCorrect = true;
                break;

            case QuestionType.TrueFalse:
                foreach (var o in question.Options)
                    o.IsCorrect = string.Equals(o.Text, correctOption, StringComparison.OrdinalIgnoreCase);
                break;

            case QuestionType.ShortAnswer:
                question.AnswerKey = answerKey?.Trim();
                break;

            case QuestionType.Subjective:
                question.RubricText = string.IsNullOrWhiteSpace(rubricText) ? null : rubricText.Trim();
                question.ReferenceAnswer = string.IsNullOrWhiteSpace(referenceAnswer) ? null : referenceAnswer.Trim();
                break;
        }

        await _db.SaveChangesAsync();
        TempData["Ok"] = "Question updated.";
        return RedirectToAction("Edit", new { id = question.QuizId });
    }

    [Authorize(Roles = "Instructor,Admin,Principal")]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteQuestion(int id)
    {
        var question = await _db.Questions.Include(q => q.Quiz)!.ThenInclude(z => z!.Course).FirstOrDefaultAsync(q => q.Id == id);
        if (question == null || !await OwnsCourseAsync(question.Quiz!.Course!)) return NotFound();
        var quizId = question.QuizId;
        _db.Questions.Remove(question);
        await _db.SaveChangesAsync();
        return RedirectToAction("Edit", new { id = quizId });
    }

    [Authorize(Roles = "Instructor,Admin,Principal")]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var quiz = await _db.Quizzes.Include(q => q.Course).FirstOrDefaultAsync(q => q.Id == id);
        if (quiz == null || !await OwnsCourseAsync(quiz.Course!)) return NotFound();
        _db.Quizzes.Remove(quiz);
        await _db.SaveChangesAsync();
        return RedirectToAction("ManageCourse", "Instructor", new { id = quiz.CourseId });
    }

    // Results overview for instructors, admin, and principal
    [Authorize(Roles = "Instructor,Admin,Principal")]
    public async Task<IActionResult> Results(int id)
    {
        var quiz = await _db.Quizzes
            .Include(q => q.Course)
            .Include(q => q.Attempts.OrderByDescending(a => a.SubmittedAt)).ThenInclude(a => a.Student)
            .FirstOrDefaultAsync(q => q.Id == id);
        if (quiz == null) return NotFound();
        if (!User.IsInRole("Principal") && !await OwnsCourseAsync(quiz.Course!)) return NotFound();
        return View(quiz);
    }

    // ---------- Taking ----------
    [Authorize(Roles = "Student")]
    public async Task<IActionResult> Take(int id)
    {
        var quiz = await _db.Quizzes
            .Include(q => q.Course)
            .Include(q => q.Questions.OrderBy(x => x.Order)).ThenInclude(x => x.Options)
            .FirstOrDefaultAsync(q => q.Id == id);
        if (quiz == null) return NotFound();
        // An assessment is closed to learners either in its own right (taken offline while
        // it is rewritten) or because its course is unpublished and therefore a draft under
        // revision (§CRS-06). Either way say so, rather than returning a bare 404.
        if (!quiz.IsPublished || !quiz.Course!.IsPublished)
        {
            TempData["Err"] = "This assessment is being updated and is temporarily unavailable. Please check back shortly.";
            return RedirectToAction("MyCourses", "Courses");
        }

        var uid = User.GetUserId();
        var enrolled = await _db.Enrollments.AnyAsync(e => e.CourseId == quiz.CourseId && e.StudentId == uid && e.Status != EnrollmentStatus.Dropped);
        if (!enrolled) return Forbid();

        var attemptCount = await _db.QuizAttempts.CountAsync(a => a.QuizId == id && a.StudentId == uid && a.SubmittedAt != null);
        if (!quiz.IsSelfAssessment && attemptCount >= await AllowedAttemptsAsync(quiz, uid))
        {
            TempData["Err"] = "You have used all your attempts. You can request a retake from Assessments → Retakes.";
            return RedirectToAction("MyResult", new { id });
        }
        if (quiz.DueDate != null && DateTime.UtcNow > quiz.DueDate)
        {
            TempData["Err"] = "This quiz is past its due date.";
            return RedirectToAction("Details", "Courses", new { id = quiz.CourseId });
        }

        // Each trainee sees the paper in their own order (§QUZ-11). Derived from the quiz, the
        // trainee and the attempt number, so a refresh mid-attempt cannot reshuffle it.
        var attemptNo = attemptCount + 1;
        ViewBag.AttemptNumber = attemptNo;
        ViewBag.OrderedQuestions = QuizOrdering.For(quiz.Questions, quiz, uid, attemptNo);
        return View(quiz);
    }

    [Authorize(Roles = "Student")]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Submit(int id, Dictionary<int, string> answers)
    {
        var quiz = await _db.Quizzes
            .Include(q => q.Course)
            .Include(q => q.Questions).ThenInclude(x => x.Options)
            .FirstOrDefaultAsync(q => q.Id == id);
        if (quiz == null) return NotFound();
        // Refuse a submission for an assessment that has since been withdrawn. The paper may
        // have been rewritten in the meantime, and scoring these answers against the new
        // questions would silently produce a wrong mark — better to say the attempt was not
        // recorded. (The trainer is warned before taking a live assessment offline.)
        if (!quiz.IsPublished || !quiz.Course!.IsPublished)
        {
            TempData["Err"] = "This assessment was withdrawn for updating while you were taking it, so your attempt could not be recorded. It has not been counted against your allowed attempts — please try again once it is available.";
            return RedirectToAction("MyCourses", "Courses");
        }

        // A paper submitted with nothing filled in posts no answers[...] keys at all, and model
        // binding then hands us null rather than an empty dictionary — which used to throw, so the
        // trainee saw a 500 instead of a (zero) result. Treat it as "answered nothing".
        answers ??= new Dictionary<int, string>();

        var uid = User.GetUserId();
        var attemptCount = await _db.QuizAttempts.CountAsync(a => a.QuizId == id && a.StudentId == uid && a.SubmittedAt != null);
        if (!quiz.IsSelfAssessment && attemptCount >= await AllowedAttemptsAsync(quiz, uid))
        {
            TempData["Err"] = "You have used all your attempts.";
            return RedirectToAction("MyResult", new { id });
        }

        var attemptNo = attemptCount + 1;
        var attempt = new QuizAttempt
        {
            QuizId = id, StudentId = uid, SubmittedAt = DateTime.UtcNow,
            AttemptNumber = attemptNo,
            MaxScore = quiz.Questions.Sum(q => q.Points),
            // The order this paper was actually sat in, so review and marking replay it (§QUZ-11).
            // Scoring below is unaffected: it iterates the questions and looks each answer up by
            // question id, so a response is always marked against its own question.
            QuestionOrder = QuizOrdering.Record(QuizOrdering.For(quiz.Questions, quiz, uid, attemptNo))
        };

        double score = 0;
        foreach (var question in quiz.Questions)
        {
            answers.TryGetValue(question.Id, out var raw);
            var answer = new QuizAnswer { QuestionId = question.Id };

            if (question.Type == QuestionType.ShortAnswer)
            {
                answer.TextAnswer = raw;
                if (!string.IsNullOrWhiteSpace(raw) && !string.IsNullOrWhiteSpace(question.AnswerKey) &&
                    string.Equals(raw.Trim(), question.AnswerKey.Trim(), StringComparison.OrdinalIgnoreCase))
                    answer.PointsAwarded = question.Points;
            }
            else if (question.Type == QuestionType.Subjective)
            {
                // Free-text: graded by the local LLM after submit (sync in GPU mode, async in CPU mode).
                answer.TextAnswer = raw;
                answer.GradingPending = true;   // no points awarded yet (§AIG-05)
            }
            else if (int.TryParse(raw, out var optionId))
            {
                answer.SelectedOptionId = optionId;
                var selected = question.Options.FirstOrDefault(o => o.Id == optionId);
                if (selected?.IsCorrect == true)
                    answer.PointsAwarded = question.Points;
            }
            score += answer.PointsAwarded;
            attempt.Answers.Add(answer);
        }
        attempt.Score = score;
        _db.QuizAttempts.Add(attempt);

        var user = await _db.Users.FindAsync(uid);
        if (user != null) user.Points += (int)(score * 2);   // gamification points for the auto-graded portion

        // Subjective (free-text) answers are graded by the local LLM: synchronously in GPU
        // mode, or by the background worker in CPU mode (§AIG-05). Until then the quiz has no
        // final pass/fail, so we don't emit a premature passed/failed statement.
        if (attempt.Answers.Any(a => a.GradingPending))
        {
            await _db.SaveChangesAsync();   // persist attempt + answers (assigns ids the grader needs)

            var mode = await _gradingOptions.GetModeAsync();
            if (GradingOptions.IsSynchronous(mode))
            {
                await _grading.GradeAttemptPendingAsync(attempt.Id);   // GPU: score ready on the result page
            }
            else
            {
                Notifier.Notify(_db, uid, $"Quiz \"{quiz.Title}\" submitted — your written answers are being graded; your result will appear shortly.");
                await _db.SaveChangesAsync();
            }
            return RedirectToAction("MyResult", new { id });
        }

        var pct = attempt.MaxScore > 0 ? score / attempt.MaxScore * 100 : 0;
        if (user != null)
        {
            Xapi.Emit(_db, user,
                pct >= quiz.PassingScore ? Xapi.VerbPassed : Xapi.VerbFailed,
                pct >= quiz.PassingScore ? "passed" : "failed",
                $"https://lms.punemetro.in/quizzes/{quiz.Id}", quiz.Title,
                attempt.MaxScore > 0 ? score / attempt.MaxScore : null);
        }
        Notifier.Notify(_db, uid, $"Quiz \"{quiz.Title}\" scored: {pct:0.#}% ({(pct >= quiz.PassingScore ? "Passed" : "Not passed")})");
        await _db.SaveChangesAsync();

        // Passing this quiz may satisfy the last completion requirement for the course.
        if (await CourseCompletion.CheckAsync(_db, quiz.CourseId, uid))
            await _db.SaveChangesAsync();

        return RedirectToAction("MyResult", new { id });
    }

    [Authorize(Roles = "Student")]
    public async Task<IActionResult> MyResult(int id)
    {
        var uid = User.GetUserId();
        var quiz = await _db.Quizzes
            .Include(q => q.Course)
            .Include(q => q.Questions.OrderBy(x => x.Order)).ThenInclude(x => x.Options)
            .FirstOrDefaultAsync(q => q.Id == id);
        if (quiz == null) return NotFound();
        var attempts = await _db.QuizAttempts
            .Include(a => a.Answers)
            .Where(a => a.QuizId == id && a.StudentId == uid && a.SubmittedAt != null)
            .OrderByDescending(a => a.SubmittedAt).ToListAsync();
        ViewBag.Attempts = attempts;
        // Review shows the paper as it was sat, not in author order (§QUZ-11).
        ViewBag.OrderedQuestions = QuizOrdering.Replay(quiz.Questions, attempts.FirstOrDefault()?.QuestionOrder);

        // Subjective-grading state + feedback for the latest attempt (§AIG-05/07).
        var pending = new HashSet<int>();
        var review = new HashSet<int>();
        var feedback = new Dictionary<int, string>();
        var latest = attempts.FirstOrDefault();
        if (latest != null)
        {
            var answerIds = latest.Answers.Select(a => a.Id).ToList();
            foreach (var a in latest.Answers.Where(a => a.GradingPending)) pending.Add(a.Id);
            review.UnionWith(await _db.SubjectiveGradingReviews
                .Where(r => answerIds.Contains(r.QuizAnswerId) && r.Status == GradingReviewStatus.Pending)
                .Select(r => r.QuizAnswerId).ToListAsync());
            foreach (var r in await _db.SubjectiveGradeResults
                .Where(r => answerIds.Contains(r.QuizAnswerId) && r.Feedback != null).ToListAsync())
                feedback[r.QuizAnswerId] = r.Feedback!;
        }
        ViewBag.SubjPending = pending;
        ViewBag.SubjReview = review;
        ViewBag.SubjFeedback = feedback;
        return View(quiz);
    }

    // ---------- Trainer review of held subjective grades (§AIG-07) ----------
    public record ReviewRow(int ReviewId, string Question, string? Rubric, string ReferenceAnswer,
        string Answer, double Proposed, double MaxPoints, string Student, string Course, string Quiz,
        string? Feedback, string Citations);

    [Authorize(Roles = "Instructor,Admin,Principal")]
    public async Task<IActionResult> Reviews()
    {
        var pending = await _db.SubjectiveGradingReviews
            .Where(r => r.Status == GradingReviewStatus.Pending)
            .OrderBy(r => r.CreatedAt).ToListAsync();

        var answerIds = pending.Select(r => r.QuizAnswerId).ToList();
        var answers = await _db.QuizAnswers
            .Where(a => answerIds.Contains(a.Id))
            .Include(a => a.QuizAttempt)!.ThenInclude(t => t!.Student)
            .Include(a => a.Question)!.ThenInclude(q => q!.Quiz)!.ThenInclude(z => z!.Course)
            .ToListAsync();
        var latestResults = await _db.SubjectiveGradeResults
            .Where(r => answerIds.Contains(r.QuizAnswerId)).ToListAsync();

        var rows = new List<ReviewRow>();
        foreach (var r in pending)
        {
            var a = answers.FirstOrDefault(x => x.Id == r.QuizAnswerId);
            var course = a?.Question?.Quiz?.Course;
            if (a == null || course == null || !await OwnsCourseAsync(course)) continue;   // tenant + ownership gate
            var res = latestResults.Where(x => x.QuizAnswerId == r.QuizAnswerId).OrderByDescending(x => x.GradedAt).FirstOrDefault();
            rows.Add(new ReviewRow(r.Id, a.Question!.Text, a.Question!.RubricText, a.Question!.ReferenceAnswer ?? "",
                a.TextAnswer ?? "", r.ProposedScore, a.Question!.Points,
                a.QuizAttempt?.Student?.FullName ?? "—", course.Title, a.Question!.Quiz!.Title,
                res?.Feedback, res?.Citations ?? ""));
        }
        return View(rows);
    }

    [Authorize(Roles = "Instructor,Admin,Principal")]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ResolveReview(int reviewId, double score, string? feedback)
    {
        // Re-check ownership before applying the decision.
        var review = await _db.SubjectiveGradingReviews.FirstOrDefaultAsync(r => r.Id == reviewId && r.Status == GradingReviewStatus.Pending);
        if (review == null) { TempData["Err"] = "That review was already resolved."; return RedirectToAction("Reviews"); }
        var course = await _db.QuizAnswers.Where(a => a.Id == review.QuizAnswerId)
            .Select(a => a.Question!.Quiz!.Course).FirstOrDefaultAsync();
        if (course == null || !await OwnsCourseAsync(course)) return Forbid();

        await _grading.ResolveReviewAsync(reviewId, score, feedback, User.GetUserId());
        Notifier.Audit(_db, User.GetUserId(), User.Identity!.Name ?? "", "ResolveSubjectiveGrade");
        await _db.SaveChangesAsync();
        TempData["Ok"] = "Grade confirmed.";
        return RedirectToAction("Reviews");
    }
}

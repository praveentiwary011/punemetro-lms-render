using System.ComponentModel.DataAnnotations;

namespace LMS.Web.Models;

public class Assignment
{
    public int Id { get; set; }
    public int CourseId { get; set; }
    public Course? Course { get; set; }
    [Required, MaxLength(200)]
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public DateTime? DueDate { get; set; }
    public double MaxPoints { get; set; } = 100;
    public bool AllowLateSubmission { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public ICollection<Submission> Submissions { get; set; } = new List<Submission>();
}

public class Submission
{
    public int Id { get; set; }
    public int AssignmentId { get; set; }
    public Assignment? Assignment { get; set; }
    public string StudentId { get; set; } = "";
    public ApplicationUser? Student { get; set; }
    public string? Text { get; set; }
    public string? FileUrl { get; set; }
    public DateTime SubmittedAt { get; set; } = DateTime.UtcNow;
    public double? Grade { get; set; }
    public string? Feedback { get; set; }
    public DateTime? GradedAt { get; set; }
}

public class Quiz
{
    public int Id { get; set; }
    public int CourseId { get; set; }
    public Course? Course { get; set; }
    [Required, MaxLength(200)]
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public int TimeLimitMinutes { get; set; } = 30;
    public int MaxAttempts { get; set; } = 2;
    public double PassingScore { get; set; } = 60;
    public bool IsPublished { get; set; } = true;
    /// <summary>Self-assessments allow unlimited attempts and don't affect grades.</summary>
    public bool IsSelfAssessment { get; set; }
    /// <summary>Present the questions in a different order to every trainee and every attempt
    /// (§QUZ-11), so neighbours in a classroom are not on the same question at the same time.
    /// On by default; a trainer turns it off for a paper whose questions must be worked in
    /// sequence (one building on the last).</summary>
    public bool ShuffleQuestions { get; set; } = true;
    /// <summary>True when the questions were drafted by the AI from the course material (§AIG-11).</summary>
    public bool GeneratedByAi { get; set; }
    public DateTime? DueDate { get; set; }
    public ICollection<Question> Questions { get; set; } = new List<Question>();
    public ICollection<QuizAttempt> Attempts { get; set; } = new List<QuizAttempt>();
}

public enum QuestionType { MultipleChoice = 0, TrueFalse = 1, ShortAnswer = 2, Subjective = 3 }

public class Question
{
    public int Id { get; set; }
    public int QuizId { get; set; }
    public Quiz? Quiz { get; set; }
    public string Text { get; set; } = "";
    public QuestionType Type { get; set; }
    public double Points { get; set; } = 1;
    public int Order { get; set; }
    /// <summary>Expected answer for short-answer questions (case-insensitive match).</summary>
    public string? AnswerKey { get; set; }
    /// <summary>Grading criteria for a Subjective (free-text) question — the primary signal the auto-grader scores against (§AIG-01).</summary>
    public string? RubricText { get; set; }
    /// <summary>Optional model/reference answer for a Subjective question.</summary>
    public string? ReferenceAnswer { get; set; }
    public ICollection<QuestionOption> Options { get; set; } = new List<QuestionOption>();
}

public class QuestionOption
{
    public int Id { get; set; }
    public int QuestionId { get; set; }
    public Question? Question { get; set; }
    public string Text { get; set; } = "";
    public bool IsCorrect { get; set; }
}

public class QuizAttempt
{
    public int Id { get; set; }
    public int QuizId { get; set; }
    public Quiz? Quiz { get; set; }
    public string StudentId { get; set; } = "";
    public ApplicationUser? Student { get; set; }
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? SubmittedAt { get; set; }
    public double Score { get; set; }
    public double MaxScore { get; set; }
    public int AttemptNumber { get; set; } = 1;
    /// <summary>Question ids in the order this attempt presented them (§QUZ-11). Recorded so the
    /// learner's review and the trainer's marking show the paper as it was actually sat, rather
    /// than in author order — and so a later change to the shuffle never rewrites history.</summary>
    public string? QuestionOrder { get; set; }
    public ICollection<QuizAnswer> Answers { get; set; } = new List<QuizAnswer>();
}

public class QuizAnswer
{
    public int Id { get; set; }
    public int QuizAttemptId { get; set; }
    public QuizAttempt? QuizAttempt { get; set; }
    public int QuestionId { get; set; }
    public Question? Question { get; set; }
    public int? SelectedOptionId { get; set; }
    public string? TextAnswer { get; set; }
    public double PointsAwarded { get; set; }
    /// <summary>True while a Subjective answer is awaiting automated (or retried) grading (§AIG-05/09).</summary>
    public bool GradingPending { get; set; }
}

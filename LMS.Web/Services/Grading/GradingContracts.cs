using LMS.Web.Models;

namespace LMS.Web.Services.Grading;

/// <summary>A reference passage retrieved from the knowledge index, handed to the grader.</summary>
public record RetrievedChunk(string Text, string SourceLabel, double Score);

/// <summary>Structured result the subjective grader returns for one answer (§AIG-04).</summary>
public record SubjectiveGradeOutcome(
    double Score,
    double MaxScore,
    string Verdict,
    GradeConfidence Confidence,
    string Feedback,
    IReadOnlyList<string> Citations,
    bool NeedsReview,
    string RawJson,
    string Model,
    GradingMode Mode);

/// <summary>Thrown when the local LLM (Ollama) cannot be reached or is still loading.
/// Callers treat this as "grade later" — never as a zero score (§AIG-09).</summary>
public class GradingUnavailableException : Exception
{
    public GradingUnavailableException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>Thin client over the local Ollama daemon (embeddings + chat).</summary>
public interface IOllamaClient
{
    Task<float[]> EmbedAsync(string text, CancellationToken ct = default);
    Task<string> ChatJsonAsync(string model, string system, string user, TimeSpan timeout, CancellationToken ct = default);
    Task<bool> IsAvailableAsync(CancellationToken ct = default);
}

/// <summary>Grades one subjective answer against a rubric and retrieved passages.</summary>
public interface ISubjectiveGrader
{
    Task<SubjectiveGradeOutcome> GradeAsync(Question question, string answer,
        IReadOnlyList<RetrievedChunk> context, CancellationToken ct = default);
}

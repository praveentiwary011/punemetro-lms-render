using System.ComponentModel.DataAnnotations;

namespace LMS.Web.Models;

/// <summary>Inference profile for subjective auto-grading (§AIG-05). CPU = Qwen2.5 7B, async;
/// GPU = Qwen2.5 14B, synchronous.</summary>
public enum GradingMode { Cpu = 0, Gpu = 1 }

public enum GradeConfidence { High = 0, Medium = 1, Low = 2 }

public enum KnowledgeSourceType { Document = 0, VideoTranscript = 1, Lesson = 2 }

public enum GradingReviewStatus { Pending = 0, Resolved = 1 }

/// <summary>A ~500-token passage of a tenant's manual/course content, with its embedding,
/// used to ground subjective grading (§AIG-02). Rebuilt per source on (re)index.</summary>
public class KnowledgeChunk
{
    public int Id { get; set; }
    /// <summary>Tenant scope — retrieval never crosses organisations.</summary>
    public int OrganisationId { get; set; }
    public Organisation? Organisation { get; set; }
    /// <summary>Preferred source course, if the passage came from course material.</summary>
    public int? CourseId { get; set; }
    public Course? Course { get; set; }
    public KnowledgeSourceType SourceType { get; set; }
    /// <summary>Pointer to the originating item (document id, video id, lesson id).</summary>
    public string SourceRef { get; set; } = "";
    /// <summary>Human-readable label used for citations, e.g. "Signalling Manual §4.2".</summary>
    [MaxLength(200)]
    public string SourceLabel { get; set; } = "";
    public string Text { get; set; } = "";
    /// <summary>Packed float32 embedding vector (little-endian).</summary>
    public byte[] Embedding { get; set; } = System.Array.Empty<byte>();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Auto-grade audit for one subjective answer (§AIG-10). 1:0..1 with a QuizAnswer.</summary>
public class SubjectiveGradeResult
{
    public int Id { get; set; }
    public int QuizAnswerId { get; set; }
    public QuizAnswer? QuizAnswer { get; set; }
    public double Score { get; set; }
    public double MaxScore { get; set; }
    public string? Verdict { get; set; }
    public GradeConfidence Confidence { get; set; }
    public string? Feedback { get; set; }
    /// <summary>Semicolon-joined source labels the grade cites.</summary>
    public string? Citations { get; set; }
    [MaxLength(80)]
    public string? Model { get; set; }
    public GradingMode Mode { get; set; }
    /// <summary>Semicolon-joined labels of the passages supplied to the grader.</summary>
    public string? RetrievedChunkLabels { get; set; }
    /// <summary>Raw structured JSON returned by the grader (for audit).</summary>
    public string? RawResult { get; set; }
    public bool NeedsReview { get; set; }
    public DateTime GradedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Holds a low-confidence / near-boundary / unparseable subjective result for
/// trainer confirmation before the attempt finalises and any certificate issues (§AIG-07).</summary>
public class SubjectiveGradingReview
{
    public int Id { get; set; }
    public int QuizAnswerId { get; set; }
    public QuizAnswer? QuizAnswer { get; set; }
    public double ProposedScore { get; set; }
    public double? ResolvedScore { get; set; }
    public string? ReviewerUserId { get; set; }
    public ApplicationUser? Reviewer { get; set; }
    public string? TrainerFeedback { get; set; }
    public GradingReviewStatus Status { get; set; } = GradingReviewStatus.Pending;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ResolvedAt { get; set; }
}

using System.Text;
using System.Text.Json;
using LMS.Web.Data;
using LMS.Web.Models;
using Microsoft.EntityFrameworkCore;

namespace LMS.Web.Services.Grading;

/// <summary>How the questions of a quiz were authored (§AIG-11).</summary>
public enum QuizAuthoring { Trainer = 0, Ai = 1 }

/// <summary>The blueprint derived from the trainer's time limit: how many questions of each
/// type fit, using conservative per-question answering times.</summary>
public record QuizBlueprint(int Mcq, int TrueFalse, int Short, int Subjective)
{
    public int Total => Mcq + TrueFalse + Short + Subjective;

    // Minutes a trainee typically needs per question type.
    public const double MinPerMcq = 1.0, MinPerTf = 0.5, MinPerShort = 1.5, MinPerSubjective = 5.0;

    public double EstimatedMinutes =>
        Mcq * MinPerMcq + TrueFalse * MinPerTf + Short * MinPerShort + Subjective * MinPerSubjective;

    /// <summary>Fit a question mix into the trainer's time limit (§AIG-12). "Written" mixes in
    /// subjective questions (auto-graded against the course material); otherwise objective only.</summary>
    public static QuizBlueprint ForTimeLimit(int minutes, bool includeWritten)
    {
        minutes = Math.Clamp(minutes, 5, 180);
        if (!includeWritten)
        {
            // ~70% of the time on multiple-choice, ~30% on true/false.
            int mcq = (int)Math.Round(minutes * 0.70 / MinPerMcq);
            int tf = (int)Math.Round(minutes * 0.30 / MinPerTf);
            return Cap(new QuizBlueprint(Math.Max(1, mcq), Math.Max(1, tf), 0, 0));
        }
        // Written mix: ~45% multiple-choice, ~15% true/false, ~40% written (subjective).
        int m = (int)Math.Round(minutes * 0.45 / MinPerMcq);
        int t = (int)Math.Round(minutes * 0.15 / MinPerTf);
        int s = (int)Math.Round(minutes * 0.40 / MinPerSubjective);
        return Cap(new QuizBlueprint(Math.Max(1, m), Math.Max(1, t), 0, Math.Max(1, s)));
    }

    /// <summary>Blueprint for adding a fixed number of extra questions to an existing quiz
    /// (§AIG-13), rather than sizing a whole paper to a time limit.</summary>
    public static QuizBlueprint ForExtra(int count, bool includeWritten)
    {
        count = Math.Clamp(count, 1, 20);
        if (!includeWritten)
        {
            int mcq = (int)Math.Ceiling(count * 0.6);
            return new QuizBlueprint(mcq, count - mcq, 0, 0);
        }
        int m = (int)Math.Round(count * 0.5);
        int t = (int)Math.Round(count * 0.2);
        int sub = Math.Max(1, count - m - t);
        if (m + t + sub > count) m = Math.Max(1, count - t - sub);
        return new QuizBlueprint(m, t, 0, sub);
    }

    // Keep generation bounded so a long time limit can't produce a runaway prompt/run.
    private static QuizBlueprint Cap(QuizBlueprint b) =>
        b.Total <= 30 ? b : new QuizBlueprint(Math.Min(b.Mcq, 15), Math.Min(b.TrueFalse, 8), b.Short, Math.Min(b.Subjective, 7));
}

public record GeneratedQuizResult(int Created, int Requested, string? Warning);

/// <summary>Generates a quiz from the trainer's own course material using the local Qwen 2.5
/// model (§AIG-11/12). Questions are written strictly from the course's lessons, so they can
/// only test what was actually taught; written questions arrive with a rubric so the existing
/// subjective auto-grader can mark them.</summary>
public class QuizGenerator
{
    private readonly AppDbContext _db;
    private readonly IOllamaClient _ollama;
    private readonly GradingOptions _options;
    private readonly ILogger<QuizGenerator> _log;

    private const int BatchSize = 4;                 // small batches keep the JSON reliable on a 7B model
    private const int MaxRoundsPerType = 6;          // bounded top-up when batches come back short
    private const int MaterialWordCap = 4500;        // keeps the prompt within a comfortable context
    private static readonly TimeSpan BatchTimeout = TimeSpan.FromMinutes(4);

    public QuizGenerator(AppDbContext db, IOllamaClient ollama, GradingOptions options, ILogger<QuizGenerator> log)
    { _db = db; _ollama = ollama; _options = options; _log = log; }

    /// <summary>Collect the course material the trainer authored: description, then each
    /// module's lessons in order (HTML stripped). Empty when the course has no readable content.</summary>
    public async Task<string> GetCourseMaterialAsync(int courseId, CancellationToken ct = default)
    {
        var course = await _db.Courses.IgnoreQueryFilters()
            .Where(c => c.Id == courseId).Select(c => new { c.Title, c.Description }).FirstOrDefaultAsync(ct);
        if (course == null) return "";

        var lessons = await (from l in _db.Lessons.IgnoreQueryFilters()
                             join m in _db.Modules on l.ModuleId equals m.Id
                             where m.CourseId == courseId
                             orderby m.Order, l.Order
                             select new { m.Title, LessonTitle = l.Title, l.Content, l.ExtractedText, l.Type })
                             .ToListAsync(ct);

        var sb = new StringBuilder();
        sb.AppendLine($"COURSE: {course.Title}");
        if (!string.IsNullOrWhiteSpace(course.Description))
            sb.AppendLine(TextChunker.HtmlToText(course.Description));
        foreach (var l in lessons)
        {
            // Authored text, plus the text of an uploaded document or a video transcript —
            // a course whose material is entirely PDFs and videos generated nothing before.
            var body = TextChunker.HtmlToText(l.Content);
            if (string.IsNullOrWhiteSpace(body) && !string.IsNullOrWhiteSpace(l.ExtractedText))
                body = l.ExtractedText;
            if (string.IsNullOrWhiteSpace(body)) continue;
            var kind = l.Type == LessonType.Video ? " (video transcript)"
                     : l.Type == LessonType.File ? " (document)" : "";
            sb.AppendLine();
            sb.AppendLine($"--- {l.Title} / {l.LessonTitle}{kind} ---");
            sb.AppendLine(body);
        }

        var words = sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length <= MaterialWordCap ? sb.ToString() : string.Join(' ', words.Take(MaterialWordCap));
    }

    /// <summary>Generate and persist questions for an existing quiz, from its course's material.</summary>
    public async Task<GeneratedQuizResult> GenerateAsync(Quiz quiz, QuizBlueprint blueprint, CancellationToken ct = default)
    {
        var material = await GetCourseMaterialAsync(quiz.CourseId, ct);
        if (string.IsNullOrWhiteSpace(material) || material.Split(' ').Length < 40)
            return new GeneratedQuizResult(0, blueprint.Total,
                "This course has no lesson content yet — add lessons first, then generate the quiz from them.");

        var mode = await _options.GetModeAsync(ct);
        var model = GradingOptions.ModelFor(mode);

        // Work through the blueprint in small batches, one question type at a time.
        var wanted = new List<(QuestionType type, int count)>
        {
            (QuestionType.MultipleChoice, blueprint.Mcq),
            (QuestionType.TrueFalse, blueprint.TrueFalse),
            (QuestionType.ShortAnswer, blueprint.Short),
            (QuestionType.Subjective, blueprint.Subjective),
        };

        int order = quiz.Questions.Count, created = 0;
        string? warning = null;
        // Seeded with the quiz's existing questions, so a top-up (§AIG-13) never repeats
        // a question the trainer already has.
        var asked = quiz.Questions.Select(q => q.Text).ToList();

        foreach (var (type, total) in wanted)
        {
            if (total <= 0) continue;
            int madeOfType = 0, rounds = 0;
            // A batch can come back short (unparseable entries, or near-duplicates we drop),
            // so keep topping up until the blueprint is met or we run out of rounds.
            while (madeOfType < total && rounds < MaxRoundsPerType)
            {
                rounds++;
                int n = Math.Min(BatchSize, total - madeOfType);
                List<Question> batch;
                try
                {
                    var raw = await _ollama.ChatJsonAsync(model, SystemPrompt(type),
                        UserPrompt(type, n, material, asked), BatchTimeout, ct);
                    batch = Parse(raw, type);
                }
                catch (GradingUnavailableException ex)
                {
                    _log.LogWarning(ex, "Quiz generation stopped early (model unavailable).");
                    warning = "The local AI model stopped responding, so fewer questions were generated. You can generate again or add the rest yourself.";
                    goto finish;
                }

                int addedThisRound = 0;
                foreach (var q in batch)
                {
                    if (madeOfType >= total) break;
                    if (IsDuplicate(q.Text, asked)) continue;
                    q.Order = ++order;
                    quiz.Questions.Add(q);
                    asked.Add(q.Text);
                    created++; madeOfType++; addedThisRound++;
                }
                if (addedThisRound == 0 && rounds >= 2) break;   // material exhausted for this type
            }
        }

    finish:
        if (created > 0) await _db.SaveChangesAsync(ct);
        if (created == 0 && warning == null)
            warning = "The AI could not produce usable questions from this course's material. Try adding more lesson content, or create the questions yourself.";
        else if (warning == null && created < blueprint.Total)
            warning = $"This course's material supported {created} distinct question(s) rather than the {blueprint.Total} your time limit allows — add more lesson content for a longer assessment, or add questions yourself.";
        return new GeneratedQuizResult(created, blueprint.Total, warning);
    }

    private static string SystemPrompt(QuestionType type)
    {
        var shape = type switch
        {
            QuestionType.MultipleChoice =>
                "{\"questions\":[{\"text\":\"…\",\"options\":[\"…\",\"…\",\"…\",\"…\"],\"correctIndex\":0}]} — exactly 4 plausible options, exactly one correct.",
            QuestionType.TrueFalse =>
                "{\"questions\":[{\"text\":\"a statement that is clearly true or false\",\"correct\":true}]}",
            QuestionType.ShortAnswer =>
                "{\"questions\":[{\"text\":\"…\",\"answerKey\":\"the exact short answer (one or two words)\"}]}",
            _ =>
                "{\"questions\":[{\"text\":\"an open question requiring a written answer\",\"rubric\":\"numbered marking criteria\",\"referenceAnswer\":\"a model answer\",\"points\":10}]}"
        };
        return "You are an experienced training assessor writing exam questions for staff training. " +
               "Use ONLY the supplied course material — never outside knowledge, and never invent facts it does not contain. " +
               "Test the SUBJECT MATTER the material teaches — the facts, procedures, rules and reasoning a trainee must know on the job. " +
               "NEVER write questions about the course's own structure: do not ask which module or lesson contains a topic, " +
               "and never mention module names, lesson titles, course codes or the word 'course' in a question. " +
               "A question must make sense to someone who has learnt the content but has never seen the syllabus, " +
               "so never write phrases such as 'according to the material', 'in the text' or 'as taught in this course'. " +
               "Questions must be unambiguous and written in clear professional English. " +
               "Do not number the questions. Respond with a single JSON object and nothing else, in exactly this shape: " + shape;
    }

    private static string UserPrompt(QuestionType type, int n, string material, List<string> asked)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Write {n} {Describe(type)} based on the course material below.");
        if (asked.Count > 0)
        {
            sb.AppendLine("Do NOT repeat or rephrase any of these already-written questions:");
            foreach (var a in asked.TakeLast(12)) sb.AppendLine("- " + a);
        }
        sb.AppendLine();
        sb.AppendLine("=== COURSE MATERIAL ===");
        sb.AppendLine(material);
        sb.AppendLine("=== END OF COURSE MATERIAL ===");
        return sb.ToString();
    }

    private static string Describe(QuestionType t) => t switch
    {
        QuestionType.MultipleChoice => "multiple-choice questions",
        QuestionType.TrueFalse => "true/false statements",
        QuestionType.ShortAnswer => "short-answer questions",
        _ => "open written questions (each with marking criteria)"
    };

    private static List<Question> Parse(string raw, QuestionType type)
    {
        var result = new List<Question>();
        JsonDocument doc;
        try { doc = JsonDocument.Parse(raw); } catch (JsonException) { return result; }
        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("questions", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return result;

            foreach (var e in arr.EnumerateArray())
            {
                var text = Str(e, "text");
                if (string.IsNullOrWhiteSpace(text)) continue;
                var q = new Question { Text = text.Trim(), Type = type, Points = 1 };

                switch (type)
                {
                    case QuestionType.MultipleChoice:
                        if (!e.TryGetProperty("options", out var opts) || opts.ValueKind != JsonValueKind.Array) continue;
                        var texts = opts.EnumerateArray().Where(o => o.ValueKind == JsonValueKind.String)
                                        .Select(o => o.GetString()!.Trim()).Where(s => s.Length > 0).Distinct().ToList();
                        if (texts.Count < 2) continue;
                        int correct = e.TryGetProperty("correctIndex", out var ci) && ci.ValueKind == JsonValueKind.Number
                            ? ci.GetInt32() : 0;
                        if (correct < 0 || correct >= texts.Count) correct = 0;
                        for (int i = 0; i < texts.Count && i < 4; i++)
                            q.Options.Add(new QuestionOption { Text = texts[i], IsCorrect = i == correct });
                        if (!q.Options.Any(o => o.IsCorrect)) q.Options.First().IsCorrect = true;
                        break;

                    case QuestionType.TrueFalse:
                        bool isTrue = e.TryGetProperty("correct", out var c) &&
                                      (c.ValueKind == JsonValueKind.True ||
                                       (c.ValueKind == JsonValueKind.String && bool.TryParse(c.GetString(), out var b) && b));
                        q.Options.Add(new QuestionOption { Text = "True", IsCorrect = isTrue });
                        q.Options.Add(new QuestionOption { Text = "False", IsCorrect = !isTrue });
                        break;

                    case QuestionType.ShortAnswer:
                        var key = Str(e, "answerKey");
                        if (string.IsNullOrWhiteSpace(key)) continue;
                        q.AnswerKey = key.Trim();
                        break;

                    default:   // Subjective
                        var rubric = Str(e, "rubric");
                        if (string.IsNullOrWhiteSpace(rubric)) continue;   // no rubric = not gradeable
                        q.RubricText = rubric.Trim();
                        q.ReferenceAnswer = Str(e, "referenceAnswer")?.Trim();
                        q.Points = e.TryGetProperty("points", out var p) && p.ValueKind == JsonValueKind.Number
                            ? Math.Clamp(p.GetDouble(), 1, 20) : 10;
                        break;
                }
                result.Add(q);
            }
        }
        return result;
    }

    /// <summary>Rejects exact repeats and near-repeats (same leading wording), so a thin
    /// course doesn't yield the same question phrased twice.</summary>
    private static bool IsDuplicate(string text, List<string> asked)
    {
        static string Norm(string t) => new string(t.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        var n = Norm(text);
        if (n.Length == 0) return true;
        foreach (var a in asked)
        {
            var na = Norm(a);
            if (na == n) return true;
            var len = Math.Min(40, Math.Min(na.Length, n.Length));
            if (len >= 25 && na.AsSpan(0, len).SequenceEqual(n.AsSpan(0, len))) return true;
        }
        return false;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

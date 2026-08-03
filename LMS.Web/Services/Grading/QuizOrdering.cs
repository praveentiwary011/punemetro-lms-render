using System.Security.Cryptography;
using System.Text;
using LMS.Web.Models;

namespace LMS.Web.Services.Grading;

/// <summary>Decides the order a trainee sees a paper's questions in (§QUZ-11).
///
/// The order is <b>derived</b>, not drawn at random: it is a hash of the question, the quiz, the
/// trainee and the attempt number. That matters more than it sounds —
///
///   • it is <b>stable within an attempt</b>, so refreshing the page, or the timer's auto-submit
///     re-rendering, cannot reshuffle a paper the trainee is halfway through;
///   • it <b>differs per trainee</b>, which is the point in a classroom;
///   • it <b>differs per attempt</b>, so a retake is not the same paper in the same order;
///   • it needs <b>no state</b> at display time, which is what allows the order to be settled
///     before an attempt row exists (the attempt is only created on submit).
///
/// Grading is unaffected by any of this: answers are keyed by question id, never by position, so
/// each response is marked against its own question no matter where it appeared on the page. The
/// order actually used is still recorded on the attempt so review replays the paper as sat.</summary>
public static class QuizOrdering
{
    /// <summary>The questions as this trainee should see them for this attempt.</summary>
    public static List<Question> For(IEnumerable<Question> questions, Quiz quiz, string userId, int attemptNumber)
    {
        var ordered = questions.OrderBy(q => q.Order).ThenBy(q => q.Id).ToList();
        if (!quiz.ShuffleQuestions || ordered.Count < 2) return ordered;

        return ordered
            .OrderBy(q => Key(q.Id, quiz.Id, userId, attemptNumber), StringComparer.Ordinal)
            .ThenBy(q => q.Id)                      // deterministic tie-break
            .ToList();
    }

    /// <summary>Replay a recorded order (from <see cref="QuizAttempt.QuestionOrder"/>). Questions
    /// not named in it — added to the paper after the attempt — go last rather than disappearing,
    /// so a review never silently drops a question.</summary>
    public static List<Question> Replay(IEnumerable<Question> questions, string? recordedOrder)
    {
        var all = questions.ToList();
        if (string.IsNullOrWhiteSpace(recordedOrder)) return all.OrderBy(q => q.Order).ThenBy(q => q.Id).ToList();

        var rank = recordedOrder.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select((s, i) => (ok: int.TryParse(s, out var id), id, i))
            .Where(x => x.ok)
            .ToDictionary(x => x.id, x => x.i);

        return all.OrderBy(q => rank.TryGetValue(q.Id, out var i) ? i : int.MaxValue)
                  .ThenBy(q => q.Order).ThenBy(q => q.Id).ToList();
    }

    public static string Record(IEnumerable<Question> ordered) =>
        string.Join(',', ordered.Select(q => q.Id));

    /// <summary>A stable pseudo-random sort key. SHA-256 of the tuple gives a well-distributed
    /// shuffle that is reproducible from the same inputs on any machine and after a restart —
    /// which <c>Random</c> without a fixed seed, or <c>GetHashCode</c> (randomised per process
    /// for strings in .NET), would not be.</summary>
    private static string Key(int questionId, int quizId, string userId, int attemptNumber)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{quizId}:{userId}:{attemptNumber}:{questionId}"));
        return Convert.ToHexString(bytes);
    }
}

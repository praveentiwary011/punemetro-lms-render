using System.Text.Json;
using LMS.Web.Data;
using LMS.Web.Models;

namespace LMS.Web.Services;

/// <summary>
/// Builds and stores xAPI statements. The LMS emits statements for its own
/// events (lessons, quizzes, completions) and stores statements received
/// from SCORM/cmi5 content through the LRS endpoints.
/// </summary>
public static class Xapi
{
    public const string VerbCompleted = "http://adlnet.gov/expapi/verbs/completed";
    public const string VerbPassed = "http://adlnet.gov/expapi/verbs/passed";
    public const string VerbFailed = "http://adlnet.gov/expapi/verbs/failed";
    public const string VerbLaunched = "http://adlnet.gov/expapi/verbs/launched";
    public const string VerbAttempted = "http://adlnet.gov/expapi/verbs/attempted";

    /// <summary>Data Protection purpose string for the signed cmi5 launch token that
    /// authenticates a learner to the xAPI LRS. Shared by the minting (ScormController)
    /// and validating (XapiController) sides.</summary>
    public const string LaunchTokenPurpose = "LMS.cmi5.launch-token.v1";

    public static void Emit(AppDbContext db, ApplicationUser actor, string verbId, string verbDisplay,
        string activityId, string activityName, double? scoreScaled = null, string? registration = null)
    {
        var id = Guid.NewGuid().ToString();
        var statement = new Dictionary<string, object?>
        {
            ["id"] = id,
            ["actor"] = new Dictionary<string, object?>
            {
                ["objectType"] = "Agent",
                ["name"] = actor.FullName,
                ["account"] = new { homePage = "https://lms.punemetro.in", name = actor.Email }
            },
            ["verb"] = new Dictionary<string, object?>
            {
                ["id"] = verbId,
                ["display"] = new Dictionary<string, string> { ["en-US"] = verbDisplay }
            },
            ["object"] = new Dictionary<string, object?>
            {
                ["objectType"] = "Activity",
                ["id"] = activityId,
                ["definition"] = new Dictionary<string, object?>
                {
                    ["name"] = new Dictionary<string, string> { ["en-US"] = activityName }
                }
            },
            ["timestamp"] = DateTime.UtcNow.ToString("o")
        };
        if (scoreScaled != null)
            statement["result"] = new Dictionary<string, object?> { ["score"] = new { scaled = Math.Round(scoreScaled.Value, 4) } };
        if (registration != null)
            statement["context"] = new Dictionary<string, object?> { ["registration"] = registration };

        db.XapiStatements.Add(new XapiStatementRecord
        {
            Id = id,
            StatementJson = JsonSerializer.Serialize(statement),
            ActorName = actor.FullName,
            ActorAccount = actor.Email ?? "",
            Verb = verbId,
            ActivityId = activityId,
            Registration = registration
        });
    }

    /// <summary>Store a raw statement received from content via the LRS endpoint.</summary>
    public static string Store(AppDbContext db, JsonElement statement)
    {
        var id = statement.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? Guid.NewGuid().ToString() : Guid.NewGuid().ToString();
        string actorName = "", actorAccount = "", verb = "", activityId = "";
        string? registration = null;

        if (statement.TryGetProperty("actor", out var actor))
        {
            if (actor.TryGetProperty("name", out var n)) actorName = n.GetString() ?? "";
            if (actor.TryGetProperty("account", out var acc) && acc.TryGetProperty("name", out var an)) actorAccount = an.GetString() ?? "";
            else if (actor.TryGetProperty("mbox", out var mb)) actorAccount = mb.GetString() ?? "";
        }
        if (statement.TryGetProperty("verb", out var v) && v.TryGetProperty("id", out var vid)) verb = vid.GetString() ?? "";
        if (statement.TryGetProperty("object", out var obj) && obj.TryGetProperty("id", out var oid)) activityId = oid.GetString() ?? "";
        if (statement.TryGetProperty("context", out var ctx) && ctx.TryGetProperty("registration", out var reg)) registration = reg.GetString();

        if (!db.XapiStatements.Any(s => s.Id == id))
        {
            db.XapiStatements.Add(new XapiStatementRecord
            {
                Id = id.Length <= 36 ? id : id[..36],
                StatementJson = statement.GetRawText(),
                ActorName = actorName,
                ActorAccount = actorAccount,
                Verb = verb,
                ActivityId = activityId.Length <= 400 ? activityId : activityId[..400],
                Registration = registration
            });
        }
        return id;
    }
}

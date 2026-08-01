using System.Globalization;
using System.Text.Json;
using LMS.Web.Models;

namespace LMS.Web.Services.Migration;

/// <summary>One LMS field an extract can be mapped onto.</summary>
public record MigrationField(string Name, string Label, bool Required, string Help);

/// <summary>How a source value is converted before use (§MIG-02). These exist because raw
/// imports fail on the client's formatting, not on their data: a date written dd/MM/yyyy read
/// as MM/dd/yyyy is the classic silent corruption, and every source system has its own words
/// for the same role.</summary>
public enum MigrationTransform { None = 0, DateFormat = 1, ValueMap = 2, FirstWord = 3, LastWords = 4, Upper = 5, Lower = 6, Constant = 7 }

/// <summary>field → source column + optional transform.</summary>
public class FieldMap
{
    public string Column { get; set; } = "";
    public MigrationTransform Transform { get; set; }
    /// <summary>Date format string, "from=to;from=to" value map, or the constant value.</summary>
    public string? Arg { get; set; }
}

public static class MigrationSchema
{
    public static IReadOnlyList<MigrationField> Fields(MigrationEntity e) => e switch
    {
        MigrationEntity.Users => new[]
        {
            new MigrationField("ExternalId", "Source ID", false,
                "The client's own identifier. Strongly recommended — without it a re-run can only match on email."),
            new MigrationField("Email", "Email", true, "Unique per tenant; also the sign-in identifier."),
            new MigrationField("FullName", "Full name", true, "Use a transform to split a combined name column."),
            new MigrationField("Department", "Department", false, "Reporting dimension."),
            new MigrationField("Role", "Role", false, "Mapped to a company role; defaults to Trainee."),
            new MigrationField("IsActive", "Active", false, "Defaults to true. Import leavers as inactive rather than omitting them, so their history stays attributable."),
        },
        MigrationEntity.Courses => new[]
        {
            new MigrationField("ExternalId", "Source ID", false, "The client's own identifier."),
            new MigrationField("Code", "Course code", true, "What enrolment rows will reference."),
            new MigrationField("Title", "Title", true, ""),
            new MigrationField("Description", "Description", false, ""),
            new MigrationField("Category", "Category", false, "Created on demand if unknown."),
            new MigrationField("InstructorEmail", "Instructor email", false, "Resolved against already-migrated users."),
        },
        _ => new[]
        {
            new MigrationField("UserExternalId", "Learner source ID", false, "Either this or the learner's email is required."),
            new MigrationField("UserEmail", "Learner email", false, "Either this or the learner's source ID is required."),
            new MigrationField("CourseExternalId", "Course source ID", false, "Either this or the course code is required."),
            new MigrationField("CourseCode", "Course code", false, "Either this or the course source ID is required."),
            new MigrationField("EnrolledAt", "Enrolled on", false, "Defaults to today; supplying it preserves the real timeline."),
            new MigrationField("Status", "Status", false, "Active / Completed / Dropped."),
            new MigrationField("CompletedAt", "Completed on", false, ""),
            new MigrationField("FinalGrade", "Final grade", false, ""),
        }
    };

    /// <summary>Suggests a source column for each field by normalised name, so *Learner Email*,
    /// `email_address` and *E-Mail* all propose Email. The operator corrects what it gets wrong —
    /// the point is to make the common case one glance rather than twelve dropdowns.</summary>
    public static Dictionary<string, FieldMap> Suggest(MigrationEntity entity, IEnumerable<string> columns)
    {
        static string Norm(string s) => new(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        var cols = columns.ToList();
        var map = new Dictionary<string, FieldMap>();

        var hints = new Dictionary<string, string[]>
        {
            ["ExternalId"] = new[] { "externalid", "sourceid", "id", "userid", "courseid", "employeeid", "payrollno", "staffno" },
            ["Email"] = new[] { "email", "emailaddress", "mail", "learneremail", "useremail" },
            ["FullName"] = new[] { "fullname", "name", "learnername", "username", "displayname", "employeename" },
            ["Department"] = new[] { "department", "dept", "division", "function", "team" },
            ["Role"] = new[] { "role", "usertype", "profile", "position", "jobrole" },
            ["IsActive"] = new[] { "isactive", "active", "status", "enabled" },
            ["Code"] = new[] { "code", "coursecode", "shortname", "itemid", "coursenumber" },
            ["Title"] = new[] { "title", "coursename", "name", "fullname", "itemtitle" },
            ["Description"] = new[] { "description", "summary", "details", "about" },
            ["Category"] = new[] { "category", "topic", "subject", "curriculum" },
            ["InstructorEmail"] = new[] { "instructoremail", "trainer", "teacher", "instructor", "owner", "facilitator" },
            ["UserExternalId"] = new[] { "userid", "learnerid", "employeeid", "userexternalid", "studentid" },
            ["UserEmail"] = new[] { "email", "learneremail", "useremail", "studentemail" },
            ["CourseExternalId"] = new[] { "courseid", "itemid", "courseexternalid" },
            ["CourseCode"] = new[] { "coursecode", "code", "shortname", "itemcode" },
            ["EnrolledAt"] = new[] { "enrolledat", "enrolled", "enrolmentdate", "enrollmentdate", "startdate", "assigned" },
            ["Status"] = new[] { "status", "completionstatus", "state", "progress" },
            ["CompletedAt"] = new[] { "completedat", "completed", "completiondate", "datecompleted", "enddate" },
            ["FinalGrade"] = new[] { "finalgrade", "grade", "score", "result", "mark" },
        };

        foreach (var f in Fields(entity))
        {
            var want = hints.TryGetValue(f.Name, out var h) ? h : new[] { Norm(f.Name) };
            // Exact normalised match first, then a contains match — in hint order, so the most
            // specific alias wins over a generic one ("learneremail" before "email").
            var col = cols.FirstOrDefault(c => want.Contains(Norm(c)))
                   ?? cols.FirstOrDefault(c => want.Any(w => Norm(c).Contains(w)));
            if (col != null) map[f.Name] = new FieldMap { Column = col };
        }
        return map;
    }

    public static Dictionary<string, FieldMap> Deserialise(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, FieldMap>>(json) ?? new();

    public static string Serialise(Dictionary<string, FieldMap> map) => JsonSerializer.Serialize(map);

    /// <summary>Applies a field's mapping and transform to one source row. Returns null when the
    /// field is unmapped or the source cell is empty, so "not supplied" stays distinguishable
    /// from "supplied as blank".</summary>
    public static string? Value(Dictionary<string, FieldMap> map, Dictionary<string, string> row, string field)
    {
        if (!map.TryGetValue(field, out var fm)) return null;
        if (fm.Transform == MigrationTransform.Constant) return fm.Arg;
        if (string.IsNullOrEmpty(fm.Column) || !row.TryGetValue(fm.Column, out var raw)) return null;
        raw = raw.Trim();
        if (raw.Length == 0) return null;

        switch (fm.Transform)
        {
            case MigrationTransform.Upper: return raw.ToUpperInvariant();
            case MigrationTransform.Lower: return raw.ToLowerInvariant();
            case MigrationTransform.FirstWord: return raw.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            case MigrationTransform.LastWords:
                var parts = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                return parts.Length > 1 ? string.Join(' ', parts.Skip(1)) : raw;
            case MigrationTransform.ValueMap:
                foreach (var pair in (fm.Arg ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    var kv = pair.Split('=', 2);
                    if (kv.Length == 2 && string.Equals(kv[0].Trim(), raw, StringComparison.OrdinalIgnoreCase))
                        return kv[1].Trim();
                }
                return raw;
            default: return raw;
        }
    }

    /// <summary>Parses a date using the operator's stated format when one is given. Guessing is
    /// deliberately avoided: 03/04/2026 is two different dates depending on the source system,
    /// and silently choosing one is how a migration corrupts a completion record.</summary>
    public static bool TryDate(Dictionary<string, FieldMap> map, Dictionary<string, string> row,
                               string field, out DateTime value)
    {
        value = default;
        var raw = Value(map, row, field);
        if (raw == null) return false;
        var fmt = map.TryGetValue(field, out var fm) && fm.Transform == MigrationTransform.DateFormat ? fm.Arg : null;
        if (!string.IsNullOrWhiteSpace(fmt))
            return DateTime.TryParseExact(raw, fmt, CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
        return DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
    }
}

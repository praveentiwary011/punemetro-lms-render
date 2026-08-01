namespace LMS.Web.Models;

/// <summary>What a migration job is importing. The order of the values is the dependency
/// order enforced by the wizard (§MIG-07) — you cannot import enrolments before the users
/// and courses they reference exist.</summary>
public enum MigrationEntity { Users = 0, Courses = 1, Enrolments = 2 }

/// <summary>Where the client's extract came from. Selects the starting column map; it does
/// not change the import path, so there is one validated route rather than several.</summary>
public enum MigrationSource { GenericCsv = 0, Excel = 1, Moodle = 2, SuccessFactors = 3, Cornerstone = 4, Other = 5 }

public enum MigrationStatus { Uploaded = 0, Mapped = 1, Validated = 2, Committed = 3, Failed = 4 }

/// <summary>Outcome of one staged row. Skipped and Error are deliberately distinct: an
/// operator must be able to tell "already done" from "rejected" (§MIG-04).</summary>
public enum MigrationRowStatus { Pending = 0, WillInsert = 1, WillUpdate = 2, Skipped = 3, Error = 4 }

/// <summary>One upload, for one tenant, of one entity type (§MIG-01).</summary>
public class MigrationJob
{
    public int Id { get; set; }
    public int OrganisationId { get; set; }
    public Organisation? Organisation { get; set; }
    public MigrationEntity EntityType { get; set; }
    public MigrationSource SourceSystem { get; set; }
    public string FileName { get; set; } = "";
    public int RowCount { get; set; }
    public MigrationStatus Status { get; set; }
    public int Inserted { get; set; }
    public int Updated { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
    public string CreatedById { get; set; } = "";
    public ApplicationUser? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ValidatedAt { get; set; }
    public DateTime? CommittedAt { get; set; }
}

/// <summary>The saved column map, so a repeat run needs only an upload (§MIG-03).
/// One per tenant per entity.</summary>
public class MigrationMapping
{
    public int Id { get; set; }
    public int OrganisationId { get; set; }
    public Organisation? Organisation { get; set; }
    public MigrationEntity EntityType { get; set; }
    /// <summary>field → { column, transform, arg } as JSON.</summary>
    public string MapJson { get; set; } = "{}";
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>A staged source row. Validation writes its verdict here and nothing is applied
/// until commit, which is what makes the dry run real rather than an estimate (§MIG-04).</summary>
public class MigrationRow
{
    public int Id { get; set; }
    public int MigrationJobId { get; set; }
    public MigrationJob? MigrationJob { get; set; }
    /// <summary>Line number in the uploaded file, so an error points at something the
    /// operator can actually find in the client's extract.</summary>
    public int RowNumber { get; set; }
    public string RawJson { get; set; } = "{}";
    public MigrationRowStatus Status { get; set; }
    public string? Message { get; set; }
    /// <summary>The LMS record created or updated, once committed.</summary>
    public string? TargetId { get; set; }
}

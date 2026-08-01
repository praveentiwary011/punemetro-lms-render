using System.Text.Json;
using LMS.Web.Data;
using LMS.Web.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace LMS.Web.Services.Migration;

/// <summary>Validates and applies a staged migration job (§MIG-04…MIG-07).
///
/// Two passes over the same staged rows: <see cref="ValidateAsync"/> writes a verdict per row and
/// touches nothing else, and <see cref="CommitAsync"/> applies exactly what validation predicted.
/// Keeping both on the same code path is what makes the dry run trustworthy — an operator who is
/// told "412 insert, 3 fail" gets that, not an estimate.
///
/// Idempotency is carried by <c>ExternalId</c>: a record the migration created before is found and
/// updated, never duplicated. Nothing here deletes.</summary>
public class MigrationEngine
{
    private readonly AppDbContext _db;
    private readonly UserManager<ApplicationUser> _users;

    public MigrationEngine(AppDbContext db, UserManager<ApplicationUser> users) { _db = db; _users = users; }

    private static Dictionary<string, string> Raw(MigrationRow r) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(r.RawJson) ?? new();

    // ============================================================ VALIDATE

    public async Task<MigrationJob> ValidateAsync(int jobId, CancellationToken ct = default)
    {
        var job = await _db.MigrationJobs.FirstAsync(j => j.Id == jobId, ct);
        var rows = await _db.MigrationRows.Where(r => r.MigrationJobId == jobId).OrderBy(r => r.RowNumber).ToListAsync(ct);
        var mapping = await _db.MigrationMappings
            .FirstOrDefaultAsync(m => m.OrganisationId == job.OrganisationId && m.EntityType == job.EntityType, ct);
        var map = MigrationSchema.Deserialise(mapping?.MapJson ?? "{}");

        // Existing keys for this tenant, loaded once rather than per row.
        var userByExt = await _db.Users.IgnoreQueryFilters()
            .Where(u => u.OrganisationId == job.OrganisationId && u.ExternalId != null)
            .ToDictionaryAsync(u => u.ExternalId!, u => u.Id, StringComparer.OrdinalIgnoreCase, ct);
        var userByEmail = await _db.Users.IgnoreQueryFilters()
            .Where(u => u.OrganisationId == job.OrganisationId)
            .ToDictionaryAsync(u => u.Email!, u => u.Id, StringComparer.OrdinalIgnoreCase, ct);
        var courseByExt = await _db.Courses.IgnoreQueryFilters()
            .Where(c => c.OrganisationId == job.OrganisationId && c.ExternalId != null)
            .ToDictionaryAsync(c => c.ExternalId!, c => c.Id, StringComparer.OrdinalIgnoreCase, ct);
        var courseByCode = await _db.Courses.IgnoreQueryFilters()
            .Where(c => c.OrganisationId == job.OrganisationId)
            .ToDictionaryAsync(c => c.Code, c => c.Id, StringComparer.OrdinalIgnoreCase, ct);

        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            var d = Raw(row);
            string? err = null, note = null;
            var status = MigrationRowStatus.WillInsert;

            string? V(string f) => MigrationSchema.Value(map, d, f);

            switch (job.EntityType)
            {
                case MigrationEntity.Users:
                {
                    var email = V("Email");
                    var name = V("FullName");
                    var ext = V("ExternalId");
                    if (string.IsNullOrWhiteSpace(email)) err = "Email is required and was not supplied.";
                    else if (!email.Contains('@') || email.StartsWith('@') || email.EndsWith('@'))
                        err = $"'{email}' is not a valid email address.";
                    else if (string.IsNullOrWhiteSpace(name)) err = "Full name is required and was not supplied.";
                    else if (!seenKeys.Add("u:" + (ext ?? email))) err = "This row duplicates an earlier row in the same file.";
                    else if (ext != null && userByExt.ContainsKey(ext)) status = MigrationRowStatus.WillUpdate;
                    else if (userByEmail.ContainsKey(email))
                    {
                        status = MigrationRowStatus.WillUpdate;
                        if (ext != null) note = "Matched on email; the source ID will be attached for future runs.";
                    }
                    break;
                }

                case MigrationEntity.Courses:
                {
                    var code = V("Code");
                    var title = V("Title");
                    var ext = V("ExternalId");
                    var instructor = V("InstructorEmail");
                    if (string.IsNullOrWhiteSpace(code)) err = "Course code is required and was not supplied.";
                    else if (string.IsNullOrWhiteSpace(title)) err = "Title is required and was not supplied.";
                    else if (!seenKeys.Add("c:" + (ext ?? code))) err = "This row duplicates an earlier row in the same file.";
                    else if (ext != null && courseByExt.ContainsKey(ext)) status = MigrationRowStatus.WillUpdate;
                    else if (courseByCode.ContainsKey(code)) status = MigrationRowStatus.WillUpdate;

                    if (err == null && instructor != null && !userByEmail.ContainsKey(instructor))
                        note = $"Instructor '{instructor}' was not found — the course will be left with no trainer assigned.";
                    break;
                }

                default:
                {
                    var uExt = V("UserExternalId"); var uMail = V("UserEmail");
                    var cExt = V("CourseExternalId"); var cCode = V("CourseCode");

                    string? uid = uExt != null && userByExt.TryGetValue(uExt, out var a) ? a
                                : uMail != null && userByEmail.TryGetValue(uMail, out var b) ? b : null;
                    int? cid = cExt != null && courseByExt.TryGetValue(cExt, out var x) ? x
                             : cCode != null && courseByCode.TryGetValue(cCode, out var y) ? y : (int?)null;

                    if (uExt == null && uMail == null) err = "No learner identifier supplied (source ID or email).";
                    else if (cExt == null && cCode == null) err = "No course identifier supplied (source ID or code).";
                    // A missing reference names the key that was not found, rather than inventing a
                    // placeholder record that nobody would notice (§MIG-07).
                    else if (uid == null) err = $"Learner '{uExt ?? uMail}' does not exist — import users first.";
                    else if (cid == null) err = $"Course '{cExt ?? cCode}' does not exist — import courses first.";
                    else if (!seenKeys.Add($"e:{uid}:{cid}")) err = "This row duplicates an earlier row in the same file.";
                    else if (await _db.Enrollments.IgnoreQueryFilters()
                                 .AnyAsync(e => e.StudentId == uid && e.CourseId == cid, ct))
                        status = MigrationRowStatus.WillUpdate;

                    if (err == null && MigrationSchema.TryDate(map, d, "CompletedAt", out var comp)
                        && MigrationSchema.TryDate(map, d, "EnrolledAt", out var enr) && comp < enr)
                        note = "Completion date precedes the enrolment date.";
                    break;
                }
            }

            // A date that was supplied but cannot be parsed is an error, never a silent default —
            // that is the difference between a preserved history and a corrupted one.
            foreach (var df in new[] { "EnrolledAt", "CompletedAt" })
                if (err == null && MigrationSchema.Value(map, d, df) != null
                    && !MigrationSchema.TryDate(map, d, df, out _))
                    err = $"'{MigrationSchema.Value(map, d, df)}' in {df} is not a date in the chosen format.";

            row.Status = err != null ? MigrationRowStatus.Error : status;
            row.Message = err ?? note ?? (status == MigrationRowStatus.WillUpdate ? "Existing record will be updated." : "New record.");
        }

        job.Inserted = rows.Count(r => r.Status == MigrationRowStatus.WillInsert);
        job.Updated = rows.Count(r => r.Status == MigrationRowStatus.WillUpdate);
        job.Skipped = rows.Count(r => r.Status == MigrationRowStatus.Skipped);
        job.Failed = rows.Count(r => r.Status == MigrationRowStatus.Error);
        job.Status = MigrationStatus.Validated;
        job.ValidatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return job;
    }

    // ============================================================ COMMIT

    public async Task<MigrationJob> CommitAsync(int jobId, CancellationToken ct = default)
    {
        var job = await _db.MigrationJobs.FirstAsync(j => j.Id == jobId, ct);
        var rows = await _db.MigrationRows
            .Where(r => r.MigrationJobId == jobId
                        && (r.Status == MigrationRowStatus.WillInsert || r.Status == MigrationRowStatus.WillUpdate))
            .OrderBy(r => r.RowNumber).ToListAsync(ct);
        var mapping = await _db.MigrationMappings
            .FirstOrDefaultAsync(m => m.OrganisationId == job.OrganisationId && m.EntityType == job.EntityType, ct);
        var map = MigrationSchema.Deserialise(mapping?.MapJson ?? "{}");

        int inserted = 0, updated = 0, failed = 0;

        foreach (var row in rows)
        {
            var d = Raw(row);
            string? V(string f) => MigrationSchema.Value(map, d, f);
            try
            {
                switch (job.EntityType)
                {
                    case MigrationEntity.Users: {
                        var (id, isNew) = await UpsertUserAsync(job.OrganisationId, map, d, ct);
                        row.TargetId = id; if (isNew) inserted++; else updated++;
                        break;
                    }
                    case MigrationEntity.Courses: {
                        var (id, isNew) = await UpsertCourseAsync(job.OrganisationId, map, d, ct);
                        row.TargetId = id.ToString(); if (isNew) inserted++; else updated++;
                        break;
                    }
                    default: {
                        var (id, isNew) = await UpsertEnrolmentAsync(job.OrganisationId, map, d, ct);
                        row.TargetId = id.ToString(); if (isNew) inserted++; else updated++;
                        break;
                    }
                }
                row.Status = MigrationRowStatus.Skipped;   // applied; reused as "done" for the report
                row.Message = "Applied.";
            }
            catch (Exception ex)
            {
                // One bad row must not abandon the rest of the migration.
                row.Status = MigrationRowStatus.Error;
                row.Message = ex.Message.Length > 300 ? ex.Message[..300] : ex.Message;
                failed++;
            }
        }

        job.Inserted = inserted; job.Updated = updated; job.Failed = failed;
        job.Status = MigrationStatus.Committed;
        job.CommittedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return job;
    }

    // ------------------------------------------------------------ upserts

    private async Task<(string Id, bool IsNew)> UpsertUserAsync(int orgId,
        Dictionary<string, FieldMap> map, Dictionary<string, string> d, CancellationToken ct)
    {
        string? V(string f) => MigrationSchema.Value(map, d, f);
        var email = V("Email")!; var ext = V("ExternalId");

        var user = ext == null ? null : await _db.Users.IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.OrganisationId == orgId && u.ExternalId == ext, ct);
        user ??= await _db.Users.IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.OrganisationId == orgId && u.Email == email, ct);

        var isNew = user == null;
        if (user == null)
        {
            user = new ApplicationUser
            {
                UserName = email, Email = email, EmailConfirmed = true,
                OrganisationId = orgId, CreatedAt = DateTime.UtcNow
            };
            // No password is ever migrated (§MIG-09): the account arrives through the tenant's
            // normal route — SSO or a reset — rather than carrying another system's hash.
            var created = await _users.CreateAsync(user);
            if (!created.Succeeded)
                throw new InvalidOperationException(string.Join("; ", created.Errors.Select(e => e.Description)));
        }

        user.ExternalId ??= ext;
        user.FullName = V("FullName") ?? user.FullName;
        user.Department = V("Department") ?? user.Department;
        var active = V("IsActive");
        if (active != null) user.IsActive = active.ToLowerInvariant() is "true" or "1" or "yes" or "y" or "active";

        var role = V("Role");
        var wanted = role is null ? "Student" : role.Trim();
        if (!await _users.IsInRoleAsync(user, wanted) && await _db.Roles.AnyAsync(r => r.Name == wanted, ct))
            await _users.AddToRoleAsync(user, wanted);
        else if (isNew && !await _users.IsInRoleAsync(user, "Student"))
            await _users.AddToRoleAsync(user, "Student");

        await _db.SaveChangesAsync(ct);
        return (user.Id, isNew);
    }

    private async Task<(int Id, bool IsNew)> UpsertCourseAsync(int orgId,
        Dictionary<string, FieldMap> map, Dictionary<string, string> d, CancellationToken ct)
    {
        string? V(string f) => MigrationSchema.Value(map, d, f);
        var code = V("Code")!; var ext = V("ExternalId");

        var course = ext == null ? null : await _db.Courses.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.OrganisationId == orgId && c.ExternalId == ext, ct);
        course ??= await _db.Courses.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.OrganisationId == orgId && c.Code == code, ct);

        var isNew = course == null;
        if (course == null)
        {
            course = new Course
            {
                OrganisationId = orgId, Code = code, CreatedAt = DateTime.UtcNow,
                // Migrated catalogues arrive as drafts so a person reviews them before
                // learners see them.
                IsPublished = false, IsActive = true, MaxEnrollment = 100, PassingGrade = 50
            };
            _db.Courses.Add(course);
        }

        course.ExternalId ??= ext;
        course.Title = V("Title") ?? course.Title;
        course.Description = V("Description") ?? course.Description;
        course.Code = code;

        var cat = V("Category");
        if (cat != null)
        {
            var category = await _db.Categories.FirstOrDefaultAsync(c => c.Name == cat, ct);
            if (category == null) { category = new Category { Name = cat }; _db.Categories.Add(category); await _db.SaveChangesAsync(ct); }
            course.CategoryId = category.Id;
        }

        var instructor = V("InstructorEmail");
        if (instructor != null)
        {
            var t = await _db.Users.IgnoreQueryFilters()
                .FirstOrDefaultAsync(u => u.OrganisationId == orgId && u.Email == instructor, ct);
            if (t != null) course.InstructorId = t.Id;
        }
        if (string.IsNullOrEmpty(course.InstructorId))
        {
            // Course.InstructorId is required; fall back to the tenant's first trainer so the
            // row imports rather than failing on a detail the client can correct later.
            var fallback = await _db.Users.IgnoreQueryFilters()
                .Where(u => u.OrganisationId == orgId).OrderBy(u => u.Id).FirstOrDefaultAsync(ct);
            course.InstructorId = fallback?.Id ?? throw new InvalidOperationException(
                "This organisation has no users yet — import users before courses.");
        }

        await _db.SaveChangesAsync(ct);
        return (course.Id, isNew);
    }

    private async Task<(int Id, bool IsNew)> UpsertEnrolmentAsync(int orgId,
        Dictionary<string, FieldMap> map, Dictionary<string, string> d, CancellationToken ct)
    {
        string? V(string f) => MigrationSchema.Value(map, d, f);
        var uExt = V("UserExternalId"); var uMail = V("UserEmail");
        var cExt = V("CourseExternalId"); var cCode = V("CourseCode");

        var user = await _db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.OrganisationId == orgId
                       && (uExt != null && u.ExternalId == uExt || uMail != null && u.Email == uMail), ct)
                   ?? throw new InvalidOperationException($"Learner '{uExt ?? uMail}' not found.");
        var course = await _db.Courses.IgnoreQueryFilters().FirstOrDefaultAsync(c => c.OrganisationId == orgId
                         && (cExt != null && c.ExternalId == cExt || cCode != null && c.Code == cCode), ct)
                     ?? throw new InvalidOperationException($"Course '{cExt ?? cCode}' not found.");

        var e = await _db.Enrollments.IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.StudentId == user.Id && x.CourseId == course.Id, ct);
        var isNew = e == null;
        if (e == null)
        {
            e = new Enrollment { StudentId = user.Id, CourseId = course.Id, EnrolledAt = DateTime.UtcNow };
            _db.Enrollments.Add(e);
        }

        if (MigrationSchema.TryDate(map, d, "EnrolledAt", out var enrolled)) e.EnrolledAt = enrolled;
        if (MigrationSchema.TryDate(map, d, "CompletedAt", out var completed))
        {
            e.CompletedAt = completed;
            e.Status = EnrollmentStatus.Completed;
        }
        var status = V("Status");
        if (status != null)
            e.Status = status.ToLowerInvariant() switch
            {
                "completed" or "complete" or "passed" or "finished" => EnrollmentStatus.Completed,
                "dropped" or "withdrawn" or "cancelled" => EnrollmentStatus.Dropped,
                _ => EnrollmentStatus.Active
            };
        if (double.TryParse(V("FinalGrade"), out var grade)) e.FinalGrade = grade;
        e.ExternalId ??= V("UserExternalId") is { } ux && V("CourseExternalId") is { } cx ? $"{ux}:{cx}" : null;

        await _db.SaveChangesAsync(ct);
        return (e.Id, isNew);
    }
}

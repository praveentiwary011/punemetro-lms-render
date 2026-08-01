using System.Text;
using System.Text.Json;
using LMS.Web.Data;
using LMS.Web.Models;
using LMS.Web.Services;
using LMS.Web.Services.Migration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LMS.Web.Controllers;

/// <summary>The Data Migration wizard (LMS-MIG-001).
///
/// Super User only (§MIG-11): the tool writes people and historical records into a named tenant,
/// and a mapping error would attribute one client's data to another. That is the same reasoning
/// that places tenant onboarding and the platform mail default with the Super User.</summary>
[Authorize(Roles = "SuperUser")]
public class MigrationController : Controller
{
    private readonly AppDbContext _db;
    private readonly MigrationEngine _engine;
    private readonly IWebHostEnvironment _env;

    public MigrationController(AppDbContext db, MigrationEngine engine, IWebHostEnvironment env)
    { _db = db; _engine = engine; _env = env; }

    /// <summary>Step 1 — target tenant and entity, plus the history of previous jobs.</summary>
    public async Task<IActionResult> Index()
    {
        ViewBag.Organisations = await _db.Organisations.IgnoreQueryFilters()
            .OrderBy(o => o.Name).ToListAsync();
        ViewBag.Jobs = await _db.MigrationJobs.Include(j => j.Organisation).Include(j => j.CreatedBy)
            .OrderByDescending(j => j.Id).Take(15).ToListAsync();
        // Dependency order (§MIG-07): report what each tenant already holds, so the operator can
        // see whether the prerequisites for enrolments are actually in place.
        ViewBag.Counts = await _db.Organisations.IgnoreQueryFilters().Select(o => new
        {
            o.Id,
            Users = _db.Users.IgnoreQueryFilters().Count(u => u.OrganisationId == o.Id),
            Courses = _db.Courses.IgnoreQueryFilters().Count(c => c.OrganisationId == o.Id)
        }).ToDictionaryAsync(x => x.Id, x => (x.Users, x.Courses));
        return View();
    }

    /// <summary>Step 2 — upload the extract and stage every row (§MIG-04). Nothing is written to
    /// the tenant here; rows land in MigrationRow and are validated from there.</summary>
    [HttpPost, ValidateAntiForgeryToken, RequestSizeLimit(50_000_000)]
    public async Task<IActionResult> Upload(int organisationId, MigrationEntity entityType,
        MigrationSource sourceSystem, IFormFile? file, IFormFile? materialZip)
    {
        if (file is not { Length: > 0 }) { TempData["Err"] = "Choose a CSV or Excel file to upload."; return RedirectToAction("Index"); }
        if (!TabularReader.IsSupported(file.FileName))
        { TempData["Err"] = "Only .csv, .tsv and .xlsx extracts are supported."; return RedirectToAction("Index"); }

        List<string> columns; List<Dictionary<string, string>> rows;
        try
        {
            using var s = file.OpenReadStream();
            (columns, rows) = TabularReader.Read(s, file.FileName);
        }
        catch (Exception ex)
        {
            TempData["Err"] = $"That file could not be read: {ex.Message}";
            return RedirectToAction("Index");
        }
        if (rows.Count == 0) { TempData["Err"] = "The file has a header but no data rows."; return RedirectToAction("Index"); }

        // Material rows name files; without the archive there is nothing to name (§MIG-08).
        if (entityType == MigrationEntity.CourseMaterial && materialZip is not { Length: > 0 })
        {
            TempData["Err"] = "Course material also needs the ZIP archive holding the files the extract refers to.";
            return RedirectToAction("Index");
        }
        if (materialZip is { Length: > 0 } && !materialZip.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        { TempData["Err"] = "The material archive must be a .zip file."; return RedirectToAction("Index"); }

        var job = new MigrationJob
        {
            OrganisationId = organisationId, EntityType = entityType, SourceSystem = sourceSystem,
            FileName = file.FileName, RowCount = rows.Count, Status = MigrationStatus.Uploaded,
            CreatedById = User.GetUserId()
        };
        _db.MigrationJobs.Add(job);
        await _db.SaveChangesAsync();

        // The archive is stored under App_Data — outside the web root, so it is never served —
        // and only after the size guards have cleared it.
        if (materialZip is { Length: > 0 })
        {
            var path = MaterialPayload.PathFor(_env, job.Id);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var fs = System.IO.File.Create(path))
                await materialZip.CopyToAsync(fs);

            var (ok, zipErr) = MaterialPayload.Check(path);
            if (!ok)
            {
                System.IO.File.Delete(path);
                _db.MigrationJobs.Remove(job);
                await _db.SaveChangesAsync();
                TempData["Err"] = zipErr;
                return RedirectToAction("Index");
            }
            job.PayloadPath = path;
            await _db.SaveChangesAsync();
        }

        int n = 1;
        foreach (var r in rows)
            _db.MigrationRows.Add(new MigrationRow
            { MigrationJobId = job.Id, RowNumber = ++n, RawJson = JsonSerializer.Serialize(r) });
        await _db.SaveChangesAsync();

        // Reuse the saved map for this tenant+entity if there is one (§MIG-03); otherwise suggest.
        var saved = await _db.MigrationMappings
            .FirstOrDefaultAsync(m => m.OrganisationId == organisationId && m.EntityType == entityType);
        if (saved == null)
        {
            _db.MigrationMappings.Add(new MigrationMapping
            {
                OrganisationId = organisationId, EntityType = entityType,
                MapJson = MigrationSchema.Serialise(MigrationSchema.Suggest(entityType, columns))
            });
            await _db.SaveChangesAsync();
        }

        Notifier.Audit(_db, User.GetUserId(), User.Identity!.Name ?? "", "MigrationUpload",
            $"{job.Organisation?.Name ?? $"org {organisationId}"} · {entityType} · {file.FileName} · {rows.Count} row(s)");
        await _db.SaveChangesAsync();
        return RedirectToAction("Map", new { id = job.Id });
    }

    /// <summary>Step 3 — map source columns to LMS fields.</summary>
    public async Task<IActionResult> Map(int id)
    {
        var job = await _db.MigrationJobs.Include(j => j.Organisation).FirstOrDefaultAsync(j => j.Id == id);
        if (job == null) return NotFound();
        var first = await _db.MigrationRows.Where(r => r.MigrationJobId == id).OrderBy(r => r.RowNumber)
            .Take(5).ToListAsync();
        var sample = first.Select(r => JsonSerializer.Deserialize<Dictionary<string, string>>(r.RawJson)!).ToList();

        var mapping = await _db.MigrationMappings
            .FirstOrDefaultAsync(m => m.OrganisationId == job.OrganisationId && m.EntityType == job.EntityType);
        ViewBag.Columns = sample.FirstOrDefault()?.Keys.ToList() ?? new List<string>();
        ViewBag.Sample = sample;
        ViewBag.Map = MigrationSchema.Deserialise(mapping?.MapJson ?? "{}");
        ViewBag.Fields = MigrationSchema.Fields(job.EntityType);
        return View(job);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveMap(int id, [FromForm] Dictionary<string, string> column,
        [FromForm] Dictionary<string, MigrationTransform> transform, [FromForm] Dictionary<string, string> arg)
    {
        var job = await _db.MigrationJobs.FirstOrDefaultAsync(j => j.Id == id);
        if (job == null) return NotFound();

        var map = new Dictionary<string, FieldMap>();
        foreach (var f in MigrationSchema.Fields(job.EntityType))
        {
            var col = column.TryGetValue(f.Name, out var c) ? c : "";
            var tr = transform.TryGetValue(f.Name, out var t) ? t : MigrationTransform.None;
            if (string.IsNullOrWhiteSpace(col) && tr != MigrationTransform.Constant) continue;
            map[f.Name] = new FieldMap { Column = col, Transform = tr, Arg = arg.TryGetValue(f.Name, out var a) ? a : null };
        }

        var mapping = await _db.MigrationMappings
            .FirstOrDefaultAsync(m => m.OrganisationId == job.OrganisationId && m.EntityType == job.EntityType);
        if (mapping == null)
        {
            mapping = new MigrationMapping { OrganisationId = job.OrganisationId, EntityType = job.EntityType };
            _db.MigrationMappings.Add(mapping);
        }
        mapping.MapJson = MigrationSchema.Serialise(map);
        mapping.UpdatedAt = DateTime.UtcNow;
        job.Status = MigrationStatus.Mapped;
        await _db.SaveChangesAsync();

        await _engine.ValidateAsync(id);
        return RedirectToAction("Review", new { id });
    }

    /// <summary>Step 4 — the dry run's verdict, per row.</summary>
    public async Task<IActionResult> Review(int id)
    {
        var job = await _db.MigrationJobs.Include(j => j.Organisation).FirstOrDefaultAsync(j => j.Id == id);
        if (job == null) return NotFound();
        ViewBag.Rows = await _db.MigrationRows.Where(r => r.MigrationJobId == id)
            .OrderBy(r => r.Status == MigrationRowStatus.Error ? 0 : 1).ThenBy(r => r.RowNumber)
            .Take(300).ToListAsync();
        return View(job);
    }

    /// <summary>Re-runs validation after the client corrects the extract or the map changes.</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Revalidate(int id) { await _engine.ValidateAsync(id); return RedirectToAction("Review", new { id }); }

    /// <summary>Step 5 — apply what validation predicted.</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Commit(int id)
    {
        var job = await _db.MigrationJobs.Include(j => j.Organisation).FirstOrDefaultAsync(j => j.Id == id);
        if (job == null) return NotFound();
        if (job.Status == MigrationStatus.Committed)
        { TempData["Err"] = "This job has already been committed. Upload the corrected extract as a new job."; return RedirectToAction("Review", new { id }); }

        MigrationJob done;
        try
        {
            done = await _engine.CommitAsync(id);
        }
        catch (Exception ex)
        {
            // An operator mid-migration needs to be told what the database refused and that
            // nothing was written — not handed a stack trace.
            Notifier.Audit(_db, User.GetUserId(), User.Identity!.Name ?? "", "MigrationCommitFailed",
                $"{job.Organisation?.Name} · {job.EntityType} · {ex.Message}");
            await _db.SaveChangesAsync();
            TempData["Err"] = ex.Message;
            return RedirectToAction("Review", new { id });
        }

        Notifier.Audit(_db, User.GetUserId(), User.Identity!.Name ?? "", "MigrationCommit",
            $"{job.Organisation?.Name} · {job.EntityType} · inserted {done.Inserted}, updated {done.Updated}, failed {done.Failed}");
        await _db.SaveChangesAsync();
        TempData["Ok"] = $"Migration applied: {done.Inserted} inserted, {done.Updated} updated, {done.Failed} failed.";
        return RedirectToAction("Review", new { id });
    }

    /// <summary>The per-row report, for return to the client so they can correct the source (§MIG-04).</summary>
    public async Task<IActionResult> Report(int id)
    {
        var job = await _db.MigrationJobs.FirstOrDefaultAsync(j => j.Id == id);
        if (job == null) return NotFound();
        var rows = await _db.MigrationRows.Where(r => r.MigrationJobId == id).OrderBy(r => r.RowNumber).ToListAsync();

        var sb = new StringBuilder("Row,Status,Message\n");
        foreach (var r in rows)
            sb.Append(r.RowNumber).Append(',').Append(r.Status).Append(",\"")
              .Append((r.Message ?? "").Replace("\"", "\"\"")).Append("\"\n");
        return File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv",
                    $"migration-{job.EntityType}-{job.Id}-report.csv");
    }
}

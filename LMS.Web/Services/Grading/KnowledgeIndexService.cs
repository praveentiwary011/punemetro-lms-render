using LMS.Web.Data;
using LMS.Web.Models;
using Microsoft.EntityFrameworkCore;

namespace LMS.Web.Services.Grading;

/// <summary>Builds the tenant-scoped retrieval index (§AIG-02) from course material —
/// course descriptions and lesson content. Text-extracts, chunks and embeds each source;
/// re-indexing deletes+rewrites so no orphan chunks accumulate.</summary>
public class KnowledgeIndexService
{
    private readonly AppDbContext _db;
    private readonly IOllamaClient _ollama;
    private readonly ILogger<KnowledgeIndexService> _log;

    public KnowledgeIndexService(AppDbContext db, IOllamaClient ollama, ILogger<KnowledgeIndexService> log)
    { _db = db; _ollama = ollama; _log = log; }

    /// <summary>(Re)build the whole index for one organisation. Returns chunks written.</summary>
    public async Task<int> ReindexOrganisationAsync(int organisationId, CancellationToken ct = default)
    {
        // Clear existing chunks for this tenant (orphan-free rebuild).
        var existing = await _db.KnowledgeChunks.IgnoreQueryFilters()
            .Where(k => k.OrganisationId == organisationId).ToListAsync(ct);
        if (existing.Count > 0) { _db.KnowledgeChunks.RemoveRange(existing); await _db.SaveChangesAsync(ct); }

        var courses = await _db.Courses.IgnoreQueryFilters()
            .Where(c => c.OrganisationId == organisationId)
            .Select(c => new { c.Id, c.Title, c.Description }).ToListAsync(ct);
        var courseIds = courses.Select(c => c.Id).ToList();

        var lessons = await (from l in _db.Lessons.IgnoreQueryFilters()
                             join m in _db.Modules on l.ModuleId equals m.Id
                             where courseIds.Contains(m.CourseId)
                             select new { l.Id, l.Title, l.Content, l.ExtractedText, l.Type, m.CourseId })
                             .ToListAsync(ct);

        int written = 0;
        foreach (var c in courses)
            written += await IndexSourceAsync(organisationId, c.Id, KnowledgeSourceType.Lesson,
                $"course:{c.Id}", $"Course: {c.Title}", c.Description, ct);

        foreach (var l in lessons)
        {
            // Authored rich text (Text lessons).
            written += await IndexSourceAsync(organisationId, l.CourseId, KnowledgeSourceType.Lesson,
                $"lesson:{l.Id}", $"Lesson: {l.Title}", TextChunker.HtmlToText(l.Content), ct);

            // Uploaded documents and video transcripts (§AIG-14). These carry no `Content`, so
            // before this they were indexed as nothing and the material was invisible to grading.
            // Indexed under their own source type so a citation says which kind of material it came from.
            if (!string.IsNullOrWhiteSpace(l.ExtractedText))
            {
                var kind = l.Type == LessonType.Video
                    ? KnowledgeSourceType.VideoTranscript
                    : KnowledgeSourceType.Document;
                var label = l.Type == LessonType.Video ? $"Video transcript: {l.Title}" : $"Document: {l.Title}";
                written += await IndexSourceAsync(organisationId, l.CourseId, kind,
                    $"material:{l.Id}", label, l.ExtractedText, ct);
            }
        }

        await _db.SaveChangesAsync(ct);
        _log.LogInformation("Knowledge index rebuilt for org {Org}: {N} chunks.", organisationId, written);
        return written;
    }

    private async Task<int> IndexSourceAsync(int orgId, int? courseId, KnowledgeSourceType type,
        string sourceRef, string label, string? text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        int n = 0;
        foreach (var chunk in TextChunker.Chunk(text))
        {
            var vec = await _ollama.EmbedAsync(chunk, ct);
            _db.KnowledgeChunks.Add(new KnowledgeChunk
            {
                OrganisationId = orgId, CourseId = courseId, SourceType = type,
                SourceRef = sourceRef, SourceLabel = label, Text = chunk,
                Embedding = TextChunker.PackVector(vec)
            });
            n++;
        }
        return n;
    }
}

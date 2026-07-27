using LMS.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace LMS.Web.Services.Grading;

/// <summary>Retrieves the top-k reference passages for a subjective answer (§AIG-03),
/// scoped to the tenant and preferring the course's own material. Cosine similarity is
/// computed in-process so no native vector store is required (SQLite default provider).</summary>
public class RetrievalService
{
    private readonly AppDbContext _db;
    private readonly IOllamaClient _ollama;
    public RetrievalService(AppDbContext db, IOllamaClient ollama) { _db = db; _ollama = ollama; }

    public async Task<IReadOnlyList<RetrievedChunk>> RetrieveContextAsync(
        string question, string answer, int organisationId, int? courseId, int k = 5, CancellationToken ct = default)
    {
        var query = $"{question}\n{answer}";
        var qvec = await _ollama.EmbedAsync(query, ct);

        // Tenant-scoped candidate set (explicit filter; safe under any context).
        var candidates = await _db.KnowledgeChunks.IgnoreQueryFilters()
            .Where(c => c.OrganisationId == organisationId)
            .Select(c => new { c.CourseId, c.SourceLabel, c.Text, c.Embedding })
            .ToListAsync(ct);

        return candidates
            .Select(c =>
            {
                var sim = TextChunker.Cosine(qvec, TextChunker.UnpackVector(c.Embedding));
                if (courseId != null && c.CourseId == courseId) sim += 0.05; // prefer the course's own material
                return new RetrievedChunk(c.Text, c.SourceLabel, sim);
            })
            .OrderByDescending(r => r.Score)
            .Take(k)
            .ToList();
    }
}

using LMS.Web.Data;
using LMS.Web.Models;
using Microsoft.EntityFrameworkCore;

namespace LMS.Web.Services.Grading;

/// <summary>Resolves the subjective-grading inference profile from the Site Settings
/// toggle (§AIG-05). CPU (default) → Qwen2.5 7B, asynchronous; GPU → Qwen2.5 14B, synchronous.</summary>
public class GradingOptions
{
    public const string SettingKey = "GradingMode";
    public const string EmbeddingModel = "nomic-embed-text";
    public const string CpuModel = "qwen2.5:7b-instruct";
    public const string GpuModel = "qwen2.5:14b-instruct";

    private readonly AppDbContext _db;
    public GradingOptions(AppDbContext db) => _db = db;

    public async Task<GradingMode> GetModeAsync(CancellationToken ct = default)
    {
        var setting = await _db.SiteSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Key == SettingKey, ct);
        return string.Equals(setting?.Value, "Gpu", StringComparison.OrdinalIgnoreCase)
            ? GradingMode.Gpu : GradingMode.Cpu;
    }

    public static string ModelFor(GradingMode mode) => mode == GradingMode.Gpu ? GpuModel : CpuModel;

    /// <summary>GPU mode grades within the submit request; CPU mode grades on the background worker.</summary>
    public static bool IsSynchronous(GradingMode mode) => mode == GradingMode.Gpu;

    public static TimeSpan Timeout(GradingMode mode) =>
        mode == GradingMode.Gpu ? TimeSpan.FromSeconds(30) : TimeSpan.FromSeconds(90);
}

using System.IO.Compression;

namespace LMS.Web.Services.Migration;

/// <summary>The archive of course files that accompanies a material extract (§MIG-08).
///
/// CSV cannot carry binaries, so material migrates as a ZIP whose entries the extract names in a
/// path column. A ZIP is preferred over a URL column the server would fetch, because migration has
/// to work for the air-gapped installations the deployment specification supports — and because
/// fetching from the client's old system turns every import into an outbound request.
///
/// The archive arrives from outside, so it is treated as hostile: entry count and uncompressed
/// size are capped before anything is read, entry names are resolved and confined, and only the
/// file types the LMS already accepts are extracted.</summary>
public static class MaterialPayload
{
    private const int MaxEntries = 5000;
    private const long MaxUncompressed = 2L * 1024 * 1024 * 1024;   // 2 GB
    private const long MaxSingleEntry = 200L * 1024 * 1024;         // 200 MB

    /// <summary>Where a job's archive lives: under App_Data, deliberately outside the web root so
    /// an uploaded archive is never directly reachable over HTTP.</summary>
    public static string PathFor(IWebHostEnvironment env, int jobId) =>
        Path.Combine(env.ContentRootPath, "App_Data", "migration", $"job-{jobId}.zip");

    /// <summary>Rejects an archive that could exhaust the server before a single entry is read.</summary>
    public static (bool Ok, string? Error) Check(string zipPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            if (zip.Entries.Count > MaxEntries)
                return (false, $"Archive rejected: {zip.Entries.Count} entries exceeds the {MaxEntries} limit.");
            if (zip.Entries.Sum(e => e.Length) > MaxUncompressed)
                return (false, "Archive rejected: uncompressed size exceeds 2 GB.");
            return (true, null);
        }
        catch (Exception ex) { return (false, $"That archive could not be read: {ex.Message}"); }
    }

    /// <summary>Entry names as the extract must reference them, normalised to forward slashes.</summary>
    public static HashSet<string> Entries(string zipPath)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(zipPath)) return set;
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var e in zip.Entries)
            if (!string.IsNullOrEmpty(e.Name))          // skip directory entries
                set.Add(e.FullName.Replace('\\', '/'));
        return set;
    }

    /// <summary>Locates one entry and checks it is something we are willing to write out.</summary>
    private static ZipArchiveEntry? Find(ZipArchive zip, string entryPath, out string? error)
    {
        error = null;
        var wanted = entryPath.Replace('\\', '/').TrimStart('/');
        var entry = zip.Entries.FirstOrDefault(e =>
            string.Equals(e.FullName.Replace('\\', '/'), wanted, StringComparison.OrdinalIgnoreCase));
        if (entry == null) { error = $"'{entryPath}' is not in the uploaded archive."; return null; }
        if (entry.Length > MaxSingleEntry) { error = $"'{entryPath}' exceeds the 200 MB per-file limit."; return null; }

        var ext = Path.GetExtension(entry.Name);
        if (!UploadHelper.IsAllowedExtension(ext))
        { error = $"'{entryPath}' is a {ext} file, which is not an accepted type."; return null; }
        return entry;
    }

    /// <summary>Copies one named entry into wwwroot/uploads/<paramref name="subfolder"/> under a
    /// generated name, and returns its web path. Returns null when the entry is absent, oversized,
    /// or of a type the LMS does not accept.
    ///
    /// The generated name is what defeats "zip slip": the entry's own name never reaches the file
    /// system, so a crafted path such as <c>../../appsettings.json</c> cannot escape the uploads
    /// folder however it is written.</summary>
    public static string? Extract(string zipPath, string entryPath, IWebHostEnvironment env,
                                  string subfolder, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(entryPath)) { error = "No path given."; return null; }
        if (!File.Exists(zipPath)) { error = "The archive for this job is no longer on the server."; return null; }

        using var zip = ZipFile.OpenRead(zipPath);
        var entry = Find(zip, entryPath, out error);
        if (entry == null) return null;

        var dir = Path.Combine(env.WebRootPath, "uploads", subfolder);
        Directory.CreateDirectory(dir);
        var name = $"{Guid.NewGuid():N}{Path.GetExtension(entry.Name)}";
        var dest = Path.Combine(dir, name);

        using (var src = entry.Open())
        using (var fs = File.Create(dest))
            src.CopyTo(fs);

        return $"/uploads/{subfolder}/{name}";
    }

    /// <summary>Copies one named entry to a temporary file and returns its path, for content whose
    /// *text* is what the LMS keeps. A video transcript is read once into the lesson and never
    /// served again, so writing it into the web root would leave a file referenced by nothing —
    /// an orphan on the first import, and another on every re-run. The caller deletes it.</summary>
    public static string? ExtractToTemp(string zipPath, string entryPath, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(entryPath)) { error = "No path given."; return null; }
        if (!File.Exists(zipPath)) { error = "The archive for this job is no longer on the server."; return null; }

        using var zip = ZipFile.OpenRead(zipPath);
        var entry = Find(zip, entryPath, out error);
        if (entry == null) return null;

        // The extension is kept because the text extractor chooses its reader from it.
        var dest = Path.Combine(Path.GetTempPath(), $"lms-mig-{Guid.NewGuid():N}{Path.GetExtension(entry.Name)}");
        using (var src = entry.Open())
        using (var fs = File.Create(dest))
            src.CopyTo(fs);

        return dest;
    }
}

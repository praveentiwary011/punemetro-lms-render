using LMS.Web.Data;
using LMS.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;

namespace LMS.Web.Controllers;

/// <summary>
/// Gated delivery of hosted Knowledge Hub files (uploaded documents and videos).
/// Direct static access to <c>/uploads/documents</c> and <c>/uploads/videos</c> is
/// blocked in the pipeline, so these files are only reachable here:
///   • any signed-in user may <b>stream/view</b> a file inline (for playback / reading);
///   • only an <b>Admin</b> may <b>download/save</b> it (Content-Disposition: attachment).
/// External links (http/https) and YouTube embeds are not our files and are unaffected.
/// </summary>
[Authorize]
public class MediaController : Controller
{
    private readonly AppDbContext _db;
    private readonly IWebHostEnvironment _env;
    private readonly MediaTokenService _tokens;
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    public MediaController(AppDbContext db, IWebHostEnvironment env, MediaTokenService tokens)
    {
        _db = db;
        _env = env;
        _tokens = tokens;
    }

    // ---- Inline viewing / streaming (any authenticated user, valid token) ----

    [HttpGet]
    public async Task<IActionResult> Document(int id, string? t)
    {
        if (!_tokens.Validate(t, User.GetUserId(), MediaTokenService.DocumentKind, id)) return TokenDenied();
        var doc = await _db.Documents.FindAsync(id);
        var path = ResolveHosted(doc?.Url);
        return path == null ? NotFound() : ServeInline(path);
    }

    [HttpGet]
    public async Task<IActionResult> Video(int id, string? t)
    {
        if (!_tokens.Validate(t, User.GetUserId(), MediaTokenService.VideoKind, id)) return TokenDenied();
        // The stream may only be consumed as a media subresource (the <video> element),
        // never opened/downloaded as a standalone document or fetched — this closes the
        // "open the URL in a new tab and Save" and "copy as fetch" paths.
        var dest = Request.Headers["Sec-Fetch-Dest"].ToString();
        if (dest.Length > 0 && dest is not ("video" or "audio")) return TokenDenied();
        var video = await _db.Videos.FindAsync(id);
        var path = ResolveHosted(video?.Url);
        // Range processing lets the HTML5 player seek within the stream.
        return path == null ? NotFound() : ServeInline(path, enableRange: true);
    }

    // ---- Download / save (Admin only, valid token) ----

    [Authorize(Roles = "Admin")]
    [HttpGet]
    public async Task<IActionResult> DownloadDocument(int id, string? t)
    {
        if (!_tokens.Validate(t, User.GetUserId(), MediaTokenService.DocumentKind, id)) return TokenDenied();
        var doc = await _db.Documents.FindAsync(id);
        var path = ResolveHosted(doc?.Url);
        return path == null ? NotFound() : ServeAttachment(path, SafeFileName(doc!.Title, path));
    }

    [Authorize(Roles = "Admin")]
    [HttpGet]
    public async Task<IActionResult> DownloadVideo(int id, string? t)
    {
        if (!_tokens.Validate(t, User.GetUserId(), MediaTokenService.VideoKind, id)) return TokenDenied();
        var video = await _db.Videos.FindAsync(id);
        var path = ResolveHosted(video?.Url);
        return path == null ? NotFound() : ServeAttachment(path, SafeFileName(video!.Title, path));
    }

    private IActionResult TokenDenied() => StatusCode(StatusCodes.Status403Forbidden);

    // ---- helpers ----

    private IActionResult ServeInline(string physicalPath, bool enableRange = false)
    {
        // Discourage the file being written to disk cache and grabbed from there.
        Response.Headers["Cache-Control"] = "private, no-store";
        return PhysicalFile(physicalPath, ContentType(physicalPath), enableRangeProcessing: enableRange);
    }

    private IActionResult ServeAttachment(string physicalPath, string downloadName) =>
        PhysicalFile(physicalPath, ContentType(physicalPath), downloadName, enableRangeProcessing: true);

    /// <summary>Maps a stored <c>/uploads/…</c> URL to a physical file inside the
    /// uploads root, or null if it is not one of our hosted files or escapes the root.</summary>
    private string? ResolveHosted(string? url)
    {
        if (string.IsNullOrEmpty(url) || !url.StartsWith("/uploads/", StringComparison.Ordinal))
            return null; // external link / YouTube — not a hosted file
        var relative = url.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(_env.WebRootPath, relative));
        var root = Path.GetFullPath(Path.Combine(_env.WebRootPath, "uploads")) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, StringComparison.Ordinal)) return null; // path-traversal guard
        return System.IO.File.Exists(full) ? full : null;
    }

    private static string ContentType(string path) =>
        ContentTypes.TryGetContentType(path, out var ct) ? ct : "application/octet-stream";

    private static string SafeFileName(string title, string path)
    {
        var ext = Path.GetExtension(path);
        var stem = string.Join("_", (title ?? "download").Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        if (string.IsNullOrWhiteSpace(stem)) stem = "download";
        return stem + ext;
    }
}

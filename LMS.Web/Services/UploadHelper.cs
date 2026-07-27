using System.Globalization;
using System.Text;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace LMS.Web.Services;

/// <summary>
/// Central file-upload helper. Images (jpeg/png/etc.) are automatically
/// converted to a compact, high-quality PDF; other files are stored as-is.
/// </summary>
public static class UploadHelper
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".tif", ".tiff" };

    // ---- Orphan-file prevention -------------------------------------------------
    // Every file written during a request is tracked. If the request then fails
    // before the row that references the file is committed, CleanupPending deletes
    // it, so a saved file can never be left with no owning database row. Actions
    // that do further DB work after the file's row is committed call Commit to keep
    // the file safe from that cleanup.
    public const string PendingKey = "__pending_upload_files";
    private static IHttpContextAccessor? _http;
    public static void ConfigureTracking(IHttpContextAccessor http) => _http = http;

    private static void Track(string physicalPath)
    {
        var items = _http?.HttpContext?.Items;
        if (items == null) return;
        if (items[PendingKey] is not List<string> list) items[PendingKey] = list = new List<string>();
        list.Add(physicalPath);
    }

    /// <summary>Marks the files written so far this request as owned by committed rows,
    /// so a later error in the same request does not delete them.</summary>
    public static void Commit(HttpContext? ctx) => (ctx?.Items[PendingKey] as List<string>)?.Clear();

    /// <summary>Deletes any files written this request that were never committed
    /// (called when the request ends in an unhandled error).</summary>
    public static void CleanupPending(HttpContext ctx)
    {
        if (ctx.Items[PendingKey] is not List<string> list) return;
        foreach (var f in list)
        {
            try { if (File.Exists(f)) File.Delete(f); } catch { /* best-effort */ }
        }
        list.Clear();
    }

    /// <summary>Best-effort delete of a previously stored upload by its URL — used when a
    /// file is replaced (delete the old one once the new one is committed) or when its
    /// owning row is removed. Only deletes hosted <c>/uploads/…</c> files (never external
    /// links or bundled <c>/images/…</c> placeholders), with a path-traversal guard.</summary>
    public static void TryDeleteStored(string? storedUrl, IWebHostEnvironment env)
    {
        if (string.IsNullOrEmpty(storedUrl) || !storedUrl.StartsWith("/uploads/", StringComparison.Ordinal)) return;
        try
        {
            var relative = storedUrl.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
            var full = Path.GetFullPath(Path.Combine(env.WebRootPath, relative));
            var root = Path.GetFullPath(Path.Combine(env.WebRootPath, "uploads")) + Path.DirectorySeparatorChar;
            if (full.StartsWith(root, StringComparison.Ordinal) && File.Exists(full)) File.Delete(full);
        }
        catch { /* best-effort */ }
    }

    /// <summary>Only these non-image types are stored as-is. Executable, script and
    /// markup types (.html, .svg, .js, .exe, …) are rejected — they would otherwise be
    /// served from the site origin and enable stored XSS / malware distribution.</summary>
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".pdf", ".pptx", ".docx", ".xlsx", ".csv", ".txt", ".mp4", ".webm", ".mov", ".ogv", ".mp3", ".zip" };

    /// <summary>
    /// Saves an uploaded image (signature, logo) after validating it by actually
    /// decoding the bytes — extension and content-type are attacker-controlled, the
    /// pixels are not. The image is re-encoded to a clean PNG, which strips any
    /// embedded HTML/script/EXIF payload and normalises the format, and the long edge
    /// is capped to keep the footprint small. Returns the stored URL, or null when the
    /// upload is missing, too large, or not a decodable image.
    /// </summary>
    public static async Task<string?> SaveImageAsync(IFormFile? file, IWebHostEnvironment env, string subfolder, long maxBytes)
    {
        if (file == null || file.Length == 0 || file.Length > maxBytes) return null;
        try
        {
            await using var input = file.OpenReadStream();
            using var image = await Image.LoadAsync<Rgba32>(input); // throws if not a real image
            image.Mutate(x => x.AutoOrient());
            const int max = 1600;
            if (image.Width > max || image.Height > max)
                image.Mutate(x => x.Resize(new ResizeOptions { Mode = ResizeMode.Max, Size = new Size(max, max) }));

            var dir = Path.Combine(env.WebRootPath, "uploads", subfolder);
            Directory.CreateDirectory(dir);
            var name = $"{Guid.NewGuid():N}.png";
            var path = Path.Combine(dir, name);
            await image.SaveAsPngAsync(path);
            Track(path);
            return $"/uploads/{subfolder}/{name}";
        }
        catch
        {
            return null; // not a decodable image
        }
    }

    public static async Task<string?> SaveAsync(IFormFile? file, IWebHostEnvironment env, string subfolder)
    {
        if (file == null || file.Length == 0) return null;
        var ext = Path.GetExtension(file.FileName);
        if (!ImageExtensions.Contains(ext) && !AllowedExtensions.Contains(ext))
            return null; // disallowed type — treated as "no file"
        var dir = Path.Combine(env.WebRootPath, "uploads", subfolder);
        Directory.CreateDirectory(dir);

        if (ImageExtensions.Contains(ext))
        {
            var pdfName = $"{Guid.NewGuid():N}.pdf";
            var pdfPath = Path.Combine(dir, pdfName);
            await using var input = file.OpenReadStream();
            await ImageToPdf.ConvertAsync(input, pdfPath);
            Track(pdfPath);
            return $"/uploads/{subfolder}/{pdfName}";
        }

        var safeName = $"{Guid.NewGuid():N}{ext}";
        var safePath = Path.Combine(dir, safeName);
        await using (var stream = File.Create(safePath))
            await file.CopyToAsync(stream);
        Track(safePath);
        return $"/uploads/{subfolder}/{safeName}";
    }
}

/// <summary>
/// Converts any raster image to a single-page PDF: auto-orients, flattens
/// transparency onto white, caps the long edge at 2400px, and embeds a
/// quality-82 JPEG (DCTDecode) — high quality with a small footprint.
/// </summary>
public static class ImageToPdf
{
    private const int MaxDimension = 2400;
    private const int JpegQuality = 82;

    public static async Task ConvertAsync(Stream input, string outputPath)
    {
        using var image = await Image.LoadAsync<Rgba32>(input);
        image.Mutate(x => x.AutoOrient());
        if (image.Width > MaxDimension || image.Height > MaxDimension)
            image.Mutate(x => x.Resize(new ResizeOptions { Mode = ResizeMode.Max, Size = new Size(MaxDimension, MaxDimension) }));
        image.Mutate(x => x.BackgroundColor(Color.White)); // flatten alpha

        using var jpegStream = new MemoryStream();
        await image.SaveAsJpegAsync(jpegStream, new JpegEncoder { Quality = JpegQuality });
        WritePdf(outputPath, jpegStream.ToArray(), image.Width, image.Height);
    }

    private static void WritePdf(string path, byte[] jpeg, int pxWidth, int pxHeight)
    {
        // Fit the page to the image within A4 bounds (points)
        var scale = Math.Min(1.0, Math.Min(595.0 / pxWidth, 842.0 / pxHeight));
        var w = (pxWidth * scale).ToString("0.##", CultureInfo.InvariantCulture);
        var h = (pxHeight * scale).ToString("0.##", CultureInfo.InvariantCulture);

        using var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.ASCII.GetBytes(s));

        var content = $"q {w} 0 0 {h} 0 0 cm /Im0 Do Q\n";
        var offsets = new long[7];

        W("%PDF-1.4\n");
        offsets[1] = ms.Position;
        W("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        offsets[2] = ms.Position;
        W("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        offsets[3] = ms.Position;
        W($"3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {w} {h}] /Contents 4 0 R /Resources << /XObject << /Im0 5 0 R >> >> >>\nendobj\n");
        offsets[4] = ms.Position;
        W($"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}endstream\nendobj\n");
        offsets[5] = ms.Position;
        W($"5 0 obj\n<< /Type /XObject /Subtype /Image /Name /Im0 /Width {pxWidth} /Height {pxHeight} " +
          $"/ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {jpeg.Length} >>\nstream\n");
        ms.Write(jpeg);
        W("\nendstream\nendobj\n");

        var xref = ms.Position;
        W("xref\n0 6\n0000000000 65535 f \n");
        for (int i = 1; i <= 5; i++)
            W($"{offsets[i].ToString("D10", CultureInfo.InvariantCulture)} 00000 n \n");
        W($"trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF");

        File.WriteAllBytes(path, ms.ToArray());
    }
}

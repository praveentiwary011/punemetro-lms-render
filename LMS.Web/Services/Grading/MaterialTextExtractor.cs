using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using UglyToad.PdfPig;

namespace LMS.Web.Services.Grading;

/// <summary>Pulls readable text out of an uploaded course material so the AI can reference it
/// (§AIG-14). Everything runs locally — no upload leaves the server, which is the same promise
/// the grading model makes.
///
/// Office formats are ZIP+XML, so they are read with the framework's own zip and XML support
/// rather than another dependency. PDF needs a real parser (PdfPig).
///
/// Extraction is best-effort by design: a scanned PDF is images with no text layer and yields
/// nothing. That is reported to the trainer rather than hidden, because silently indexing an
/// empty document is what made uploaded material invisible in the first place.</summary>
public static class MaterialTextExtractor
{
    /// <summary>Formats we can read text from. A video file is not among them — its transcript
    /// is supplied by the trainer.</summary>
    public static bool CanExtract(string path) =>
        Ext(path) is ".pdf" or ".txt" or ".csv" or ".vtt" or ".srt" or ".md" or ".docx" or ".pptx";

    private static string Ext(string p) => Path.GetExtension(p).ToLowerInvariant();

    public static string Extract(string absolutePath)
    {
        if (!File.Exists(absolutePath)) return "";
        try
        {
            return Ext(absolutePath) switch
            {
                ".pdf" => FromPdf(absolutePath),
                ".docx" => FromOpenXml(absolutePath, e => e.FullName == "word/document.xml"),
                ".pptx" => FromOpenXml(absolutePath, e => e.FullName.StartsWith("ppt/slides/slide")
                                                          && e.FullName.EndsWith(".xml")),
                ".vtt" or ".srt" => FromCaptions(absolutePath),
                _ => Clean(File.ReadAllText(absolutePath))
            };
        }
        catch
        {
            // A corrupt or password-protected file must not break the upload — the trainer is
            // told no text was found and can paste it in manually.
            return "";
        }
    }

    private static string FromPdf(string path)
    {
        using var doc = PdfDocument.Open(path);
        var sb = new StringBuilder();
        foreach (var page in doc.GetPages())
        {
            // page.Text concatenates glyphs in content order with no separator at a line
            // break, so "…isolated at the\nsubstation…" comes back as "at thesubstation".
            // Those fused tokens embed badly and make the material effectively unsearchable,
            // which defeats the point of indexing it. GetWords() applies PdfPig's word
            // segmentation, so join those with spaces instead.
            var words = page.GetWords().Select(w => w.Text);
            sb.AppendLine(string.Join(' ', words));
        }
        return Clean(sb.ToString());
    }

    /// <summary>DOCX/PPTX are zip archives of XML. Concatenating the text nodes of the relevant
    /// parts gives the readable content without needing the OpenXML SDK.</summary>
    private static string FromOpenXml(string path, Func<ZipArchiveEntry, bool> wanted)
    {
        using var zip = ZipFile.OpenRead(path);
        var sb = new StringBuilder();
        foreach (var entry in zip.Entries.Where(wanted).OrderBy(e => e.FullName))
        {
            using var s = entry.Open();
            var xml = XDocument.Load(s);
            // Word and PowerPoint both put run text in <a:t> / <w:t>; taking every text node
            // and separating on paragraph boundaries is enough for retrieval purposes.
            foreach (var t in xml.Descendants().Where(e => e.Name.LocalName is "t"))
                sb.Append(t.Value).Append(' ');
            sb.AppendLine();
        }
        return Clean(sb.ToString());
    }

    /// <summary>WebVTT/SRT captions: drop the cue numbers, timestamps and WEBVTT header, keep
    /// the spoken lines. This is the usual export from a video platform, so a trainer can attach
    /// a transcript without retyping it.</summary>
    private static string FromCaptions(string path)
    {
        var sb = new StringBuilder();
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("WEBVTT", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.Contains("-->")) continue;                       // timestamp cue
            if (int.TryParse(line, out _)) continue;                  // SRT sequence number
            sb.AppendLine(line);
        }
        return Clean(sb.ToString());
    }

    /// <summary>Collapse the whitespace that PDF and caption extraction leave behind, so chunking
    /// and the token budget are not wasted on blank space.</summary>
    private static string Clean(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var lines = s.Replace("\r", "").Split('\n')
            .Select(l => System.Text.RegularExpressions.Regex.Replace(l, @"[ \t]+", " ").Trim())
            .Where(l => l.Length > 0);
        return string.Join("\n", lines).Trim();
    }
}

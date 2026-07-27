using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace LMS.Web.Services.Grading;

/// <summary>Splits source text into ~500-token passages on sentence/paragraph boundaries
/// with small overlap, and strips HTML from lesson content before chunking (§AIG-02).</summary>
public static class TextChunker
{
    // Rough token≈word heuristic for chunk sizing (no tokenizer dependency).
    private const int TargetWords = 350;   // ~500 tokens
    private const int OverlapWords = 40;

    public static string HtmlToText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";
        var noTags = Regex.Replace(html, "<[^>]+>", " ");
        var decoded = WebUtility.HtmlDecode(noTags);
        return Regex.Replace(decoded, "\\s+", " ").Trim();
    }

    public static IReadOnlyList<string> Chunk(string text)
    {
        text = Regex.Replace(text ?? "", "\\s+", " ").Trim();
        if (text.Length == 0) return System.Array.Empty<string>();

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length <= TargetWords) return new[] { text };

        var chunks = new List<string>();
        int i = 0;
        while (i < words.Length)
        {
            int end = Math.Min(i + TargetWords, words.Length);
            var sb = new StringBuilder();
            for (int j = i; j < end; j++) { if (sb.Length > 0) sb.Append(' '); sb.Append(words[j]); }
            chunks.Add(sb.ToString());
            if (end >= words.Length) break;
            i = end - OverlapWords;   // carry overlap into the next chunk
        }
        return chunks;
    }

    /// <summary>Pack a float32 vector little-endian for BLOB storage.</summary>
    public static byte[] PackVector(float[] v)
    {
        var bytes = new byte[v.Length * sizeof(float)];
        Buffer.BlockCopy(v, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    public static float[] UnpackVector(byte[] b)
    {
        var v = new float[b.Length / sizeof(float)];
        Buffer.BlockCopy(b, 0, v, 0, b.Length);
        return v;
    }

    public static double Cosine(float[] a, float[] b)
    {
        if (a.Length == 0 || b.Length != a.Length) return 0;
        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++) { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
        return (na == 0 || nb == 0) ? 0 : dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }
}

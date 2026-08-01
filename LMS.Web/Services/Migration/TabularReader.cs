using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace LMS.Web.Services.Migration;

/// <summary>Reads a client's extract into rows of column → value (§MIG-02).
///
/// CSV and XLSX only, because every source system of interest can produce one — including the
/// clients whose "LMS" is really a spreadsheet. XLSX is read straight from the Office ZIP with
/// the framework's own XML support rather than adding a spreadsheet dependency, and formulas are
/// never evaluated: a migration must not be able to execute anything that arrives in a file.</summary>
public static class TabularReader
{
    public static bool IsSupported(string fileName)
    {
        var e = Path.GetExtension(fileName).ToLowerInvariant();
        return e is ".csv" or ".tsv" or ".xlsx";
    }

    public static (List<string> Columns, List<Dictionary<string, string>> Rows) Read(Stream stream, string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        var grid = ext == ".xlsx" ? ReadXlsx(stream) : ReadDelimited(stream, ext == ".tsv" ? '\t' : ',');

        if (grid.Count == 0) return (new(), new());

        // First row is the header. Blank/duplicate headers are given stable names so a mapping
        // can still address them rather than the import failing on the client's untidy export.
        var header = new List<string>();
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in grid[0])
        {
            var name = string.IsNullOrWhiteSpace(raw) ? $"Column{header.Count + 1}" : raw.Trim();
            if (seen.TryGetValue(name, out var n)) { seen[name] = n + 1; name = $"{name} ({n + 1})"; }
            else seen[name] = 1;
            header.Add(name);
        }

        var rows = new List<Dictionary<string, string>>();
        foreach (var line in grid.Skip(1))
        {
            if (line.All(string.IsNullOrWhiteSpace)) continue;          // blank line, not a record
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < header.Count; i++)
                d[header[i]] = i < line.Count ? (line[i] ?? "").Trim() : "";
            rows.Add(d);
        }
        return (header, rows);
    }

    // ---------------------------------------------------------------- CSV / TSV

    /// <summary>RFC 4180-style parsing: quoted fields may contain the delimiter, newlines and
    /// doubled quotes. A naive split on commas corrupts any export containing an address or a
    /// course title with a comma, which is most of them.</summary>
    private static List<List<string>> ReadDelimited(Stream stream, char delim)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var text = reader.ReadToEnd();
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        bool quoted = false;

        for (int i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else quoted = false;
                }
                else field.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == delim) { row.Add(field.ToString()); field.Clear(); }
            else if (c == '\r') { /* handled by \n */ }
            else if (c == '\n') { row.Add(field.ToString()); field.Clear(); rows.Add(row); row = new List<string>(); }
            else field.Append(c);
        }
        if (field.Length > 0 || row.Count > 0) { row.Add(field.ToString()); rows.Add(row); }
        return rows;
    }

    // ---------------------------------------------------------------- XLSX

    private static List<List<string>> ReadXlsx(Stream stream)
    {
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);

        // Shared strings: xlsx stores most text once and references it by index.
        var shared = new List<string>();
        var sst = zip.GetEntry("xl/sharedStrings.xml");
        if (sst != null)
        {
            using var s = sst.Open();
            foreach (var si in XDocument.Load(s).Root!.Elements().Where(e => e.Name.LocalName == "si"))
                shared.Add(string.Concat(si.Descendants().Where(t => t.Name.LocalName == "t").Select(t => t.Value)));
        }

        var sheet = zip.Entries.FirstOrDefault(e => e.FullName.StartsWith("xl/worksheets/sheet")
                                                    && e.FullName.EndsWith(".xml"));
        if (sheet == null) return new();

        using var ss = sheet.Open();
        var doc = XDocument.Load(ss);
        var grid = new List<List<string>>();

        foreach (var r in doc.Descendants().Where(e => e.Name.LocalName == "row"))
        {
            var cells = new List<string>();
            foreach (var c in r.Elements().Where(e => e.Name.LocalName == "c"))
            {
                // Column letter → index, so gaps in a sparse sheet do not shift the columns.
                var reference = (string?)c.Attribute("r") ?? "";
                var letters = new string(reference.TakeWhile(char.IsLetter).ToArray());
                int idx = letters.Aggregate(0, (a, ch) => a * 26 + (char.ToUpperInvariant(ch) - 'A' + 1)) - 1;
                while (cells.Count < idx) cells.Add("");

                var type = (string?)c.Attribute("t");
                // Read the cached value only — never the formula. Nothing in a client's
                // spreadsheet gets evaluated on import.
                var v = c.Elements().FirstOrDefault(e => e.Name.LocalName == "v")?.Value ?? "";
                if (type == "s" && int.TryParse(v, out var si2) && si2 < shared.Count) v = shared[si2];
                else if (type == "inlineStr")
                    v = string.Concat(c.Descendants().Where(t => t.Name.LocalName == "t").Select(t => t.Value));
                cells.Add(v);
            }
            grid.Add(cells);
        }
        return grid;
    }
}

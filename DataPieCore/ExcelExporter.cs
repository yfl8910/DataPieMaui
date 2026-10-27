using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace DataPieCore;

public sealed class ExcelExportOptions
{
    // Plain mode keeps only the date number format needed to display typed dates.
    public bool Formatted { get; init; } = true;
    // Analysis-specific styles are opt-in; ordinary table columns are not inferred by name.
    public int CalculationStartColumn { get; init; } = int.MaxValue;
    public IReadOnlyList<int> PercentageColumns { get; init; } = Array.Empty<int>();
    public object[] Totals { get; init; }
    public IReadOnlyList<AnalysisNumberFormat> NumberFormats { get; init; } = Array.Empty<AnalysisNumberFormat>();
}

// Readers belong to the caller. Complete publishes the file; otherwise Dispose removes it.
public sealed class ExcelExporter : IDisposable
{
    private const string Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private readonly string path, temporary;
    private readonly ZipArchive zip;
    private readonly List<string> sheets = new();
    private readonly XDocument styles;
    private readonly Dictionary<string, (int Normal, int Total)> customStyles = new(StringComparer.Ordinal);
    private (int Normal, int Total) RegisterFormat(AnalysisNumberFormat format)
    {
        string code = format.Code();
        if (customStyles.TryGetValue(code, out var existing)) return existing;
        XNamespace ns = Ns;
        var formats = styles.Root.Element(ns + "numFmts"); var cells = styles.Root.Element(ns + "cellXfs");
        int formatId = 166 + customStyles.Count, normal = cells.Elements().Count();
        formats.Add(new XElement(ns + "numFmt", new XAttribute("numFmtId", formatId), new XAttribute("formatCode", code)));
        for (int total = 0; total < 2; total++) cells.Add(new XElement(ns + "xf", new XAttribute("numFmtId", formatId), new XAttribute("fontId", total),
            new XAttribute("fillId", total == 0 ? 0 : 3), new XAttribute("borderId", total), new XAttribute("xfId", 0), new XAttribute("applyNumberFormat", 1), new XAttribute("applyFont", 1), new XAttribute("applyFill", 1), new XAttribute("applyBorder", 1)));
        formats.SetAttributeValue("count", formats.Elements().Count()); cells.SetAttributeValue("count", cells.Elements().Count());
        return customStyles[code] = (normal, normal + 1);
    }
    private bool disposed, faulted;
    public ExcelExporter(string path)
    {
        this.path = Path.GetFullPath(path);
        temporary = this.path + "." + Guid.NewGuid().ToString("N") + ".xlsx";
        zip = ZipFile.Open(temporary, ZipArchiveMode.Create);
        try
        {
            styles = XDocument.Parse("""
                <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
                <numFmts count="2"><numFmt numFmtId="164" formatCode="0.0%"/><numFmt numFmtId="165" formatCode="yyyy-mm-dd hh:mm:ss"/></numFmts>
                <fonts count="2"><font><sz val="11"/><name val="Calibri"/></font><font><b/><color rgb="FFFFFFFF"/><sz val="11"/><name val="Calibri"/></font></fonts>
                <fills count="4"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill><fill><patternFill patternType="solid"><fgColor rgb="FF334E68"/><bgColor indexed="64"/></patternFill></fill><fill><patternFill patternType="solid"><fgColor rgb="FF28756A"/><bgColor indexed="64"/></patternFill></fill></fills>
                <borders count="2"><border><left/><right/><top/><bottom/><diagonal/></border><border><left/><right/><top style="thin"><color rgb="FF90BCB4"/></top><bottom/><diagonal/></border></borders>
                <cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>
                <cellXfs count="9"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/><xf numFmtId="0" fontId="1" fillId="2" borderId="0" xfId="0" applyFont="1" applyFill="1" applyAlignment="1"><alignment vertical="center" wrapText="1"/></xf><xf numFmtId="4" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/><xf numFmtId="164" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/><xf numFmtId="165" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/><xf numFmtId="0" fontId="1" fillId="3" borderId="1" applyBorder="1" xfId="0" applyFont="1" applyFill="1"/><xf numFmtId="4" fontId="1" fillId="3" borderId="1" applyBorder="1" xfId="0" applyFont="1" applyFill="1" applyNumberFormat="1"/><xf numFmtId="164" fontId="1" fillId="3" borderId="1" applyBorder="1" xfId="0" applyFont="1" applyFill="1" applyNumberFormat="1"/><xf numFmtId="165" fontId="1" fillId="3" borderId="1" applyBorder="1" xfId="0" applyFont="1" applyFill="1" applyNumberFormat="1"/></cellXfs>
                <cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles>
                </styleSheet>
                """);
        }
        catch { zip.Dispose(); File.Delete(temporary); throw; }
    }
    private void Part(string name, string text)
    {
        using var stream = zip.CreateEntry(name).Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false)); writer.Write(text);
    }
    public static void Save(string path, IDataReader reader, string sheetName = "Sheet1", ExcelExportOptions options = null)
    {
        using var workbook = new ExcelExporter(path);
        workbook.WriteSheet(sheetName, reader, options); workbook.Complete();
    }
    public void WriteSheet(string name, IDataReader reader, ExcelExportOptions options = null)
    {
        if (disposed || faulted) throw new InvalidOperationException("Workbook is closed or a previous write failed.");
        try { WriteSheetCore(name, reader, options ?? new ExcelExportOptions()); }
        catch { faulted = true; throw; }
    }
    private void WriteSheetCore(string name, IDataReader reader, ExcelExportOptions options)
    {
        ArgumentNullException.ThrowIfNull(reader);
        int count = reader.FieldCount;
        if (count is < 1 or > 16384) throw new InvalidOperationException("Excel supports 1 to 16,384 columns.");
        if (options.Totals != null && options.Totals.Length != count) throw new ArgumentException("Totals must match the worksheet column count.");
        sheets.Add(SafeSheetName(name));
        var names = Enumerable.Range(0, count).Select(reader.GetName).ToArray();
        var numberStyles = new Dictionary<int, (int Normal, int Total)>();
        if (options.Formatted) for (int c = 0; c < Math.Min(count, options.NumberFormats.Count); c++)
            if (options.NumberFormats[c] is { Kind: not "Auto" } format) numberStyles[c] = RegisterFormat(format);
        var sample = new List<object[]>();
        while (sample.Count < 200 && reader.Read()) { var values = new object[count]; reader.GetValues(values); sample.Add(values); }
        using var sheet = zip.CreateEntry($"xl/worksheets/sheet{sheets.Count}.xml").Open();
        using var xml = XmlWriter.Create(sheet, new XmlWriterSettings { Encoding = new UTF8Encoding(false) });
        void Start(string name) => xml.WriteStartElement(name, Ns);
        void Attr(string name, object value) => xml.WriteAttributeString(name, Convert.ToString(value, Invariant));
        void Empty(string name, params (string Key, object Value)[] attributes)
        { Start(name); foreach (var a in attributes) Attr(a.Key, a.Value); xml.WriteEndElement(); }
        void TextCell(string reference, string value, int style)
        {
            Start("c"); Attr("r", reference); Attr("s", style); Attr("t", "inlineStr"); Start("is"); Start("t");
            xml.WriteAttributeString("xml", "space", "http://www.w3.org/XML/1998/namespace", "preserve");
            if (value.Length > 32767) throw new InvalidOperationException("A cell exceeds Excel's 32,767 character limit.");
            xml.WriteString(ExcelText(value)); xml.WriteEndElement(); xml.WriteEndElement(); xml.WriteEndElement();
        }
        Start("worksheet");
        if (options.Formatted)
        {
            Start("sheetViews"); Start("sheetView"); Attr("workbookViewId", 0);
            Empty("pane", ("ySplit", 1), ("topLeftCell", "A2"), ("activePane", "bottomLeft"), ("state", "frozen"));
            xml.WriteEndElement(); xml.WriteEndElement();
            Start("cols");
            for (int c = 0; c < count; c++)
            {
                int width = names[c].Length;
                foreach (var row in sample) width = Math.Max(width, (Numeric(row[c]) && c < options.NumberFormats.Count && options.NumberFormats[c] is { Kind: not "Auto" } format ? ((IFormattable)row[c]).ToString(format.Code(), Invariant) : Display(row[c], options.PercentageColumns.Contains(c), c >= options.CalculationStartColumn)).Length);
                Empty("col", ("min", c + 1), ("max", c + 1), ("width", Math.Clamp(width + 2, 12, 45)), ("customWidth", 1));
            }
            xml.WriteEndElement();
        }
        Start("sheetData"); Start("row"); Attr("r", 1);
        if (options.Formatted) { Attr("ht", 30); Attr("customHeight", 1); }
        for (int c = 0; c < count; c++) TextCell(Column(c) + "1", names[c], options.Formatted ? 1 : 0);
        xml.WriteEndElement(); int rowNumber = 1;
        void Row(object[] values, bool total = false)
        {
            if (++rowNumber > 1048576) throw new InvalidOperationException("Worksheet exceeds Excel's row limit (including header and totals).");
            Start("row"); Attr("r", rowNumber);
            for (int c = 0; c < count; c++)
            {
                object value = values[c]; if (value is null or DBNull) { if (total && options.Formatted) { Start("c"); Attr("r", Column(c) + rowNumber.ToString(Invariant)); Attr("s", 5); xml.WriteEndElement(); } continue; }
                if (value is double d && !double.IsFinite(d) || value is float f && !float.IsFinite(f))
                    throw new InvalidOperationException("Excel cannot store a nonfinite numeric value.");
                string reference = Column(c) + rowNumber.ToString(Invariant);
                if (Numeric(value) && !NeedsText(value) || value is DateTime || value is bool)
                {
                    Start("c"); Attr("r", reference);
                    int style = value is DateTime ? 4 : !options.Formatted ? 0 : Numeric(value) && options.PercentageColumns.Contains(c) ? 3 : Numeric(value) && c >= options.CalculationStartColumn ? 2 : 0;
                    Attr("s", Numeric(value) && numberStyles.TryGetValue(c, out var custom) ? (total ? custom.Total : custom.Normal) : total && options.Formatted ? (style == 0 ? 5 : style + 4) : style);
                    if (value is bool) Attr("t", "b");
                    Start("v"); xml.WriteString(value is DateTime date ? date.ToOADate().ToString("R", Invariant) : value is bool flag ? (flag ? "1" : "0") : Convert.ToString(value, Invariant));
                    xml.WriteEndElement(); xml.WriteEndElement();
                }
                else TextCell(reference, CellText(value), total && options.Formatted ? 5 : 0);
            }
            xml.WriteEndElement();
        }
        foreach (var values in sample) Row(values);
        while (reader.Read()) { var values = new object[count]; reader.GetValues(values); Row(values); }
        int lastDataRow = rowNumber; if (options.Totals != null) Row(options.Totals, true);
        xml.WriteEndElement(); if (options.Formatted) Empty("autoFilter", ("ref", "A1:" + Column(count - 1) + lastDataRow.ToString(Invariant)));
        xml.WriteEndElement();
    }
    public void Complete()
    {
        if (disposed || faulted) throw new InvalidOperationException("Workbook is closed or a previous write failed.");
        if (sheets.Count == 0) throw new InvalidOperationException("Add at least one worksheet.");
        faulted = true;
        Part("xl/styles.xml", styles.ToString());
        XNamespace content = "http://schemas.openxmlformats.org/package/2006/content-types";
        XNamespace rel = "http://schemas.openxmlformats.org/package/2006/relationships";
        XNamespace main = Ns;
        const string docRel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        XElement Relationship(string id, string type, string target) => new(rel + "Relationship", new XAttribute("Id", id), new XAttribute("Type", docRel + "/" + type), new XAttribute("Target", target));
        XElement Override(string part, string type) => new(content + "Override", new XAttribute("PartName", part), new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml." + type + "+xml"));
        Part("[Content_Types].xml", new XElement(content + "Types",
            new XElement(content + "Default", new XAttribute("Extension", "rels"), new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")),
            new XElement(content + "Default", new XAttribute("Extension", "xml"), new XAttribute("ContentType", "application/xml")),
            Override("/xl/workbook.xml", "sheet.main"), Override("/xl/styles.xml", "styles"),
            sheets.Select((_, i) => Override($"/xl/worksheets/sheet{i + 1}.xml", "worksheet"))).ToString());
        Part("_rels/.rels", new XElement(rel + "Relationships", Relationship("rId1", "officeDocument", "xl/workbook.xml")).ToString());
        Part("xl/workbook.xml", new XElement(main + "workbook", new XAttribute(XNamespace.Xmlns + "r", docRel),
            new XElement(main + "sheets", sheets.Select((name, i) => new XElement(main + "sheet", new XAttribute("name", name), new XAttribute("sheetId", i + 1), new XAttribute(XName.Get("id", docRel), "rId" + (i + 1)))))).ToString());
        Part("xl/_rels/workbook.xml.rels", new XElement(rel + "Relationships",
            sheets.Select((_, i) => Relationship("rId" + (i + 1), "worksheet", $"worksheets/sheet{i + 1}.xml")),
            Relationship("rId" + (sheets.Count + 1), "styles", "styles.xml")).ToString());
        zip.Dispose();
        File.Move(temporary, path, true);
        disposed = true;
    }
    private string SafeSheetName(string name)
    {
        name = new string((name ?? "").Select(c => ":\\/?*[]".Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim('\'');
        if (string.IsNullOrWhiteSpace(name)) name = "Sheet";
        if (name.Length > 31) name = name.Substring(0, char.IsHighSurrogate(name[30]) ? 30 : 31);
        name = name.Trim('\'');
        string candidate = name;
        int suffix = 1;
        while (candidate.Equals("History", StringComparison.OrdinalIgnoreCase) || sheets.Contains(candidate, StringComparer.OrdinalIgnoreCase))
        {
            string tail = " (" + (++suffix).ToString(Invariant) + ")";
            int length = Math.Min(name.Length, 31 - tail.Length);
            if (length > 0 && char.IsHighSurrogate(name[length - 1])) length--;
            candidate = name.Substring(0, length).TrimEnd('\'') + tail;
        }
        return candidate;
    }
    public void Dispose()
    {
        if (disposed) return;
        try { zip.Dispose(); }
        finally { disposed = true; if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static bool NeedsText(object value) => (value is long or ulong or decimal)
        && Convert.ToString(value, Invariant).Count(char.IsDigit) > 15;
    private static string CellText(object value) => value switch
    {
        byte[] bytes => Convert.ToHexString(bytes),
        DateTimeOffset date => date.ToString("O", Invariant),
        _ => Convert.ToString(value, Invariant) ?? ""
    };
    private static bool Numeric(object value) => value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal;
    private static string ExcelText(string value)
    {
        value = System.Text.RegularExpressions.Regex.Replace(value, "_x[0-9a-fA-F]{4}_", m => "_x005F_" + m.Value.Substring(1));
        return System.Text.RegularExpressions.Regex.Replace(value, "[\u0000-\u0008\u000B\u000C\u000E-\u001F\uFFFE\uFFFF]", m => "_x" + ((int)m.Value[0]).ToString("X4", Invariant) + "_");
    }
    private static string Display(object value, bool percent, bool calculation) => value is null or DBNull ? "" : Numeric(value)
        ? ((IFormattable)value).ToString(percent ? "0.0%" : calculation ? "N2" : "G", Invariant) : Convert.ToString(value, Invariant) ?? "";
    private static string Column(int index)
    {
        string name = ""; for (int n = index + 1; n > 0; n = (n - 1) / 26) name = (char)('A' + (n - 1) % 26) + name;
        return name;
    }
}

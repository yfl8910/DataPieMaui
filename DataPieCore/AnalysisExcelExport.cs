using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml;

namespace DataPieCore;

// Dedicated streaming workbook writer; shared ExcelIO exports keep their existing behavior.
internal static class AnalysisExcelExport
{
    private const string Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    public static void Save(string path, IDataReader reader, int groupColumns, object[] totals = null)
    {
        int count = reader.FieldCount;
        if (count is < 1 or > 16384) throw new InvalidOperationException("Excel supports 1 to 16,384 columns.");
        var names = Enumerable.Range(0, count).Select(reader.GetName).ToArray();
        var sample = new List<object[]>();
        while (sample.Count < 200 && reader.Read()) { var values = new object[count]; reader.GetValues(values); sample.Add(values); }
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Part(string name, string xml)
        {
            using var stream = zip.CreateEntry(name).Open();
            using var writer = new StreamWriter(stream, new UTF8Encoding(false)); writer.Write(xml);
        }
        Part("[Content_Types].xml", """
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/></Types>
            """);
        Part("_rels/.rels", """
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>
            """);
        Part("xl/workbook.xml", """
            <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Analysis" sheetId="1" r:id="rId1"/></sheets></workbook>
            """);
        Part("xl/_rels/workbook.xml.rels", """
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>
            """);
        Part("xl/styles.xml", """
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
        using var sheet = zip.CreateEntry("xl/worksheets/sheet1.xml").Open();
        using var xml = XmlWriter.Create(sheet, new XmlWriterSettings { Encoding = new UTF8Encoding(false) });
        void Start(string name) => xml.WriteStartElement(name, Ns);
        void Attr(string name, object value) => xml.WriteAttributeString(name, Convert.ToString(value, Invariant));
        void Empty(string name, params (string Key, object Value)[] attributes)
        { Start(name); foreach (var a in attributes) Attr(a.Key, a.Value); xml.WriteEndElement(); }
        void TextCell(string reference, string value, int style)
        {
            Start("c"); Attr("r", reference); Attr("s", style); Attr("t", "inlineStr"); Start("is"); Start("t");
            xml.WriteAttributeString("xml", "space", "http://www.w3.org/XML/1998/namespace", "preserve");
            if (value.Length > 32767) throw new InvalidOperationException("An analysis cell exceeds Excel's 32,767 character limit.");
            xml.WriteString(ExcelText(value)); xml.WriteEndElement(); xml.WriteEndElement(); xml.WriteEndElement();
        }
        Start("worksheet");
        Start("sheetViews"); Start("sheetView"); Attr("workbookViewId", 0);
        Empty("pane", ("ySplit", 1), ("topLeftCell", "A2"), ("activePane", "bottomLeft"), ("state", "frozen"));
        xml.WriteEndElement(); xml.WriteEndElement();
        Start("cols");
        for (int c = 0; c < count; c++)
        {
            int width = names[c].Length;
            foreach (var row in sample) width = Math.Max(width, Display(row[c], names[c].Contains('%'), c >= groupColumns).Length);
            Empty("col", ("min", c + 1), ("max", c + 1), ("width", Math.Clamp(width + 2, 12, 45)), ("customWidth", 1));
        }
        xml.WriteEndElement(); Start("sheetData"); Start("row"); Attr("r", 1); Attr("ht", 30); Attr("customHeight", 1);
        for (int c = 0; c < count; c++) TextCell(Column(c) + "1", names[c], 1);
        xml.WriteEndElement(); int rowNumber = 1;
        void Row(object[] values, bool total = false)
        {
            if (++rowNumber > 1048576) throw new InvalidOperationException("Analysis exceeds Excel's 1,048,575 data-row limit. Narrow the search or grouping.");
            Start("row"); Attr("r", rowNumber);
            for (int c = 0; c < count; c++)
            {
                object value = values[c]; if (value is null or DBNull) { if (total) { Start("c"); Attr("r", Column(c) + rowNumber.ToString(Invariant)); Attr("s", 5); xml.WriteEndElement(); } continue; }
                if (value is double d && !double.IsFinite(d) || value is float f && !float.IsFinite(f))
                    throw new InvalidOperationException("Excel cannot store a nonfinite numeric analysis result.");
                string reference = Column(c) + rowNumber.ToString(Invariant);
                if (Numeric(value) || value is DateTime || value is bool)
                {
                    Start("c"); Attr("r", reference);
                    int style = value is DateTime ? 4 : Numeric(value) && names[c].Contains('%') ? 3 : Numeric(value) && c >= groupColumns ? 2 : 0; Attr("s", total ? (style == 0 ? 5 : style + 4) : style);
                    if (value is bool) Attr("t", "b");
                    Start("v"); xml.WriteString(value is DateTime date ? date.ToOADate().ToString("R", Invariant) : value is bool flag ? (flag ? "1" : "0") : Convert.ToString(value, Invariant));
                    xml.WriteEndElement(); xml.WriteEndElement();
                }
                else TextCell(reference, Convert.ToString(value, Invariant) ?? "", total ? 5 : 0);
            }
            xml.WriteEndElement();
        }
        foreach (var values in sample) Row(values);
        while (reader.Read()) { var values = new object[count]; reader.GetValues(values); Row(values); }
        int lastDataRow = rowNumber; if (totals != null) Row(totals, true);
        xml.WriteEndElement(); Empty("autoFilter", ("ref", "A1:" + Column(count - 1) + lastDataRow.ToString(Invariant)));
        xml.WriteEndElement();
    }
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

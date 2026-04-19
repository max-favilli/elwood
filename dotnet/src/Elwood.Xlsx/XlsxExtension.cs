using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Elwood.Core;
using Elwood.Core.Abstractions;

namespace Elwood.Xlsx;

/// <summary>
/// Registers fromXlsx and toXlsx methods with an ElwoodEngine.
/// Usage: XlsxExtension.Register(engine);
/// </summary>
public static class XlsxExtension
{
    public static void Register(ElwoodEngine engine)
    {
        engine.RegisterMethod("fromXlsx", FromXlsx);
        engine.RegisterMethod("toXlsx", ToXlsx);
    }

    private static IElwoodValue FromXlsx(IElwoodValue target, List<IElwoodValue> args, IElwoodValueFactory factory)
    {
        var base64 = target.GetStringValue() ?? "";
        var hasHeaders = true;
        var sheetIndex = 0;
        string? sheetName = null;

        if (args.Count > 0 && args[0].Kind == ElwoodValueKind.Object)
        {
            var opts = args[0];
            var h = opts.GetProperty("headers");
            if (h is not null) hasHeaders = h.GetBooleanValue() || h.GetStringValue()?.ToLower() == "true";
            var s = opts.GetProperty("sheet");
            if (s is not null)
            {
                if (s.Kind == ElwoodValueKind.Number)
                    sheetIndex = (int)s.GetNumberValue();
                else
                    sheetName = s.GetStringValue();
            }
        }

        byte[] bytes;
        try { bytes = Convert.FromBase64String(base64); }
        catch { return factory.CreateArray([]); }

        using var stream = new MemoryStream(bytes);
        using var doc = SpreadsheetDocument.Open(stream, false);

        var workbookPart = doc.WorkbookPart;
        if (workbookPart is null) return factory.CreateArray([]);

        // Find the target sheet
        var sheets = workbookPart.Workbook?.Sheets?.Elements<Sheet>().ToList() ?? new List<Sheet>();
        Sheet? sheet = null;

        if (sheetName is not null)
            sheet = sheets.FirstOrDefault(s => s.Name?.Value == sheetName);
        else if (sheetIndex < sheets.Count)
            sheet = sheets[sheetIndex];

        if (sheet?.Id?.Value is null) return factory.CreateArray([]);

        var worksheetPart = (WorksheetPart)workbookPart.GetPartById(sheet.Id.Value!);
        var sheetData = worksheetPart.Worksheet?.GetFirstChild<SheetData>();
        if (sheetData is null) return factory.CreateArray([]);

        var sst = workbookPart.SharedStringTablePart?.SharedStringTable;
        var rows = sheetData.Elements<Row>().ToList();
        if (rows.Count == 0) return factory.CreateArray([]);

        // Parse all rows into string arrays
        var maxCol = 0;
        var parsed = new List<List<string>>();
        foreach (var row in rows)
        {
            var cells = row.Elements<Cell>().ToList();
            var values = new List<string>();
            var colIdx = 0;
            foreach (var cell in cells)
            {
                var cellCol = GetColumnIndex(cell.CellReference?.Value);
                // Fill gaps with empty strings
                while (colIdx < cellCol) { values.Add(""); colIdx++; }
                values.Add(GetCellValue(cell, sst));
                colIdx = cellCol + 1;
            }
            if (values.Count > maxCol) maxCol = values.Count;
            parsed.Add(values);
        }

        // Pad all rows to same length
        foreach (var row in parsed)
            while (row.Count < maxCol) row.Add("");

        if (hasHeaders && parsed.Count >= 1)
        {
            var headers = parsed[0];
            var result = new List<IElwoodValue>();
            for (var i = 1; i < parsed.Count; i++)
            {
                var row = parsed[i];
                var props = new List<KeyValuePair<string, IElwoodValue>>();
                for (var j = 0; j < headers.Count; j++)
                {
                    var header = !string.IsNullOrWhiteSpace(headers[j]) ? headers[j] : GetAlphabeticColumnName(j);
                    props.Add(new KeyValuePair<string, IElwoodValue>(header, factory.CreateString(j < row.Count ? row[j] : "")));
                }
                result.Add(factory.CreateObject(props));
            }
            return factory.CreateArray(result);
        }
        else
        {
            // No headers: auto-generated column names A, B, C, ...
            var colNames = Enumerable.Range(0, maxCol).Select(GetAlphabeticColumnName).ToList();
            return factory.CreateArray(parsed.Select(row =>
            {
                var props = new List<KeyValuePair<string, IElwoodValue>>();
                for (var j = 0; j < colNames.Count; j++)
                    props.Add(new KeyValuePair<string, IElwoodValue>(colNames[j], factory.CreateString(j < row.Count ? row[j] : "")));
                return factory.CreateObject(props);
            }));
        }
    }

    private static IElwoodValue ToXlsx(IElwoodValue target, List<IElwoodValue> args, IElwoodValueFactory factory)
    {
        var includeHeaders = true;
        var sheetNameOpt = "Sheet1";

        if (args.Count > 0 && args[0].Kind == ElwoodValueKind.Object)
        {
            var opts = args[0];
            var h = opts.GetProperty("headers");
            if (h is not null) includeHeaders = h.GetBooleanValue() || h.GetStringValue()?.ToLower() == "true";
            var s = opts.GetProperty("sheet");
            if (s is not null) sheetNameOpt = s.GetStringValue() ?? "Sheet1";
        }

        var items = target.EnumerateArray().ToList();

        // Collect all property names
        var allKeys = new List<string>();
        foreach (var item in items)
        {
            if (item.Kind == ElwoodValueKind.Object)
            {
                foreach (var key in item.GetPropertyNames())
                    if (!allKeys.Contains(key)) allKeys.Add(key);
            }
        }

        using var stream = new MemoryStream();
        using (var doc = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook))
        {
            var workbookPart = doc.AddWorkbookPart();
            workbookPart.Workbook = new Workbook();
            var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
            var sheetData = new SheetData();
            worksheetPart.Worksheet = new Worksheet(sheetData);

            var sheets = workbookPart.Workbook.AppendChild(new Sheets());
            sheets.Append(new Sheet
            {
                Id = doc.WorkbookPart!.GetIdOfPart(worksheetPart),
                SheetId = 1,
                Name = sheetNameOpt
            });

            uint rowIndex = 1;

            // Header row
            if (includeHeaders && allKeys.Count > 0)
            {
                var headerRow = new Row { RowIndex = rowIndex++ };
                foreach (var key in allKeys)
                    headerRow.Append(new Cell { DataType = CellValues.String, CellValue = new CellValue(key) });
                sheetData.Append(headerRow);
            }

            // Data rows
            foreach (var item in items)
            {
                var dataRow = new Row { RowIndex = rowIndex++ };
                if (item.Kind == ElwoodValueKind.Object)
                {
                    foreach (var key in allKeys)
                    {
                        var val = item.GetProperty(key);
                        var text = val is not null ? ValueToString(val) : "";
                        dataRow.Append(new Cell { DataType = CellValues.String, CellValue = new CellValue(text) });
                    }
                }
                sheetData.Append(dataRow);
            }
        }

        return factory.CreateString(Convert.ToBase64String(stream.ToArray()));
    }

    private static string GetCellValue(Cell cell, SharedStringTable? sst)
    {
        var value = cell.CellValue?.InnerText ?? "";
        if (cell.DataType is not null && cell.DataType.Value == CellValues.SharedString && sst is not null)
        {
            if (int.TryParse(value, out var idx) && idx < sst.ChildElements.Count)
                return sst.ChildElements[idx].InnerText;
        }
        return value;
    }

    private static int GetColumnIndex(string? cellReference)
    {
        if (string.IsNullOrEmpty(cellReference)) return 0;
        var col = 0;
        foreach (var c in cellReference)
        {
            if (char.IsLetter(c))
                col = col * 26 + (char.ToUpper(c) - 'A' + 1);
            else
                break;
        }
        return col - 1;
    }

    private static string GetAlphabeticColumnName(int index)
    {
        var name = "";
        var i = index + 1;
        while (i > 0) { i--; name = (char)('A' + i % 26) + name; i /= 26; }
        return name;
    }

    private static string ValueToString(IElwoodValue value) => value.Kind switch
    {
        ElwoodValueKind.String => value.GetStringValue() ?? "",
        ElwoodValueKind.Number => value.GetNumberValue().ToString(System.Globalization.CultureInfo.InvariantCulture),
        ElwoodValueKind.Boolean => value.GetBooleanValue() ? "true" : "false",
        ElwoodValueKind.Null => "",
        _ => value.GetStringValue() ?? ""
    };
}

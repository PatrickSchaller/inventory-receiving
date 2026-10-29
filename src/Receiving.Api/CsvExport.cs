using System.Globalization;
using System.Text;

namespace Receiving.Api;

public static class CsvExport
{
    public static byte[] Write(IEnumerable<Movement> movements)
    {
        var csv = new StringBuilder("Timestamp,Sku,Item,Kind,Quantity change,Balance,Reference,Reason\r\n");
        foreach (var movement in movements)
        {
            string[] cells = [Cell(movement.CreatedAt), Cell(movement.Sku), Cell(movement.ItemName),
                Cell(movement.Kind), movement.Delta.ToString(CultureInfo.InvariantCulture),
                movement.Balance.ToString(CultureInfo.InvariantCulture), Cell(movement.Reference), Cell(movement.Note)];
            csv.AppendJoin(',', cells).Append("\r\n");
        }
        return Encoding.UTF8.GetBytes(csv.ToString());
    }

    private static string Cell(string value)
    {
        // CSV quoting alone does not stop spreadsheet formulas from executing.
        if (value.TrimStart() is { Length: > 0 } trimmed && "=+-@".Contains(trimmed[0]))
            value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}

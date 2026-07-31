using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace MitPlanRecorder;

internal static partial class CsvImporter
{
    public static CsvDocument Read(string path, int? headerRowIndex = null)
    {
        var records = Parse(File.ReadAllText(path));
        if (records.Count == 0)
            throw new InvalidDataException("The CSV file is empty.");

        var selectedHeaderRow = Math.Clamp(headerRowIndex ?? DetectHeaderRow(records), 0, records.Count - 1);
        var width = records.Max(row => row.Count);
        var headers = records[selectedHeaderRow].Select((value, index) => string.IsNullOrWhiteSpace(value) ? $"Column {index + 1}" : value.Trim()).ToList();
        while (headers.Count < width)
            headers.Add($"Column {headers.Count + 1}");
        foreach (var row in records.Skip(selectedHeaderRow + 1))
            while (row.Count < width)
                row.Add(string.Empty);

        var document = new CsvDocument
        {
            Path = path,
            HeaderRowIndex = selectedHeaderRow,
            Headers = headers,
            Rows = records.Skip(selectedHeaderRow + 1).ToList(),
        };
        AutoMap(document);
        return document;
    }

    private static int DetectHeaderRow(IReadOnlyList<List<string>> records)
    {
        var bestIndex = 0;
        var bestScore = int.MinValue;
        for (var index = 0; index < Math.Min(records.Count, 30); index++)
        {
            var values = records[index].Select(Normalize).ToList();
            var score = values.Count(value => value.Contains("time") || value.Contains("timing")) * 4 +
                        values.Count(value => value.Contains("mechanic") || value.Contains("attack") || value.Contains("action")) * 4 +
                        values.Count(value => value.Contains("phase")) * 2 +
                        records[index].Count(value => InferTarget(value).Include) * 2 -
                        values.Count(value => value.Length == 0);
            if (score <= bestScore) continue;
            bestScore = score;
            bestIndex = index;
        }
        return bestIndex;
    }

    private static List<List<string>> Parse(string text)
    {
        var result = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (quoted)
            {
                if (character == '"' && index + 1 < text.Length && text[index + 1] == '"')
                {
                    field.Append('"');
                    index++;
                }
                else if (character == '"')
                    quoted = false;
                else
                    field.Append(character);
                continue;
            }
            if (character == '"')
                quoted = true;
            else if (character == ',')
            {
                row.Add(field.ToString());
                field.Clear();
            }
            else if (character is '\r' or '\n')
            {
                if (character == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
                    index++;
                row.Add(field.ToString());
                field.Clear();
                if (row.Any(value => !string.IsNullOrWhiteSpace(value)))
                    result.Add(row);
                row = [];
            }
            else
                field.Append(character);
        }
        row.Add(field.ToString());
        if (row.Any(value => !string.IsNullOrWhiteSpace(value)))
            result.Add(row);
        return result;
    }

    private static void AutoMap(CsvDocument document)
    {
        document.TimeColumn = FindHeader(document.Headers, "time", "timing", "timestamp");
        document.MechanicColumn = FindHeader(document.Headers, "mechanic", "attack", "action", "event", "damage");
        document.PhaseColumn = FindHeader(document.Headers, "phase", "part");
        document.MitigationColumns = document.Headers.Select((header, index) =>
        {
            var mapping = InferTarget(header);
            return new CsvMitigationColumn
            {
                Index = index,
                Header = header,
                Included = index != document.TimeColumn && index != document.MechanicColumn && index != document.PhaseColumn && mapping.Include,
                TargetJob = mapping.Job,
                TargetRole = mapping.Role,
            };
        }).ToList();
    }

    private static int FindHeader(IReadOnlyList<string> headers, params string[] terms)
    {
        for (var index = 0; index < headers.Count; index++)
            if (terms.Any(term => headers[index].Contains(term, StringComparison.OrdinalIgnoreCase)))
                return index;
        return -1;
    }

    public static (bool Include, string Job, string Role) InferTarget(string header)
    {
        var normalized = Normalize(header);
        bool HasToken(string token) => Regex.IsMatch(header, $@"(?:^|[^A-Za-z0-9]){Regex.Escape(token)}(?:$|[^A-Za-z0-9])", RegexOptions.IgnoreCase);
        var jobs = new[] { "WAR", "PLD", "DRK", "GNB", "WHM", "AST", "SCH", "SGE", "MNK", "DRG", "NIN", "SAM", "RPR", "VPR", "BRD", "MCH", "DNC", "BLM", "SMN", "RDM", "PCT", "BLU" };
        var job = jobs.FirstOrDefault(HasToken);
        var role = header switch
        {
            _ when HasToken("MT") => "MT",
            _ when HasToken("OT") => "OT",
            _ when HasToken("H1") || normalized.Contains("pure healer") => "Pure Healer (H1)",
            _ when HasToken("H2") || normalized.Contains("shield healer") => "Shield Healer (H2)",
            _ when HasToken("M1") || HasToken("D1") => "Melee 1 (M1) (D1)",
            _ when HasToken("M2") || HasToken("D2") => "Melee 2 (M2) (D2)",
            _ when HasToken("R1") || HasToken("D3") || normalized.Contains("phys ranged") => "Phys Ranged (R1) (D3)",
            _ when HasToken("R2") || HasToken("D4") || normalized.Contains("caster") => "Caster (R2) (D4)",
            _ => "Any Role",
        };
        if (job is "WHM" or "AST" && role == "Any Role") role = "Pure Healer (H1)";
        if (job is "SCH" or "SGE" && role == "Any Role") role = "Shield Healer (H2)";
        var include = job is not null || role != "Any Role" || normalized.Contains("mit");
        return (include, job ?? "Any Job", role);
    }

    public static bool TryParseTime(string value, out double seconds)
    {
        seconds = 0;
        value = value.Trim();
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds))
            return seconds >= 0;
        var pieces = value.Split(':');
        if (pieces.Length is < 2 or > 3 || pieces.Any(piece => !double.TryParse(piece, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
            return false;
        var values = pieces.Select(piece => double.Parse(piece, CultureInfo.InvariantCulture)).ToArray();
        seconds = values.Length == 2 ? values[0] * 60 + values[1] : values[0] * 3600 + values[1] * 60 + values[2];
        return seconds >= 0;
    }

    public static string Normalize(string value) => NonAlphaNumeric().Replace(value.ToLowerInvariant(), " ")
        .Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Where(token => token is not ("the" or "of" or "and" or "cast" or "castbar"))
        .Aggregate(new StringBuilder(), (builder, token) => builder.Append(token).Append(' '))
        .ToString().Trim();

    public static double NameSimilarity(string left, string right)
    {
        left = Normalize(left);
        right = Normalize(right);
        if (left.Length == 0 || right.Length == 0) return 0;
        if (left == right) return 1;
        if (left.Contains(right, StringComparison.Ordinal) || right.Contains(left, StringComparison.Ordinal)) return 0.9;
        var leftTokens = left.Split(' ').ToHashSet();
        var rightTokens = right.Split(' ').ToHashSet();
        var union = leftTokens.Union(rightTokens).Count();
        return union == 0 ? 0 : (double)leftTokens.Intersect(rightTokens).Count() / union;
    }

    [GeneratedRegex(@"[^a-z0-9]+", RegexOptions.IgnoreCase)]
    private static partial Regex NonAlphaNumeric();
}

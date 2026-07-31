using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MitPlan;

public sealed class GoogleSheetLoader(HttpClient httpClient)
{
    public static readonly string[] DefaultTabs =
    [
        "P1 | Kefka",
        "P2 | Forsaken Kefka",
        "P3 | Chaos & Exdeath",
        "P4 | Kefka Says",
        "P5 | Kefka Reimagined"
    ];

    private static readonly Regex SheetIdRegex = new(@"/spreadsheets/d/([^/]+)", RegexOptions.Compiled);

    public async Task<IReadOnlyList<PhasePlan>> LoadAsync(string sheetUrl, CancellationToken cancellationToken)
    {
        var match = SheetIdRegex.Match(sheetUrl);
        if (!match.Success)
            throw new InvalidOperationException("The URL does not contain a Google Sheets document ID.");

        var sheetId = match.Groups[1].Value;
        var phases = new List<PhasePlan>();

        foreach (var tab in DefaultTabs)
        {
            var url = $"https://docs.google.com/spreadsheets/d/{sheetId}/gviz/tq?tqx=out:csv&sheet={Uri.EscapeDataString(tab)}";
            using var response = await httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var csv = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            phases.Add(ParsePhase(tab, csv));
        }

        return phases;
    }

    internal static PhasePlan ParsePhase(string phaseName, string csv)
    {
        var rows = ParseCsv(csv);
        if (rows.Count == 0)
            return new PhasePlan(phaseName, 0, []);

        var header = rows[0];
        var globalTimeIndex = FindHeader(header, value => Normalize(value) == "time");
        if (globalTimeIndex < 0)
            throw new InvalidOperationException($"Could not find the Time column in {phaseName}.");

        var mechanicIndex = Enumerable.Range(0, globalTimeIndex)
            .Where(index => !string.IsNullOrWhiteSpace(header[index]))
            .DefaultIfEmpty(1)
            .Last();
        if (mechanicIndex == globalTimeIndex || mechanicIndex < 0)
            mechanicIndex = Math.Max(0, globalTimeIndex - 2);

        // The source plan places the mechanic label in the populated column immediately
        // before the blank spacer leading into Time. Data rows consistently use column 1.
        mechanicIndex = Math.Min(1, header.Count - 1);
        var phaseTimeIndex = globalTimeIndex + 1;

        var assignmentColumns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["MT"] = FindHeader(header, value => Normalize(value) == "mt"),
            ["OT"] = FindHeader(header, value => Normalize(value) == "ot"),
            ["WHM"] = FindHeader(header, value => Normalize(value).Contains("white mage")),
            ["AST"] = FindHeader(header, value => Normalize(value).Contains("astrologian")),
            ["SCH"] = FindHeader(header, value => Normalize(value) == "scholar"),
            ["SGE"] = FindHeader(header, value => Normalize(value) == "sage"),
            ["D1"] = FindHeader(header, value => Normalize(value) == "d1"),
            ["D2"] = FindHeader(header, value => Normalize(value) == "d2"),
            ["D3"] = FindHeader(header, value => Normalize(value) == "d3"),
            ["D4"] = FindHeader(header, value => Normalize(value) == "d4")
        };

        var entries = new List<TimelineEntry>();
        var phaseOffsets = new List<int>();

        foreach (var row in rows.Skip(1))
        {
            if (!TryGet(row, globalTimeIndex, out var globalText) || !TryParseTime(globalText, out var globalSeconds))
                continue;

            var mechanic = TryGet(row, mechanicIndex, out var mechanicText) ? Clean(mechanicText) : string.Empty;
            if (string.IsNullOrWhiteSpace(mechanic))
                continue;

            var phaseSeconds = 0;
            if (TryGet(row, phaseTimeIndex, out var phaseText) && TryParseTime(phaseText, out var parsedPhaseSeconds))
            {
                phaseSeconds = parsedPhaseSeconds;
                phaseOffsets.Add(globalSeconds - phaseSeconds);
            }

            var assignments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, index) in assignmentColumns)
            {
                if (index >= 0 && TryGet(row, index, out var value) && !string.IsNullOrWhiteSpace(value))
                    assignments[key] = Clean(value);
            }

            entries.Add(new TimelineEntry(phaseName, mechanic, globalSeconds, phaseSeconds, assignments));
        }

        var startSeconds = phaseOffsets.Count == 0 ? entries.FirstOrDefault()?.GlobalSeconds ?? 0 :
            (int)Math.Round(phaseOffsets.Average());
        return new PhasePlan(phaseName, startSeconds, entries.OrderBy(entry => entry.GlobalSeconds).ToList());
    }

    private static int FindHeader(IReadOnlyList<string> header, Func<string, bool> predicate)
    {
        for (var i = 0; i < header.Count; i++)
            if (predicate(header[i]))
                return i;
        return -1;
    }

    private static bool TryGet(IReadOnlyList<string> row, int index, out string value)
    {
        if (index >= 0 && index < row.Count)
        {
            value = row[index];
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static bool TryParseTime(string value, out int seconds)
    {
        seconds = 0;
        var parts = value.Trim().Split(':');
        return parts.Length == 2 &&
               int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes) &&
               int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var remainingSeconds) &&
               (seconds = minutes * 60 + remainingSeconds) >= 0;
    }

    private static string Normalize(string value) =>
        Regex.Replace(value.Trim().ToLowerInvariant(), @"\s+", " ");

    private static string Clean(string value)
    {
        var cleaned = value.Replace("\r", string.Empty).Trim();
        cleaned = Regex.Replace(cleaned, "[¹²³⁴⁵⁶⁷⁸⁹]", string.Empty);
        return cleaned.Replace("✔", string.Empty).Trim();
    }

    internal static List<List<string>> ParseCsv(string csv)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;

        for (var i = 0; i < csv.Length; i++)
        {
            var character = csv[i];
            if (quoted)
            {
                if (character == '"')
                {
                    if (i + 1 < csv.Length && csv[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    field.Append(character);
                }
            }
            else if (character == '"')
            {
                quoted = true;
            }
            else if (character == ',')
            {
                row.Add(field.ToString());
                field.Clear();
            }
            else if (character is '\r' or '\n')
            {
                if (character == '\r' && i + 1 < csv.Length && csv[i + 1] == '\n')
                    i++;
                row.Add(field.ToString());
                field.Clear();
                rows.Add(row);
                row = [];
            }
            else
            {
                field.Append(character);
            }
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }

        return rows;
    }
}

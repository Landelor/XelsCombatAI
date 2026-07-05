using System.Text;

namespace FightReview;

internal sealed record DutyContentInfo(uint ContentFinderConditionId, uint ContentTypeId, string ContentTypeName)
{
    public bool IsBossOnlyDuty =>
        this.ContentTypeName.Contains("Trial", StringComparison.OrdinalIgnoreCase) ||
        (this.ContentTypeName.Contains("Raid", StringComparison.OrdinalIgnoreCase) &&
         !this.ContentTypeName.Contains("Alliance", StringComparison.OrdinalIgnoreCase));
}

internal static class DutyContentLookup
{
    private static readonly Lazy<IReadOnlyDictionary<uint, DutyContentInfo>> Cache = new(Load);

    public static DutyContentInfo? Find(uint contentFinderConditionId)
    {
        return Cache.Value.TryGetValue(contentFinderConditionId, out var info)
            ? info
            : null;
    }

    private static IReadOnlyDictionary<uint, DutyContentInfo> Load()
    {
        var csvDirectory = FindCsvDirectory();
        if (csvDirectory == null)
        {
            return new Dictionary<uint, DutyContentInfo>();
        }

        var contentTypes = LoadContentTypes(Path.Combine(csvDirectory, "ContentType.csv"));
        var contentFinderPath = Path.Combine(csvDirectory, "ContentFinderCondition.csv");
        if (!File.Exists(contentFinderPath))
        {
            return new Dictionary<uint, DutyContentInfo>();
        }

        var rows = ReadCsv(contentFinderPath).ToArray();
        if (rows.Length == 0)
        {
            return new Dictionary<uint, DutyContentInfo>();
        }

        var keyColumn = ColumnIndex(rows[0], "#");
        var contentTypeColumn = ColumnIndex(rows[0], "ContentType");
        if (keyColumn < 0 || contentTypeColumn < 0)
        {
            return new Dictionary<uint, DutyContentInfo>();
        }

        var result = new Dictionary<uint, DutyContentInfo>();
        foreach (var row in rows.Skip(1))
        {
            if (row.Length <= Math.Max(keyColumn, contentTypeColumn) ||
                !uint.TryParse(row[keyColumn], out var cfcid) ||
                !uint.TryParse(row[contentTypeColumn], out var contentTypeId))
            {
                continue;
            }

            contentTypes.TryGetValue(contentTypeId, out var contentTypeName);
            result[cfcid] = new DutyContentInfo(cfcid, contentTypeId, contentTypeName ?? string.Empty);
        }

        return result;
    }

    private static IReadOnlyDictionary<uint, string> LoadContentTypes(string path)
    {
        if (!File.Exists(path))
        {
            return new Dictionary<uint, string>();
        }

        var rows = ReadCsv(path).ToArray();
        if (rows.Length == 0)
        {
            return new Dictionary<uint, string>();
        }

        var keyColumn = ColumnIndex(rows[0], "#");
        var nameColumn = ColumnIndex(rows[0], "Name");
        if (keyColumn < 0 || nameColumn < 0)
        {
            return new Dictionary<uint, string>();
        }

        var result = new Dictionary<uint, string>();
        foreach (var row in rows.Skip(1))
        {
            if (row.Length > Math.Max(keyColumn, nameColumn) && uint.TryParse(row[keyColumn], out var id))
            {
                result[id] = row[nameColumn];
            }
        }

        return result;
    }

    private static string? FindCsvDirectory()
    {
        var configured = Environment.GetEnvironmentVariable("FFXIV_DATAMINING_PATH");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var configuredCsv = NormalizeCsvDirectory(configured);
            if (configuredCsv != null)
            {
                return configuredCsv;
            }
        }

        foreach (var referenceRoot in ReferenceRoots())
        {
            var referenceCsv = Path.Combine(referenceRoot, "FfxivDatamining", "csv", "en");
            if (IsCsvDirectory(referenceCsv))
            {
                return referenceCsv;
            }
        }

        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory != null)
            {
                var siblingReferenceCsv = Path.GetFullPath(Path.Combine(
                    directory.FullName,
                    "..",
                    "XelsCombatAIReferences",
                    "FfxivDatamining",
                    "csv",
                    "en"));
                if (IsCsvDirectory(siblingReferenceCsv))
                {
                    return siblingReferenceCsv;
                }

                var directCsv = Path.Combine(directory.FullName, "csv", "en");
                if (IsCsvDirectory(directCsv))
                {
                    return directCsv;
                }

                directory = directory.Parent;
            }
        }

        return null;
    }

    private static IEnumerable<string> ReferenceRoots()
    {
        var referencesDir = Environment.GetEnvironmentVariable("XCAI_REFERENCES_DIR");
        if (!string.IsNullOrWhiteSpace(referencesDir))
        {
            yield return referencesDir;
        }

        var workspaceDir = Environment.GetEnvironmentVariable("XELS_WORKSPACE_DIR");
        if (!string.IsNullOrWhiteSpace(workspaceDir))
        {
            yield return Path.Combine(workspaceDir, "XelsCombatAIReferences");
        }
    }

    private static string? NormalizeCsvDirectory(string path)
    {
        if (IsCsvDirectory(path))
        {
            return path;
        }

        var csvEn = Path.Combine(path, "csv", "en");
        if (IsCsvDirectory(csvEn))
        {
            return csvEn;
        }

        return null;
    }

    private static bool IsCsvDirectory(string path)
    {
        return File.Exists(Path.Combine(path, "ContentFinderCondition.csv")) &&
               File.Exists(Path.Combine(path, "ContentType.csv"));
    }

    private static IEnumerable<string[]> ReadCsv(string path)
    {
        foreach (var line in File.ReadLines(path))
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                yield return SplitCsvLine(line);
            }
        }
    }

    private static int ColumnIndex(string[] header, string name)
    {
        for (var i = 0; i < header.Length; i++)
        {
            if (header[i].Equals(name, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private static string[] SplitCsvLine(string line)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else
                {
                    quoted = !quoted;
                }
            }
            else if (c == ',' && !quoted)
            {
                fields.Add(field.ToString());
                field.Clear();
            }
            else
            {
                field.Append(c);
            }
        }

        fields.Add(field.ToString());
        return fields.ToArray();
    }
}

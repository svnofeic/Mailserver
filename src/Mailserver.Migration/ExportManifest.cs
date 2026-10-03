using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mailserver.Migration;

/// <summary>
/// Contents of an exported mailbox (export.json next to the exported folders): which file belongs to which folder,
/// with flags and received date, so the export can be updated incrementally and restored with <see cref="ExportImporter"/>.
/// </summary>
public sealed class ExportManifest
{
    public const string FileName = "export.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public int Version { get; set; } = 1;

    public string Address { get; set; } = "";

    public string? Source { get; set; }

    public DateTimeOffset? Updated { get; set; }

    public List<ExportedFolder> Folders { get; set; } = [];

    public static ExportManifest? Load(string accountDirectory)
    {
        var path = Path.Combine(accountDirectory, FileName);
        return File.Exists(path) ? JsonSerializer.Deserialize<ExportManifest>(File.ReadAllText(path), JsonOptions) : null;
    }

    /// <summary>Writes the manifest atomically, so an interrupted export never leaves a truncated file behind.</summary>
    public void Save(string accountDirectory)
    {
        var path = Path.Combine(accountDirectory, FileName);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(temp, path, overwrite: true);
    }
}

public sealed class ExportedFolder
{
    /// <summary>Full name at the source server, e.g. "INBOX/Projekte".</summary>
    public string Name { get; set; } = "";

    public string Separator { get; set; } = "/";

    /// <summary>Special role announced by the source (INBOX, Sent, Drafts, Trash, Junk, Archive).</summary>
    public string? SpecialUse { get; set; }

    /// <summary>Directory below the account directory, with '/' as separator.</summary>
    public string Directory { get; set; } = "";

    public uint UidValidity { get; set; }

    public List<ExportedMessage> Messages { get; set; } = [];

    [JsonIgnore]
    public char SeparatorChar => Separator.Length == 0 ? '\0' : Separator[0];
}

public sealed class ExportedMessage
{
    public uint Uid { get; set; }

    /// <summary>File name inside the folder directory.</summary>
    public string File { get; set; } = "";

    public DateTimeOffset? Received { get; set; }

    /// <summary>IMAP flags and keywords, e.g. "\Seen", "$Projekt".</summary>
    public string Flags { get; set; } = "";

    public long Size { get; set; }
}

/// <summary>File and directory names that are valid on Windows and readable for people.</summary>
public static class ExportNames
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static string Sanitize(string? name, int maxLength = 60, string fallback = "_")
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['<', '>', ':', '"', '/', '\\', '|', '?', '*']).ToHashSet();
        var chars = (name ?? "").Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray();
        var result = new string(chars).Trim();
        if (result.Length > maxLength)
        {
            result = result[..maxLength].Trim();
        }

        result = result.TrimEnd('.', ' ');
        if (result.Length == 0 || result.All(c => c == '.'))
        {
            return fallback;
        }

        return Reserved.Contains(result) || Reserved.Contains(Path.GetFileNameWithoutExtension(result)) ? "_" + result : result;
    }

    /// <summary>Returns <paramref name="name"/> or "name (2)", "name (3)" … so that <paramref name="isTaken"/> is false.</summary>
    public static string Unique(string name, Func<string, bool> isTaken, string extension = "")
    {
        var candidate = name + extension;
        for (var i = 2; isTaken(candidate); i++)
        {
            candidate = $"{name} ({i}){extension}";
        }

        return candidate;
    }
}

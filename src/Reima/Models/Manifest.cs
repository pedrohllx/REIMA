using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Reima.Models;

/// <summary>The modpack recipe: everything the launcher needs to build the install.</summary>
public sealed record Manifest
{
    public required string Name { get; init; }
    public required string Version { get; init; }
    /// <summary>
    /// Where the published recipe lives (Drive share link, GitHub raw, any direct link). The launcher downloads it
    /// on every start, so changing a Drive pack + publishing the recipe updates every install.
    /// </summary>
    public string? ManifestUrl { get; init; }
    /// <summary>MO2 profile name to create/update.</summary>
    public string Profile { get; init; } = "REIMA";
    /// <summary>Clean base install: Anomaly 1.5.3 + Mod Organizer 2.</summary>
    public BaseGame? Base { get; init; }
    public List<OptionGroup> OptionGroups { get; init; } = [];
    public List<Asset> Assets { get; init; } = [];
    /// <summary>Key/value lines forced into ini/ltx files after install (e.g. user.ltx).</summary>
    public List<ConfigPatch> ConfigPatches { get; init; } = [];
    /// <summary>Big community packs (ROMEO, GeorgePack) the user already has: mods are imported from them instead of the Drive.</summary>
    public List<SourcePack> SourcePacks { get; init; } = [];

    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static Manifest Load(string path) =>
        JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path), Json)
        ?? throw new InvalidDataException($"Manifest vazio: {path}");

    public static Manifest Parse(string json) =>
        JsonSerializer.Deserialize<Manifest>(json, Json) ?? throw new InvalidDataException("Manifest vazio");
}

/// <summary>A choice shown on the first screen. Exclusive groups render as radio buttons.</summary>
public sealed record OptionGroup
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public bool Exclusive { get; init; } = true;
    public List<Option> Options { get; init; } = [];
}

public sealed record Option
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public string Description { get; init; } = "";
    public bool Default { get; init; }
}

public enum AssetKind
{
    /// <summary>Extracted into MO2\mods\&lt;Target&gt; and added to the profile.</summary>
    Mod,
    /// <summary>Extracted over the Anomaly folder (Target = sub path, e.g. "bin"). Files replaced are backed up.</summary>
    Anomaly,
    /// <summary>Extracted over the MO2 folder.</summary>
    Mo2,
}

public sealed record Asset
{
    public required string Id { get; init; }
    /// <summary>Human name. For Mod assets this is also the MO2 mod folder name unless Target is set.</summary>
    public required string Name { get; init; }
    /// <summary>Download URL (http/https, Google Drive share link, GitHub release). Empty = manual file.</summary>
    public string? Url { get; init; }
    /// <summary>Where to get it by hand when Url is empty (e.g. "Discord CLIKRΔD #romeo").</summary>
    public string? ManualHint { get; init; }
    public required string File { get; init; }
    public string? Sha256 { get; init; }
    /// <summary>MD5 as published in GAMMA's mods.txt.</summary>
    public string? Md5 { get; init; }
    public AssetKind Kind { get; init; } = AssetKind.Mod;
    public string? Target { get; init; }
    /// <summary>Folder inside the archive to use as the root (e.g. "gamedata/.." or "Mod Name").</summary>
    public string? ArchiveRoot { get; init; }
    /// <summary>GAMMA-style install instructions: these archive folders are merged into the mod (in order).</summary>
    public List<string> ArchiveFolders { get; init; } = [];
    /// <summary>Info page (ModDB/GitHub), shown to the user.</summary>
    public string? Page { get; init; }
    /// <summary>Original ModDB download link (addons/start/ID). Kept when the file is re-hosted on Drive.</summary>
    public string? Source { get; init; }
    /// <summary>ModDB download id of the build in this recipe; compared with GAMMA's list to detect outdated mods.</summary>
    public string? SourceVersion { get; init; }
    /// <summary>Archive name unknown (GAMMA update): assisted download accepts any new archive and saves it as File.</summary>
    public bool AnyFile { get; init; }
    /// <summary>Tooling only: MO2 mod folder that tools/pack-mods.js packs into File.</summary>
    public string? PackFrom { get; init; }
    /// <summary>Where this mod is inside a source pack the user owns (ROMEO / GeorgePack). Drive stays the fallback.</summary>
    public PackRef? FromPack { get; init; }
    /// <summary>Anomaly/MO2 overlay that is taken out again when its option is switched off (OptiScaler). Never set it on saves/settings.</summary>
    public bool RemoveWhenOff { get; init; }
    /// <summary>Separator (MO2 group) this mod goes under.</summary>
    public string? Separator { get; init; }
    public bool Enabled { get; init; } = true;
    /// <summary>Option ids that must be selected for this asset to be used. Empty = always.</summary>
    public List<string> Requires { get; init; } = [];
    /// <summary>Option ids that disable this asset when selected.</summary>
    public List<string> Excludes { get; init; } = [];

    [JsonIgnore] public string ModFolder => Target ?? Name;
    [JsonIgnore] public bool IsManual => string.IsNullOrWhiteSpace(Url);

    public bool AppliesTo(IReadOnlySet<string> selected) =>
        Requires.All(selected.Contains) && !Excludes.Any(selected.Contains);
}

public sealed record BaseGame
{
    public required BaseFile Anomaly { get; init; }
    public required BaseFile Mo2 { get; init; }
}

public sealed record BaseFile
{
    /// <summary>http(s) or magnet: link. Magnets are fetched with aria2c (torrent).</summary>
    public required string Url { get; init; }
    public required string File { get; init; }
    public string? Sha256 { get; init; }
    /// <summary>Page to open for a manual download if the automatic one fails.</summary>
    public string? Page { get; init; }
}

/// <summary>A community pack the user downloads once (Discord etc.); REIMA pulls single mods out of it.</summary>
public sealed record SourcePack
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    /// <summary>Where to get it (shown to the user).</summary>
    public string? Hint { get; init; }
    /// <summary>Archive file names of the pack (main + hotfixes). Searched in the user's Downloads, cache folders and the chosen folder.</summary>
    public List<string> Files { get; init; } = [];
}

/// <summary>Location of one mod folder inside a source pack.</summary>
public sealed record PackRef
{
    public required string Pack { get; init; }
    /// <summary>Which of the pack's files holds it.</summary>
    public required string Archive { get; init; }
    /// <summary>Optional archive nested inside Archive (GeorgePack: "Gamma dist\data\mods03.7z").</summary>
    public string? Inner { get; init; }
    /// <summary>Folder (inside Inner or Archive) whose contents are the mod.</summary>
    public required string Path { get; init; }
}

public sealed record ConfigPatch
{
    /// <summary>Path relative to the Anomaly folder, e.g. "appdata/user.ltx".</summary>
    public required string File { get; init; }
    public Dictionary<string, string> Set { get; init; } = [];
    public List<string> Requires { get; init; } = [];
}

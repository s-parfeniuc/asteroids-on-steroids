using System.Text.Json;
using System.Text.Json.Serialization;

namespace AsteroidsGame.Config;

public static class GameConfigLoader
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        WriteIndented               = true,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling         = JsonCommentHandling.Skip,
        AllowTrailingCommas         = true,
    };

    // ── Load ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Loads game_config.json and all shape files from the given assets directory.
    /// Shapes are keyed by filename stem (e.g. "player_ship" for player_ship.json).
    /// </summary>
    public static (GameConfig Config, Dictionary<string, ShapeData> Shapes) Load(string assetsDir)
    {
        string configPath = Path.Combine(assetsDir, "game_config.json");
        var config = Deserialize<GameConfig>(configPath);
        var shapes = LoadShapes(Path.Combine(assetsDir, "shapes"));
        return (config, shapes);
    }

    public static Dictionary<string, ShapeData> LoadShapes(string shapesDir)
    {
        var result = new Dictionary<string, ShapeData>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(shapesDir)) return result;

        foreach (string file in Directory.GetFiles(shapesDir, "*.json"))
        {
            try
            {
                var shape = Deserialize<ShapeData>(file);
                result[Path.GetFileNameWithoutExtension(file)] = shape;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[GameConfigLoader] Skipping malformed shape '{file}': {ex.Message}");
            }
        }
        return result;
    }

    // ── Save ──────────────────────────────────────────────────────────────────

    /// <summary>Serialises the config back to game_config.json (demo / editor use only).</summary>
    public static void Save(GameConfig config, string assetsDir)
    {
        Directory.CreateDirectory(assetsDir);
        File.WriteAllText(Path.Combine(assetsDir, "game_config.json"),
                          JsonSerializer.Serialize(config, Opts));
    }

    /// <summary>Writes a shape file to Assets/shapes/. Filename defaults to shape.Name if null.</summary>
    public static void SaveShape(ShapeData shape, string assetsDir, string? filename = null)
    {
        string shapesDir = Path.Combine(assetsDir, "shapes");
        Directory.CreateDirectory(shapesDir);
        string stem = filename ?? shape.Name.ToLowerInvariant().Replace(' ', '_');
        if (!stem.EndsWith(".json")) stem += ".json";
        File.WriteAllText(Path.Combine(shapesDir, stem), JsonSerializer.Serialize(shape, Opts));
    }

    // ── Discovery ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Walks up from startDir until it finds a sibling folder named "Assets".
    /// Typical call: FindAssetsDir(AppContext.BaseDirectory)
    /// </summary>
    public static string FindAssetsDir(string startDir)
    {
        string? dir = Path.GetFullPath(startDir);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir, "Assets");
            if (Directory.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new DirectoryNotFoundException(
            $"Could not locate an 'Assets' directory by walking up from '{startDir}'.");
    }

    // ── Internal ──────────────────────────────────────────────────────────────

    private static T Deserialize<T>(string path)
    {
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<T>(stream, Opts)
               ?? throw new InvalidDataException($"Null result deserialising '{path}'.");
    }
}

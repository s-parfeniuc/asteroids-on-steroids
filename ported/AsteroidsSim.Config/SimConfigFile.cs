using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using AsteroidsSim.Fracture;

namespace AsteroidsSim.Config;

/// <summary>
/// Loads <c>Assets/sim.json</c>, the single source of truth for the simulation's tuning, model
/// constants and materials.
/// </summary>
/// <remarks>
/// <para><b>Strict.</b> Every field of <see cref="SimTuning"/> and <see cref="ModelConstants"/> and
/// every material field must be present, and any key that names nothing is rejected, so a typo or a
/// renamed parameter fails with its path instead of silently leaving a zero behind.</para>
///
/// <para><b>Exact.</b> Floats are parsed straight to <c>float</c> and build constants to
/// <c>double</c>, both correctly rounded, so a literal in the file yields the same bits as the same
/// literal in C#.</para>
/// </remarks>
public static class SimConfigFile
{
    private static readonly JsonDocumentOptions Options = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Finds <c>Assets/sim.json</c> by walking up from the executable's directory, then from the
    /// working directory.
    /// </summary>
    public static string Find()
    {
        foreach (string start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, "Assets", "sim.json");
                if (File.Exists(candidate)) return candidate;
            }
        }
        throw new FileNotFoundException(
            $"Assets/sim.json not found above {AppContext.BaseDirectory} or {Directory.GetCurrentDirectory()}");
    }

    /// <summary>Loads the configuration found by <see cref="Find"/>.</summary>
    public static SimConfig Load() => Load(Find());

    public static SimConfig Load(string path) => Parse(File.ReadAllText(path), path);

    public static SimConfig Parse(string json, string source = "sim.json")
    {
        using var doc = JsonDocument.Parse(json, Options);
        JsonElement root = doc.RootElement;
        RequireKind(root, JsonValueKind.Object, source);
        RequireOnlyKeys(root, source, "tuning", "materials", "constants");

        object tuning = default(SimTuning);
        FillFields(tuning, Member(root, "tuning", source), $"{source}: tuning", skip: nameof(SimTuning.Constants));

        object constants = default(ModelConstants);
        FillGroups(constants, Member(root, "constants", source), $"{source}: constants");

        var t = (SimTuning)tuning;
        t.Constants = (ModelConstants)constants;
        return new SimConfig
        {
            Tuning = t,
            Materials = ReadMaterials(Member(root, "materials", source), $"{source}: materials"),
        };
    }

    // ── sections ─────────────────────────────────────────────────────────────

    /// <summary>Sets every public field of a boxed struct from one JSON object, by camelCase name.</summary>
    private static void FillFields(object boxed, JsonElement obj, string where, string? skip = null)
    {
        RequireKind(obj, JsonValueKind.Object, where);
        var fields = FieldsByKey(boxed.GetType(), skip);
        var seen = new HashSet<string>();
        foreach (JsonProperty p in obj.EnumerateObject())
        {
            if (!fields.TryGetValue(p.Name, out FieldInfo? f))
                throw new FormatException($"{where}: unknown key '{p.Name}'");
            f.SetValue(boxed, Read(p.Value, f.FieldType, $"{where}.{p.Name}"));
            seen.Add(p.Name);
        }
        RequireAll(fields.Keys, seen, where);
    }

    /// <summary>Like <see cref="FillFields"/>, but the keys are spread over named groups.</summary>
    private static void FillGroups(object boxed, JsonElement obj, string where)
    {
        RequireKind(obj, JsonValueKind.Object, where);
        var fields = FieldsByKey(boxed.GetType(), null);
        var seen = new HashSet<string>();
        foreach (JsonProperty group in obj.EnumerateObject())
        {
            string gw = $"{where}.{group.Name}";
            RequireKind(group.Value, JsonValueKind.Object, gw);
            foreach (JsonProperty p in group.Value.EnumerateObject())
            {
                if (!fields.TryGetValue(p.Name, out FieldInfo? f))
                    throw new FormatException($"{gw}: unknown key '{p.Name}'");
                if (!seen.Add(p.Name))
                    throw new FormatException($"{gw}: '{p.Name}' is already set in another group");
                f.SetValue(boxed, Read(p.Value, f.FieldType, $"{gw}.{p.Name}"));
            }
        }
        RequireAll(fields.Keys, seen, where);
    }

    private static Material[] ReadMaterials(JsonElement arr, string where)
    {
        RequireKind(arr, JsonValueKind.Array, where);
        var list = new List<Material>();
        var names = new HashSet<string>();
        foreach (JsonElement m in arr.EnumerateArray())
        {
            string mw = $"{where}[{list.Count}]";
            RequireKind(m, JsonValueKind.Object, mw);
            RequireOnlyKeys(m, mw, "name", "rho", "c", "strain", "chi", "crush", "crushRate", "shedLimit", "dent");
            string name = (string)Read(Member(m, "name", mw), typeof(string), $"{mw}.name");
            if (!names.Add(name)) throw new FormatException($"{mw}: duplicate material '{name}'");
            float F(string key) => (float)Read(Member(m, key, mw), typeof(float), $"{mw}.{key}");
            list.Add(new Material(name, F("rho"), F("c"), F("strain"), F("chi"),
                F("crush"), F("crushRate"), F("shedLimit"), F("dent")));
        }
        return list.ToArray();
    }

    // ── values ───────────────────────────────────────────────────────────────

    private static object Read(JsonElement v, Type type, string where)
    {
        if (type == typeof(float))
        {
            if (v.ValueKind == JsonValueKind.Number && v.TryGetSingle(out float f)) return f;
        }
        else if (type == typeof(double))
        {
            if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double d)) return d;
        }
        else if (type == typeof(int))
        {
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i)) return i;
        }
        else if (type == typeof(bool))
        {
            if (v.ValueKind == JsonValueKind.True) return true;
            if (v.ValueKind == JsonValueKind.False) return false;
        }
        else if (type == typeof(string))
        {
            if (v.ValueKind == JsonValueKind.String) return v.GetString()!;
        }
        else throw new InvalidOperationException($"{where}: fields of type {type.Name} are not supported");
        throw new FormatException($"{where}: expected {type.Name}, found {v.ValueKind} '{v.GetRawText()}'");
    }

    private static Dictionary<string, FieldInfo> FieldsByKey(Type t, string? skip)
    {
        var map = new Dictionary<string, FieldInfo>();
        foreach (FieldInfo f in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
            if (f.Name != skip) map[char.ToLowerInvariant(f.Name[0]) + f.Name.Substring(1)] = f;
        return map;
    }

    private static JsonElement Member(JsonElement obj, string key, string where)
        => obj.TryGetProperty(key, out JsonElement v) ? v : throw new FormatException($"{where}: missing '{key}'");

    private static void RequireKind(JsonElement e, JsonValueKind kind, string where)
    {
        if (e.ValueKind != kind) throw new FormatException($"{where}: expected {kind}, found {e.ValueKind}");
    }

    private static void RequireOnlyKeys(JsonElement obj, string where, params string[] allowed)
    {
        foreach (JsonProperty p in obj.EnumerateObject())
            if (Array.IndexOf(allowed, p.Name) < 0) throw new FormatException($"{where}: unknown key '{p.Name}'");
        foreach (string k in allowed)
            if (!obj.TryGetProperty(k, out _)) throw new FormatException($"{where}: missing '{k}'");
    }

    private static void RequireAll(IEnumerable<string> keys, HashSet<string> seen, string where)
    {
        var missing = new List<string>();
        foreach (string k in keys) if (!seen.Contains(k)) missing.Add(k);
        if (missing.Count > 0) throw new FormatException($"{where}: missing {string.Join(", ", missing)}");
    }
}

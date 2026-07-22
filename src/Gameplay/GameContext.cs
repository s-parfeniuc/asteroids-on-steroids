using System;
using System.IO;
using System.Text.Json;
using AsteroidsEngine.Engine.Destruction;
using AsteroidsEngine.Engine.Input;
using AsteroidsGame.Config;

namespace AsteroidsGame;

/// <summary>
/// Shared services passed to every game state. Owns nothing that belongs to a
/// single state — per-state worlds and systems live on the states themselves.
/// </summary>
public sealed class GameContext
{
    public GameConfig                    Config   { get; }
    public Dictionary<string, ShapeData> Shapes   { get; }
    public InputSystem                   Input    { get; }
    public int                           ScreenW  { get; }
    public int                           ScreenH  { get; }
    public Random                        Rng      { get; } = new();

    public Score      Score      { get; } = new();
    public CellBudget CellBudget { get; } = new();

    // ── Difficulty ────────────────────────────────────────────────────────────
    private static readonly List<DifficultyConfig> _defaultDiffs = new() { new DifficultyConfig() };
    /// <summary>Available presets (falls back to a single Normal if none are configured).</summary>
    public List<DifficultyConfig> Difficulties => Config.Difficulties.Count > 0 ? Config.Difficulties : _defaultDiffs;
    /// <summary>Currently selected preset index (set by the menu).</summary>
    public int DifficultyIndex { get; set; }
    public DifficultyConfig Difficulty => Difficulties[Math.Clamp(DifficultyIndex, 0, Difficulties.Count - 1)];
    /// <summary>Player impact coefficient scaled by the difficulty's player-damage multiplier.</summary>
    public float PlayerImpactCoeff => Config.Player.PlayerImpactCoeff * Difficulty.PlayerDamageMult;

    /// <summary>Persisted best score across runs (loaded at startup, saved when beaten).</summary>
    public float HighScore { get; private set; }
    /// <summary>Screen-shake intensity setting (0 = off), applied to the Camera; persisted.</summary>
    public float ShakeIntensity { get; set; } = 1f;
    /// <summary>Set by a state (e.g. main menu on Esc) to request app exit; the main loop honours it.</summary>
    public bool QuitRequested { get; set; }
    private readonly string _savePath;

    public GameContext(GameConfig config, Dictionary<string, ShapeData> shapes,
                       InputSystem input, int screenW, int screenH)
    {
        Config  = config;
        Shapes  = shapes;
        Input   = input;
        ScreenW = screenW;
        ScreenH = screenH;
        ApplyFractureTuning(config.Fracture);

        _savePath = BuildSavePath();
        DifficultyIndex = Difficulties.Count > 1 ? 1 : 0;   // default to the second preset (Normal)
        Load();
    }

    /// <summary>Records a finished run; returns true and persists if it's a new best.</summary>
    public bool SubmitScore(float score)
    {
        if (score <= HighScore) return false;
        HighScore = score;
        Save();
        return true;
    }

    private sealed class SaveData
    {
        public float HighScore { get; set; }
        public int   Difficulty { get; set; }
        public float ShakeIntensity { get; set; } = 1f;
    }

    private static string BuildSavePath()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AsteroidsOnSteroids");
        try { Directory.CreateDirectory(dir); } catch { /* fall back to a bare filename below */ }
        return Path.Combine(dir, "save.json");
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_savePath)) return;
            var d = JsonSerializer.Deserialize<SaveData>(File.ReadAllText(_savePath));
            if (d == null) return;
            HighScore      = d.HighScore;
            ShakeIntensity = d.ShakeIntensity;
            if (Difficulties.Count > 0)
                DifficultyIndex = Math.Clamp(d.Difficulty, 0, Difficulties.Count - 1);
        }
        catch { /* corrupt/unreadable → keep defaults */ }
    }

    /// <summary>Persists high score + difficulty + settings. Non-fatal on failure.</summary>
    public void Save()
    {
        try
        {
            var d = new SaveData { HighScore = HighScore, Difficulty = DifficultyIndex, ShakeIntensity = ShakeIntensity };
            File.WriteAllText(_savePath, JsonSerializer.Serialize(d));
        }
        catch { /* non-fatal */ }
    }

    /// <summary>Push the global fracture tuning constants from config into the engine.</summary>
    public static void ApplyFractureTuning(FractureGlobalConfig f)
    {
        FractureTuning.EnergyScale         = f.EnergyScale;
        FractureTuning.ReachMin            = f.ReachMin;
        FractureTuning.ReachMax            = f.ReachMax;
        FractureTuning.VaporEff            = f.VaporEff;
        FractureTuning.BreakPerp           = f.BreakPerp;
        FractureTuning.FlingScale          = f.FlingScale;
        FractureTuning.AlignExponent       = f.AlignExponent;
        FractureTuning.SpinCap             = f.SpinCap;
        FractureTuning.FragmentSpeedMax    = f.FragmentSpeedMax;
        FractureTuning.TumbleScale         = f.TumbleScale;
        FractureTuning.FragmentSpinMax     = f.FragmentSpinMax;
        FractureTuning.SpinProfileBase     = f.SpinProfileBase;
        FractureTuning.CrackSpeedRefVelocity = f.CrackSpeedRefVelocity;
        FractureTuning.CrackSpeedVelExponent = f.CrackSpeedVelExponent;
        FractureTuning.SplitStressInherit    = f.SplitStressInherit;
    }
}

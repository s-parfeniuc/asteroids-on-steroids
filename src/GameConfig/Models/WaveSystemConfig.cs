namespace AsteroidsGame.Config;

public class WaveSystemConfig
{
    public int   BaseCellCap                { get; set; } = 300;
    public int   MaxCellCap                 { get; set; } = 2000;
    public int   CellCapGrowthAmount        { get; set; } = 30;
    public float GrowthIntervalSeconds      { get; set; } = 30f;
    public int   BaseBudget                 { get; set; } = 20;
    public int   BudgetGrowthPerInterval    { get; set; } = 5;
    public float TriggerThreshold           { get; set; } = 0.30f;
    public float GracePeriodSeconds         { get; set; } = 8.0f;
    public float HardTriggerIntervalSeconds { get; set; } = 30.0f;
    public float SpawnDelaySeconds          { get; set; } = 1.5f;
    public float SizeBiasStart              { get; set; } = -0.2f;
    public float SizeBiasEnd                { get; set; } = 0.6f;
    public float SizeBiasRampEnd            { get; set; } = 600.0f;
    public float MothershpSpawnTime         { get; set; } = 600.0f;

    public Dictionary<string, SpawnBiasEntry> SpawnBias { get; set; } = new();

    /// <summary>Scripted one-shot waves that fire at specific game times with their own weights,
    /// budget, cell cap, and banner — independent of the normal wave loop.</summary>
    public List<SpecialWaveConfig> SpecialWaves { get; set; } = new();

    /// <summary>Spawn pattern for normal waves (special waves may override with their own).</summary>
    public SpawnPatternConfig Pattern { get; set; } = new();

    /// <summary>Anti-camping response: lingering near the border rim builds a timer that sends
    /// hunter waves at the player from their nearest side.</summary>
    public CampingResponseConfig CampingResponse { get; set; } = new();
}

/// <summary>
/// Camping is tracked as time spent inside the border band (borderHazard.hazardZone + ZoneDepth
/// from any edge). The timer DECAYS when the player leaves — it does not reset — so dipping in
/// and out doesn't cheese it. At TriggerSeconds a hunter wave fires at the player from their
/// nearest side, then again every RepeatSeconds while they stay camped.
/// </summary>
public class CampingResponseConfig
{
    public bool  Enabled        { get; set; } = true;
    /// <summary>Band beyond the erosion rim that still counts as camping (px).</summary>
    public float ZoneDepth      { get; set; } = 200f;
    /// <summary>Corner reach as a multiple of the edge band: near a corner (close to two edges at
    /// once) the zone extends this much further in, so hugging a corner is caught sooner and deeper
    /// than hugging a flat edge.</summary>
    public float CornerScale    { get; set; } = 1.8f;
    public float TriggerSeconds { get; set; } = 20f;
    /// <summary>Timer decay per second while outside the zone (1 = unwinds as fast as it builds).</summary>
    public float DecayRate      { get; set; } = 0.5f;
    /// <summary>Cadence of further hunter waves while the player keeps camping.</summary>
    public float RepeatSeconds  { get; set; } = 12f;
    public int   Budget         { get; set; } = 90;
    public int   CellCap        { get; set; } = 400;
    public float SizeBias       { get; set; } = 0f;
    public string Banner        { get; set; } = "HUNTERS INBOUND";
    /// <summary>Full-screen grey "you're exposed" filter alpha at full pressure (distinct from the
    /// red erosion tint). 0 disables it.</summary>
    public float TintMaxAlpha   { get; set; } = 70f;
    public Dictionary<string, float> Weights { get; set; } = new() { ["drone"] = 1f };
    public SpawnPatternConfig Pattern { get; set; } = new()
        { Pattern = "burst", Direction = "atPlayer", Side = "nearPlayer", SpawnDuration = 2f };
}

public class SpecialWaveConfig
{
    public float  TriggerTime { get; set; }                     // game seconds at which it fires
    public int    Budget      { get; set; } = 120;
    public int    CellCap     { get; set; } = 500;
    public float  SizeBias    { get; set; } = 0f;               // 0 = uniform sizing
    public string Banner      { get; set; } = "SPECIAL WAVE";
    public Dictionary<string, float> Weights { get; set; } = new();  // asteroid/alien key → absolute weight
    /// <summary>Per-wave spawn pattern; null = the wave system's default pattern.</summary>
    public SpawnPatternConfig? Pattern { get; set; }
}

/// <summary>How a wave's bodies are placed and aimed when they enter the map.</summary>
public class SpawnPatternConfig
{
    /// <summary>scattered (independent border spots — the classic) · burst (one tight cluster
    /// around an anchor on one side) · wall (spread along one side) · pincer (split across two
    /// opposite sides).</summary>
    public string Pattern { get; set; } = "scattered";
    /// <summary>inward (at the world centre) · atPlayer (at the player's position at RELEASE time)
    /// · random · fixed (FixedAngle).</summary>
    public string Direction { get; set; } = "inward";
    /// <summary>Which border the wave enters from (burst/wall/pincer): random · nearPlayer (the
    /// side closest to the player, anchored at their projection — the anti-camping entry).</summary>
    public string Side { get; set; } = "random";
    /// <summary>Aim angle in degrees when Direction == "fixed" (0 = +X, 90 = +Y/down).</summary>
    public float FixedAngle { get; set; } = 0f;
    /// <summary>Seconds the wave takes to trickle in (bodies released spread across the window).
    /// 0 = everything appears at once.</summary>
    public float SpawnDuration { get; set; } = 0f;
    /// <summary>burst: cluster radius (px) around the anchor point.</summary>
    public float BurstRadius { get; set; } = 420f;
    /// <summary>wall/pincer: fraction of the side's length the line occupies (0..1).</summary>
    public float Spread { get; set; } = 0.6f;
    /// <summary>Multiplier on each body's sampled entry speed.</summary>
    public float SpeedMult { get; set; } = 1f;
    /// <summary>Aim cone half-angle (radians) jittered around the pattern direction.</summary>
    public float AimJitter { get; set; } = 0.35f;
}

public class SpawnBiasEntry
{
    public float W0 { get; set; }
    public float W1 { get; set; }
    public float T0 { get; set; }
    public float T1 { get; set; }
}

public class VortexConfig
{
    public float Centripetal          { get; set; } = 0.05f;
    public float Tangential           { get; set; } = 0.02f;
    public float Deadzone             { get; set; } = 800f;
    public float CapFrames            { get; set; } = 8f;
    public float VariationCentripetal { get; set; } = 0.3f;
    public float VariationTangential  { get; set; } = 0.3f;

    // ── Moving centre (Lissajous orbit around the map centre) ──────────────────
    /// <summary>Orbit amplitude (px) along X / Y. Auto-clamped so the centre never comes within
    /// BorderMargin of any edge. 0 = stationary on that axis.</summary>
    public float MoveAmpX             { get; set; } = 0f;
    public float MoveAmpY             { get; set; } = 0f;
    /// <summary>Seconds per full oscillation along X / Y. Distinct values trace an open Lissajous path.</summary>
    public float MovePeriodX          { get; set; } = 40f;
    public float MovePeriodY          { get; set; } = 31f;
    /// <summary>Phase offset (radians) of the Y oscillation — shapes the Lissajous figure (default π/2).</summary>
    public float MovePhase            { get; set; } = 1.5707964f;
    /// <summary>Keep-out distance (px) from the map borders the centre must never cross.</summary>
    public float BorderMargin         { get; set; } = 700f;
}

/// <summary>
/// Vortex visualisation: sporadic wind-gust motes advected along the real force field (sparse and
/// slow at the calm eye, dense and fast further out — the field's terminal speed grows with radius),
/// drawn as fading streaks, plus an optional screen-space swirl warp centred on the eye.
/// </summary>
public class VortexFxConfig
{
    public bool  Enabled       { get; set; } = true;
    /// <summary>Seconds between gust bursts (±GustJitter).</summary>
    public float GustInterval  { get; set; } = 0.5f;
    public float GustJitter    { get; set; } = 0.6f;
    public int   MotesPerGust  { get; set; } = 14;
    public int   MaxMotes      { get; set; } = 400;
    public float Ttl           { get; set; } = 2.2f;
    public float TtlJitter     { get; set; } = 0.4f;
    /// <summary>Radius (world px) of the disc around the eye where motes spawn. Area-uniform, so
    /// more appear further out — matching the field getting stronger with radius.</summary>
    public float MaxRadius     { get; set; } = 1600f;
    /// <summary>Streak tail length, in seconds of the mote's velocity (faster motes → longer tails).</summary>
    public float StreakSeconds { get; set; } = 0.09f;
    /// <summary>Peak streak alpha (0..255) at a mote's mid-life; lower = fainter, clearer currents.</summary>
    public float StreakAlpha   { get; set; } = 120f;
    /// <summary>Streak line width (px).</summary>
    public float StreakWidth   { get; set; } = 2.5f;
    /// <summary>Visual multiplier on the advection speed (does not affect physics).</summary>
    public float SpeedScale    { get; set; } = 1f;
    public float[] Color       { get; set; } = [150f, 130f, 240f];

    // ── Screen-space swirl warp centred on the eye ──────────────────────────────
    public bool  WarpEnabled   { get; set; } = true;
    /// <summary>Radius (world px) of the warp disc — keep tight to the eye.</summary>
    public float WarpRadius    { get; set; } = 320f;
    /// <summary>Peak twist (radians) at the eye, falling to 0 at WarpRadius.</summary>
    public float WarpStrength  { get; set; } = 1.6f;
    /// <summary>Falloff exponent of the twist from eye→edge (higher = more concentrated at the centre).</summary>
    public float WarpFalloffExp { get; set; } = 3f;
    public int   WarpGrid      { get; set; } = 24;
}

/// <summary>Corner minimap: asteroid dots (sized by area, coloured by material) + actor blips.</summary>
public class MinimapConfig
{
    public bool   Enabled  { get; set; } = true;
    /// <summary>Panel width in px at 1080p (height follows the world aspect; both scale by UiScale).</summary>
    public float  Width    { get; set; } = 260f;
    public float  Margin   { get; set; } = 16f;
    /// <summary>topRight · topLeft · bottomRight · bottomLeft.</summary>
    public string Corner   { get; set; } = "topRight";
    /// <summary>Dot radius = sqrt(area) · DotScale (× UiScale), clamped to [DotMin, DotMax].</summary>
    public float  DotScale { get; set; } = 0.02f;
    public float  DotMin   { get; set; } = 1.2f;
    public float  DotMax   { get; set; } = 5f;
    /// <summary>Asteroids with total area below this get no dot — keeps the map legible.</summary>
    public float  MinArea  { get; set; } = 4000f;
    public float  BgAlpha  { get; set; } = 110f;
}

public class WorldConfig
{
    /// <summary>Playable field size. The camera clamps to it and the border hazard encloses it.</summary>
    public int   Width             { get; set; } = 5760;
    public int   Height            { get; set; } = 3240;
    public float CameraFollowSpeed { get; set; } = 4f;
    /// <summary>Width (px) of the spawn ring OUTSIDE the playable field. Wave bodies spawn there and
    /// drift in — guaranteed off-screen (the camera never sees past the playable bounds), so waves
    /// can enter from the player's own side even when they're parked in a corner.</summary>
    public float SpawnMargin       { get; set; } = 500f;
}

/// <summary>
/// The map-border rim: keeps bodies in, shoves campers off the walls, and — past a grace
/// period — erodes whatever lingers there. Anti-camping enforcement (pairs with the vortex pull).
/// </summary>
public class BorderHazardConfig
{
    public bool  Enabled      { get; set; } = true;

    // ── Damp: cancel outward velocity near an edge so nothing leaves the map (was hard-coded) ──
    /// <summary>Distance (px) from an edge within which outward velocity is damped.</summary>
    public float DampZone     { get; set; } = 200f;
    /// <summary>Exponential damping rate applied to outward velocity in the damp zone.</summary>
    public float DampStrength { get; set; } = 20f;

    // ── Push: an inward shove that grows toward the edge (nudges campers inward) ──
    /// <summary>Distance (px) from an edge within which the inward push applies.</summary>
    public float PushZone     { get; set; } = 420f;
    /// <summary>Inward acceleration (px/s²) at the very edge, fading linearly to 0 at PushZone.</summary>
    public float PushStrength { get; set; } = 1200f;

    // ── Erosion: after grace, the storm rips the most-exposed cell every Tick, ramping over time ──
    /// <summary>Distance (px) from an edge within which erosion exposure accumulates.</summary>
    public float HazardZone   { get; set; } = 340f;
    /// <summary>Seconds a body may sit in the hazard rim before erosion begins.</summary>
    public float Grace        { get; set; } = 2.0f;
    /// <summary>Seconds between erosion hits once past the grace period.</summary>
    public float Tick         { get; set; } = 0.4f;
    /// <summary>Synthetic impactor mass of the first erosion hit (fracture energy scales with it).</summary>
    public float BaseMass     { get; set; } = 8f;
    /// <summary>Impactor-mass growth per extra second camped past grace (energy ramp).</summary>
    public float Ramp         { get; set; } = 0.5f;
    /// <summary>Normal speed of the synthetic erosion impact (with BaseMass sets the base energy).</summary>
    public float ImpactSpeed  { get; set; } = 1000f;
    /// <summary>Exposure recovery rate (× dt) once a body leaves the hazard rim.</summary>
    public float DecayRate    { get; set; } = 0.5f;

    // ── Visuals: the primary cue is a red tint that deepens as the PLAYER goes into the rim; a
    //    subtle heat-haze warp on the on-screen world edges is the secondary, thematic cue. ──
    /// <summary>Full-screen red tint alpha (0..255) at the very edge, fading to 0 at the rim boundary.</summary>
    public float TintMaxAlpha { get; set; } = 120f;
    /// <summary>Heat-haze shimmer on the world edges when they are on-screen. 0 disables it.</summary>
    public bool  WarpEnabled  { get; set; } = true;
    /// <summary>Peak horizontal/vertical ripple (px) at the very edge, fading inward across HazardZone.</summary>
    public float WarpStrength { get; set; } = 7f;
    /// <summary>Ripple spatial frequency (radians per px along the edge).</summary>
    public float WarpFreq     { get; set; } = 0.03f;
    /// <summary>Ripple scroll speed (radians/s).</summary>
    public float WarpSpeed    { get; set; } = 2.2f;
    public int   WarpGrid     { get; set; } = 24;
}

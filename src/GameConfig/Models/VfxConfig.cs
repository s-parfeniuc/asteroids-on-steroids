namespace AsteroidsGame.Config;

public class VfxConfig
{
    // ── Dust burst (vaporised cells / tiny shards) ────────────────────────────
    public float DustCount   { get; set; } = 14f;
    public float DustSize    { get; set; } = 2.6f;
    public float DustTtl     { get; set; } = 0.70f;
    public float DustSpeed   { get; set; } = 60f;
    /// <summary>Cone half-angle as a fraction of π.</summary>
    public float DustSpread  { get; set; } = 0.50f;

    // ── Impact flash ──────────────────────────────────────────────────────────
    public float FlashSize   { get; set; } = 22f;
    public float FlashTtl    { get; set; } = 0.12f;

    // ── Bullet tracer ─────────────────────────────────────────────────────────
    public float TracerLength { get; set; } = 26f;
    public float TracerWidth  { get; set; } = 2f;

    // ── Debris polygon chunks (shed by vaporised cells) ───────────────────────
    public float DebrisTtl     { get; set; } = 0.80f;
    public float DebrisScatter { get; set; } = 40f;

    // ── Impact sparks (bright fast cone back along the hit) ────────────────────
    public float SparkCount  { get; set; } = 9f;     // at reference energy; scales with impact energy
    public float SparkSpeed  { get; set; } = 430f;
    public float SparkTtl    { get; set; } = 0.20f;
    public float SparkSize   { get; set; } = 2.0f;
    public float SparkSpread { get; set; } = 0.42f;  // cone half-angle as a fraction of π

    // ── Hitstop (brief freeze on big events) ──────────────────────────────────
    public float HitstopPlayerHit   { get; set; } = 0.055f; // player cell lost
    public float HitstopGrenade     { get; set; } = 0.025f;
    public float HitstopBigFracture { get; set; } = 0.015f; // a single pulverize whose area ≥ HitstopBigArea
    public float HitstopBigArea     { get; set; } = 1800f;
    public float HitstopMax         { get; set; } = 0.06f;  // cap on any single freeze

    // ── Floating score popups (aggregated, not per-hit) ───────────────────────
    public float PopupMinValue    { get; set; } = 50f;   // accumulate until ≥ this, then emit
    public float PopupFlushWindow { get; set; } = 0.35f; // s of idle before flushing a partial accumulator
    public float PopupTtl         { get; set; } = 0.85f; // at PopupRefValue; scales with value
    public float PopupRiseSpeed   { get; set; } = 46f;   // px/s upward drift
    public float PopupMinSize     { get; set; } = 13f;
    public float PopupMaxSize     { get; set; } = 30f;
    public float PopupRefValue    { get; set; } = 600f;  // value mapping to ~PopupMaxSize / longer ttl
}

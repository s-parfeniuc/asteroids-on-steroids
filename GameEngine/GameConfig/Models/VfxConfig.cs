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
}

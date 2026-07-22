using System;
using AsteroidsEngine.Engine.Destruction;
using AsteroidsEngine.Engine.Rendering;
using AsteroidsGame.Components;

namespace AsteroidsGame.Gameplay;

/// <summary>
/// Computes per-cell fill colours from a body's base colour plus each cell's role and density:
///   • base shading — denser cells (cluster cores, armour) render darker, and a rotation-invariant
///     rim light lifts the outer cells for volume;
///   • that base is BLENDED across bonded neighbours (a few Laplacian passes) so the individual cell
///     boundaries aren't obvious — the body reads as a smooth gradient, not visible tiles;
///   • ship cell roles (cockpit / propeller / weapon / spawner) get an accent tint applied AFTER the
///     blend, so those cells stay crisp and readable; asteroid cells (no role) are left smooth.
/// Baked once at spawn (and per fragment), so per-frame rendering just reads the array.
/// </summary>
public static class CellColorizer
{
    private const float DensityDarken = 0.58f;  // max darkening at high DensityMult
    private const float RimLight      = 0.16f;  // max lightening at the rim
    private const int   SmoothPasses  = 7;       // neighbour-blend passes (0 = crisp tiles)
    private const float SmoothBlend   = 0.55f;   // per-pass pull toward the neighbour average

    private static readonly Color White = new(255, 255, 255);

    /// <summary>Bakes each cell's FillColor in place. Called once for a freshly-built body; fragments
    /// inherit their cells' colours through fracture, so this never re-runs on them.</summary>
    public static void Apply(in FracturableBody body, in BodyColor baseColor)
    {
        var cells = body.Cells;
        int n = cells.Length;
        if (n == 0) return;

        float maxR = 1f;
        for (int i = 0; i < n; i++)
        {
            float d = cells[i].Centroid.Length();
            if (d > maxR) maxR = d;
        }

        // 1. Base shading (density darken + rim light), no role tint yet.
        var baseCol = new Color[n];
        for (int i = 0; i < n; i++)
        {
            Color c = baseColor.Fill;
            float dens   = Math.Clamp(cells[i].DensityMult, 0.6f, 3f);
            float darken = Math.Clamp((dens - 1f) * 0.5f, 0f, 1f) * DensityDarken;
            c = Scale(c, 1f - darken);
            float rim = Math.Clamp(cells[i].Centroid.Length() / maxR, 0f, 1f);
            baseCol[i] = Lerp(c, White, rim * RimLight);
        }

        // 2. Blend the base across bonded neighbours so cell borders aren't obvious.
        Smooth(baseCol, body.Bonds, n);

        // 3. Role accents on top (ships stay readable; asteroids have no matching role).
        byte a = baseColor.Fill.A == 0 ? (byte)255 : baseColor.Fill.A;   // alpha 0 means "not baked"
        for (int i = 0; i < n; i++)
        {
            Color c = baseCol[i];
            if (RoleAccent(cells[i].Role, out Color accent, out float amt))
                c = Lerp(c, accent, amt);
            cells[i].FillColor = new Color(c.R, c.G, c.B, a);
        }
    }

    // Jacobi Laplacian smoothing over the bond graph: each pass pulls a cell toward the average of
    // its bonded neighbours. Preserves the low-frequency gradient (dark core → lit rim) while erasing
    // the per-cell steps at shared edges.
    private static void Smooth(Color[] col, Bond[] bonds, int n)
    {
        if (SmoothPasses <= 0 || bonds.Length == 0) return;
        var sr = new float[n]; var sg = new float[n]; var sb = new float[n]; var cnt = new int[n];
        for (int p = 0; p < SmoothPasses; p++)
        {
            Array.Clear(sr); Array.Clear(sg); Array.Clear(sb); Array.Clear(cnt);
            foreach (var bd in bonds)
            {
                sr[bd.A] += col[bd.B].R; sg[bd.A] += col[bd.B].G; sb[bd.A] += col[bd.B].B; cnt[bd.A]++;
                sr[bd.B] += col[bd.A].R; sg[bd.B] += col[bd.A].G; sb[bd.B] += col[bd.A].B; cnt[bd.B]++;
            }
            for (int i = 0; i < n; i++)
            {
                if (cnt[i] == 0) continue;
                var avg = new Color((byte)(sr[i] / cnt[i]), (byte)(sg[i] / cnt[i]), (byte)(sb[i] / cnt[i]));
                col[i] = Lerp(col[i], avg, SmoothBlend);
            }
        }
    }

    private static bool RoleAccent(string? role, out Color accent, out float amt)
    {
        switch (role)
        {
            case "cockpit":   accent = new Color(210, 240, 255); amt = 0.50f; return true;  // bright glow
            case "propeller": accent = new Color(28, 28, 38);    amt = 0.45f; return true;  // dark engine
            case "cannon":    accent = new Color(240, 210, 70);  amt = 0.55f; return true;
            case "shotgun":   accent = new Color(240, 150, 60);  amt = 0.55f; return true;
            case "piercing":  accent = new Color(235, 235, 245); amt = 0.55f; return true;
            case "grenade":   accent = new Color(120, 220, 110); amt = 0.55f; return true;
            case "spawner":   accent = new Color(220, 90, 210);  amt = 0.50f; return true;
            case "skill":     accent = new Color(90, 220, 235);  amt = 0.55f; return true;  // cyan power cells
            default:          accent = default; amt = 0f; return false;
        }
    }

    private static Color Lerp(Color a, Color b, float t) => new(
        (byte)(a.R + (b.R - a.R) * t),
        (byte)(a.G + (b.G - a.G) * t),
        (byte)(a.B + (b.B - a.B) * t), a.A);

    private static Color Scale(Color c, float k) => new(
        (byte)Math.Clamp(c.R * k, 0f, 255f),
        (byte)Math.Clamp(c.G * k, 0f, 255f),
        (byte)Math.Clamp(c.B * k, 0f, 255f), c.A);
}

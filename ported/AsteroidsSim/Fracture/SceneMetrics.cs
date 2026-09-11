using AsteroidsSim.Math;

namespace AsteroidsSim.Fracture;

/// <summary>
/// The measurable character of a fracture outcome — what the JavaScript reference's headless suite
/// reports, in the same terms.
/// </summary>
/// <remarks>
/// The conservation invariants catch bugs; these catch <i>feel changes</i>. An optimisation that
/// leaves momentum and energy perfect can still turn a body that used to split into big chunks into
/// one that turns to gravel, and only morphology shows that. Every performance change is expected
/// to move these slightly and none of them materially.
/// </remarks>
public readonly struct SceneMetrics
{
    /// <summary>Live bodies at the end of the run.</summary>
    public readonly int Bodies;
    /// <summary>Bonds that separated.</summary>
    public readonly int Broken;
    /// <summary>Cells converted to debris by the comminution classifier.</summary>
    public readonly int Dust;
    /// <summary>Plastic rest rebakes performed.</summary>
    public readonly int Rebakes;

    /// <summary>
    /// Percentage of separated bonds that share a cell with another separated bond — i.e. how much
    /// of the damage formed connected cracks rather than scattering. Low values mean the model is
    /// dissolving regions into confetti instead of cracking them.
    /// </summary>
    public readonly float CrackConnectivity;

    /// <summary>Fraction of live mass in single-cell fragments.</summary>
    public readonly float SinglesMassPct;
    /// <summary>Fraction of live mass in fragments larger than four cells.</summary>
    public readonly float BigMassPct;

    /// <summary>Peak bond stretch seen during the run, as a percentage of a cell.</summary>
    public readonly float PeakStretchPct;

    /// <summary>Momentum drift as a fraction of the scene's momentum scale.</summary>
    public readonly float MomentumDrift;
    /// <summary>Final kinetic energy as a fraction of initial.</summary>
    public readonly float EnergyFraction;
    /// <summary>Peak kinetic energy as a fraction of initial. Above 1 means energy was created.</summary>
    public readonly float PeakEnergyFraction;
    /// <summary>
    /// Worst collider vertex distance from its own cell centre, in cell radii. About 1 by
    /// construction now; anything larger means geometry is being read from the wrong place.
    /// </summary>
    public readonly float MaxSharedVertexGap;
    /// <summary>Deepest overlap seen during the run.</summary>
    public readonly float PeakOverlap;

    public SceneMetrics(int bodies, int broken, int dust, int rebakes, float crackConnectivity,
        float singlesMassPct, float bigMassPct, float peakStretchPct,
        float momentumDrift, float energyFraction, float peakEnergyFraction,
        float maxSharedVertexGap, float peakOverlap)
    {
        Bodies = bodies; Broken = broken; Dust = dust; Rebakes = rebakes;
        CrackConnectivity = crackConnectivity;
        SinglesMassPct = singlesMassPct; BigMassPct = bigMassPct;
        PeakStretchPct = peakStretchPct;
        MomentumDrift = momentumDrift; EnergyFraction = energyFraction;
        PeakEnergyFraction = peakEnergyFraction;
        MaxSharedVertexGap = maxSharedVertexGap; PeakOverlap = peakOverlap;
    }

    public override string ToString()
        => $"bodies={Bodies} broken={Broken} dust={Dust} rb={Rebakes} conn={CrackConnectivity:F0}% "
         + $"sing={SinglesMassPct:F0}% big={BigMassPct:F0}% str={PeakStretchPct:F1}% "
         + $"mom={100 * MomentumDrift:F3}% "
         + $"ke={100 * EnergyFraction:F0}/{100 * PeakEnergyFraction:F0}% "
         + $"vtx={MaxSharedVertexGap:F2} ov={PeakOverlap:F1}";
}

/// <summary>Runs a scenario and collects <see cref="SceneMetrics"/>.</summary>
public static class SceneRunner
{
    public static SceneMetrics Run(in Scenarios.Result r, int ticks, float cellSize = 30f)
    {
        float peakKe = 0f, peakOverlap = 0f, peakStretch = 0f, worstGap = 0f;

        for (int i = 0; i < ticks; i++)
        {
            r.Solver.Step();

            float ke = r.EnergyFraction();
            if (ke > peakKe) peakKe = ke;
            if (r.Solver.MaxOverlap > peakOverlap) peakOverlap = r.Solver.MaxOverlap;

            SimState s = r.State;
            for (int k = 0; k < s.BondCount; k++)
            {
                if (s.BondBroken[k]) continue;
                float st = SimMath.Hypot(s.BondSn[k], s.BondSt[k])
                           + SimMath.Abs(s.BondSa[k]) * s.BondLen[k] * 0.5f;
                if (st > peakStretch) peakStretch = st;
            }

            if (i % 10 == 0)
            {
                // Collider vertices must sit on the cells they belong to. With deformation gone this
                // is close to an identity, but it still catches a transform reading a stale pose.
                float g = r.Solver.MaxSkinRadiusRatio();
                if (g > worstGap) worstGap = g;
            }
        }

        SimState st2 = r.State;

        // fragment morphology by mass
        float mTot = 0f, mSingles = 0f, mBig = 0f;
        for (int b = 0; b < st2.BodyCount; b++)
        {
            int n = 0; float m = 0f;
            int off = st2.BodyCellOff[b], len = st2.BodyCellLen[b];
            for (int i = 0; i < len; i++)
            {
                int c = st2.BodyCells[off + i];
                if (st2.Dead(c)) continue;
                n++; m += st2.CellM[c];
            }
            if (n == 0) continue;
            mTot += m;
            if (n == 1) mSingles += m;
            if (n > 4) mBig += m;
        }
        if (mTot < 1e-9f) mTot = 1e-9f;

        // crack connectivity: separated bonds that continue another separated bond
        int nb = 0, conn = 0;
        var incident = new int[st2.CellCount];
        for (int k = 0; k < st2.BondCount; k++)
        {
            if (!st2.BondBroken[k] || st2.BondMode[k] == 0) continue;
            nb++; incident[st2.BondA[k]]++; incident[st2.BondB[k]]++;
        }
        for (int k = 0; k < st2.BondCount; k++)
        {
            if (!st2.BondBroken[k] || st2.BondMode[k] == 0) continue;
            if (incident[st2.BondA[k]] > 1 || incident[st2.BondB[k]] > 1) conn++;
        }

        return new SceneMetrics(
            bodies: st2.BodyCount,
            broken: r.Solver.Broken,
            dust: r.Solver.Dust,
            rebakes: r.Solver.Rebakes,
            crackConnectivity: nb > 0 ? 100f * conn / nb : 0f,
            singlesMassPct: 100f * mSingles / mTot,
            bigMassPct: 100f * mBig / mTot,
            peakStretchPct: 100f * peakStretch / cellSize,
            momentumDrift: r.MomentumDrift(),
            energyFraction: r.EnergyFraction(),
            peakEnergyFraction: peakKe,
            maxSharedVertexGap: worstGap,
            peakOverlap: peakOverlap);
    }
}

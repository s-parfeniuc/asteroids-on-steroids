using System.Numerics;
using AsteroidsEngine.Engine.Rendering;

namespace AsteroidsGame.States;

public sealed class WaveCompleteState : IGameState
{
    private readonly GameContext _ctx;
    private readonly int         _completedWave;
    private float                _timer;
    private const float          DisplayTime = 3.0f;

    private static readonly FontSpec Big  = new("monospace", 48f);
    private static readonly FontSpec Med  = new("monospace", 22f);
    private static readonly Color    Bg   = new(8, 9, 14);

    public WaveCompleteState(GameContext ctx, int completedWave)
    {
        _ctx = ctx;
        _completedWave = completedWave;
    }

    public void Enter()
    {
        _timer = DisplayTime;
        _ctx.Score.AddBonus(_ctx.Config.Scoring.WaveSurvivalBonus);
    }

    public void Exit() { }

    public IGameState? Update(double dt)
    {
        _timer -= (float)dt;
        if (_timer > 0f) return null;

        int nextWave = _completedWave + 1;

        // Check win condition (last wave was the boss)
        bool justFinishedBoss = _ctx.Config.Waves
            .Any(w => w.Wave == _completedWave && w.Boss);
        if (justFinishedBoss) return new GameOverState(_ctx, won: true);

        return new PlayingState(_ctx, nextWave);
    }

    public void Draw(IRenderer r, float alpha)
    {
        float cx = _ctx.ScreenW / 2f, cy = _ctx.ScreenH / 2f;
        r.Begin(Bg);
        r.DrawText($"WAVE {_completedWave} COMPLETE",
                   new Vector2(cx - 160f, cy - 40f), new Color(120, 220, 255), Big);
        r.DrawText($"Score  {_ctx.Score.Total:F0}",
                   new Vector2(cx - 80f, cy + 30f),  new Color(200, 210, 230), Med);
        r.End();
    }
}

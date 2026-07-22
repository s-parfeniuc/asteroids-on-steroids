using System;
using System.Numerics;
using AsteroidsEngine.Engine.Input;
using AsteroidsEngine.Engine.Rendering;

namespace AsteroidsGame.States;

public sealed class MainMenuState : IGameState
{
    private readonly GameContext _ctx;
    private static readonly FontSpec Title  = new("monospace", 48f);
    private static readonly FontSpec Sub    = new("monospace", 20f);
    private static readonly FontSpec Hint   = new("monospace", 14f);
    private static readonly Color    Bg     = new(8, 9, 14);
    private static readonly Color    TitleC = new(220, 235, 255);
    private static readonly Color    SubC   = new(120, 160, 210);
    private static readonly Color    HintC  = new(80, 100, 130);

    public MainMenuState(GameContext ctx) => _ctx = ctx;

    public void Enter()  { }
    public void Exit()   { }

    private static readonly float[] ShakeLevels = { 0f, 0.5f, 1f, 1.5f };

    public IGameState? Update(double dt)
    {
        var inp = _ctx.Input;
        int n = _ctx.Difficulties.Count;
        if (n > 1)
        {
            if (inp.ConsumePress(KeyCode.Right) || inp.ConsumePress(KeyCode.D))
            { _ctx.DifficultyIndex = (_ctx.DifficultyIndex + 1) % n; _ctx.Save(); }
            if (inp.ConsumePress(KeyCode.Left) || inp.ConsumePress(KeyCode.A))
            { _ctx.DifficultyIndex = (_ctx.DifficultyIndex + n - 1) % n; _ctx.Save(); }
        }
        if (inp.ConsumePress(KeyCode.Up) || inp.ConsumePress(KeyCode.W))   CycleShake(+1);
        if (inp.ConsumePress(KeyCode.Down) || inp.ConsumePress(KeyCode.S)) CycleShake(-1);

        if (inp.ConsumePress(KeyCode.Escape)) { _ctx.QuitRequested = true; return null; }
        if (inp.IsPressed(KeyCode.Space) || inp.IsPressed(KeyCode.Enter))
            return new PlayingState(_ctx);
        return null;
    }

    private void CycleShake(int dir)
    {
        int i = 0; float best = float.MaxValue;
        for (int k = 0; k < ShakeLevels.Length; k++)
        {
            float d = MathF.Abs(ShakeLevels[k] - _ctx.ShakeIntensity);
            if (d < best) { best = d; i = k; }
        }
        i = Math.Clamp(i + dir, 0, ShakeLevels.Length - 1);
        _ctx.ShakeIntensity = ShakeLevels[i];
        _ctx.Save();
    }

    public void Draw(IRenderer r, float alpha)
    {
        float cx = _ctx.ScreenW / 2f, cy = _ctx.ScreenH / 2f;
        r.Begin(Bg);
        r.DrawText("ASTEROIDS ON STEROIDS",     new Vector2(cx - 280f, cy - 80f), TitleC, Title);
        r.DrawText("Destruction Arcade",        new Vector2(cx - 120f, cy - 18f), SubC,   Sub);
        if (_ctx.HighScore > 0f)
            r.DrawText($"Best  {_ctx.HighScore:F0}", new Vector2(cx - 60f, cy + 20f), new Color(150, 165, 195), Hint);
        r.DrawText($"< {_ctx.Difficulty.Name} >",   new Vector2(cx - 70f, cy + 44f), new Color(230, 200, 120), Sub);
        r.DrawText($"Shake  {_ctx.ShakeIntensity * 100f:F0}%", new Vector2(cx - 62f, cy + 74f), new Color(150, 190, 170), Hint);
        r.DrawText("A/D difficulty   W/S shake",    new Vector2(cx - 118f, cy + 96f), HintC, Hint);
        r.DrawText("SPACE / ENTER start    ESC quit", new Vector2(cx - 140f, cy + 116f), HintC, Hint);
        r.End();
    }
}

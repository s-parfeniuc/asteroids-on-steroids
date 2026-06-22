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

    public IGameState? Update(double dt)
    {
        if (_ctx.Input.IsPressed(KeyCode.Space) || _ctx.Input.IsPressed(KeyCode.Enter))
            return new PlayingState(_ctx);
        return null;
    }

    public void Draw(IRenderer r, float alpha)
    {
        float cx = _ctx.ScreenW / 2f, cy = _ctx.ScreenH / 2f;
        r.Begin(Bg);
        r.DrawText("ASTEROIDS ON STEROIDS",     new Vector2(cx - 280f, cy - 80f), TitleC, Title);
        r.DrawText("Destruction Arcade",        new Vector2(cx - 120f, cy - 18f), SubC,   Sub);
        r.DrawText("SPACE / ENTER to start",    new Vector2(cx - 110f, cy + 60f), HintC,  Hint);
        r.DrawText("ESC to quit",               new Vector2(cx - 50f,  cy + 82f), HintC,  Hint);
        r.End();
    }
}

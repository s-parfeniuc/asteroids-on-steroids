using System.Numerics;
using AsteroidsEngine.Engine.Input;
using AsteroidsEngine.Engine.Rendering;

namespace AsteroidsGame.States;

public sealed class GameOverState : IGameState
{
    private readonly GameContext _ctx;
    private readonly bool        _won;
    private static readonly FontSpec Big  = new("monospace", 48f);
    private static readonly FontSpec Med  = new("monospace", 22f);
    private static readonly FontSpec Hint = new("monospace", 14f);
    private static readonly Color    Bg   = new(8, 9, 14);

    public GameOverState(GameContext ctx, bool won) { _ctx = ctx; _won = won; }

    public void Enter()  { }
    public void Exit()   { }

    public IGameState? Update(double dt)
    {
        if (_ctx.Input.IsPressed(KeyCode.Space) || _ctx.Input.IsPressed(KeyCode.Enter))
        {
            _ctx.Score.Reset();
            _ctx.CellBudget.Reset();
            return new MainMenuState(_ctx);
        }
        return null;
    }

    public void Draw(IRenderer r, float alpha)
    {
        float cx = _ctx.ScreenW / 2f, cy = _ctx.ScreenH / 2f;
        r.Begin(Bg);

        string headline = _won ? "YOU WIN" : "GAME OVER";
        Color headlineC = _won ? new Color(120, 255, 160) : new Color(255, 100, 90);
        r.DrawText(headline,                              new Vector2(cx - 120f, cy - 80f), headlineC, Big);
        r.DrawText($"Score  {_ctx.Score.Total:F0}",      new Vector2(cx - 80f,  cy - 10f), new Color(220, 230, 255), Med);
        r.DrawText("SPACE / ENTER for main menu",        new Vector2(cx - 145f, cy + 60f), new Color(80, 100, 130), Hint);
        r.End();
    }
}

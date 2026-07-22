using System.Linq.Expressions;
using AsteroidsEngine.Engine.Rendering;

namespace AsteroidsEngine.Engine.Ui;

/// <summary>
/// Declarative per-field editor binding for a config class.
/// Build once at startup; call <see cref="Draw"/> every frame.
///
/// Example:
///   static readonly EditorDef&lt;MaterialConfig&gt; MatDef = new EditorDef&lt;MaterialConfig&gt;()
///       .Slider(m =&gt; m.Toughness, "Toughness", 0.5f, 40f)
///       .Separator()
///       .Slider(m =&gt; m.Density,   "Density",   0.2f, 5f);
///
///   // per frame:
///   if (MatDef.Draw(myMaterial, ui)) Save();
/// </summary>
public sealed class EditorDef<T> where T : class
{
    // Each entry draws one or more widgets and returns true when any value changes.
    private readonly List<Func<T, Ui, bool>> _entries = [];

    // ── Builder ───────────────────────────────────────────────────────────────

    /// <summary>Float slider bound to a property via expression tree.</summary>
    public EditorDef<T> Slider(Expression<Func<T, float>> prop, string label,
                                float min, float max, string? fmt = null)
    {
        var (get, set) = Bind<float>(prop);
        _entries.Add((obj, ui) =>
        {
            float v = get(obj);
            if (!ui.Slider(label, ref v, min, max, fmt)) return false;
            set(obj, v);
            return true;
        });
        return this;
    }

    /// <summary>Integer slider bound to a property via expression tree.</summary>
    public EditorDef<T> SliderInt(Expression<Func<T, int>> prop, string label, int min, int max)
    {
        var (get, set) = Bind<int>(prop);
        _entries.Add((obj, ui) =>
        {
            int v = get(obj);
            if (!ui.SliderInt(label, ref v, min, max)) return false;
            set(obj, v);
            return true;
        });
        return this;
    }

    /// <summary>
    /// Float slider with explicit getter/setter lambdas.
    /// Use when the property is not a simple member (e.g. array elements, computed fields).
    /// </summary>
    public EditorDef<T> Slider(string label, Func<T, float> getter, Action<T, float> setter,
                                float min, float max, string? fmt = null)
    {
        _entries.Add((obj, ui) =>
        {
            float v = getter(obj);
            if (!ui.Slider(label, ref v, min, max, fmt)) return false;
            setter(obj, v);
            return true;
        });
        return this;
    }

    /// <summary>
    /// Integer slider with explicit getter/setter lambdas.
    /// Use when the target is not a simple property (e.g. array elements).
    /// </summary>
    public EditorDef<T> SliderInt(string label, Func<T, int> getter, Action<T, int> setter,
                                   int min, int max)
    {
        _entries.Add((obj, ui) =>
        {
            int v = getter(obj);
            if (!ui.SliderInt(label, ref v, min, max)) return false;
            setter(obj, v);
            return true;
        });
        return this;
    }

    /// <summary>Checkbox bound to a boolean property via expression tree.</summary>
    public EditorDef<T> Toggle(Expression<Func<T, bool>> prop, string label)
    {
        var (get, set) = Bind<bool>(prop);
        _entries.Add((obj, ui) =>
        {
            bool v = get(obj);
            if (!ui.Toggle(label, ref v)) return false;
            set(obj, v);
            return true;
        });
        return this;
    }

    /// <summary>Static text label (no data binding).</summary>
    public EditorDef<T> Label(string text, Color? color = null)
    {
        _entries.Add((_, ui) => { ui.Label(text, color); return false; });
        return this;
    }

    /// <summary>Horizontal separator line.</summary>
    public EditorDef<T> Separator()
    {
        _entries.Add((_, ui) => { ui.Separator(); return false; });
        return this;
    }

    /// <summary>Vertical gap.</summary>
    public EditorDef<T> Space(float h = 6f)
    {
        _entries.Add((_, ui) => { ui.Space(h); return false; });
        return this;
    }

    // ── Render ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Renders all bound widgets for <paramref name="obj"/> using <paramref name="ui"/>.
    /// Must be called inside a <see cref="Ui.BeginPanel"/> / <see cref="Ui.EndPanel"/> block.
    /// Returns <c>true</c> if any field value changed this frame.
    /// </summary>
    public bool Draw(T obj, Ui ui)
    {
        bool changed = false;
        foreach (var e in _entries) changed |= e(obj, ui);
        return changed;
    }

    // ── Expression-tree compilation ───────────────────────────────────────────

    // Compiles a property expression into a typed getter and setter pair.
    // Called once at startup when the EditorDef is built.
    private static (Func<T, TV> get, Action<T, TV> set) Bind<TV>(Expression<Func<T, TV>> expr)
    {
        var get = expr.Compile();

        var memberExpr = (MemberExpression)expr.Body;
        var tParam = Expression.Parameter(typeof(T));
        var vParam = Expression.Parameter(typeof(TV));
        var prop   = Expression.MakeMemberAccess(tParam, memberExpr.Member);
        var assign = Expression.Assign(prop, vParam);
        var set    = Expression.Lambda<Action<T, TV>>(assign, tParam, vParam).Compile();

        return (get, set);
    }
}

using System.Numerics;
using AsteroidsEngine.Engine.Input;
using AsteroidsEngine.Engine.Rendering;

namespace AsteroidsEngine.Engine.Ui;

/// <summary>
/// Minimal immediate-mode 2D UI.
///
/// Usage per frame:
///   ui.BeginFrame(renderer, input);
///   ui.BeginPanel(x, y, w, h, "Title");
///   if (ui.Slider("Toughness", ref mat.Toughness, 0.1f, 20f)) matChanged = true;
///   ui.EndPanel();
///   ui.EndFrame();
///
/// Coordinates: top-left origin, Y-down screen space (matches IRenderer).
/// </summary>
public sealed class Ui
{
    // ── Theme ─────────────────────────────────────────────────────────────────────
    public static readonly Color ColBg         = new(15,  16,  22);
    public static readonly Color ColPanel       = new(25,  28,  38);
    public static readonly Color ColBorder      = new(50,  58,  78);
    public static readonly Color ColHeader      = new(35,  40,  56);
    public static readonly Color ColHeaderHot   = new(45,  52,  72);
    public static readonly Color ColText        = new(200, 210, 230);
    public static readonly Color ColTextDim     = new(110, 125, 150);
    public static readonly Color ColAccent      = new(75,  135, 235);
    public static readonly Color ColAccentDim   = new(50,  90,  165);
    public static readonly Color ColSliderBg    = new(40,  45,  62);
    public static readonly Color ColSliderFill  = new(58,  100, 180);
    public static readonly Color ColSliderHot   = new(78,  130, 215);
    public static readonly Color ColBtnBg       = new(42,  50,  70);
    public static readonly Color ColBtnHot      = new(58,  70,  100);
    public static readonly Color ColBtnPress    = new(80,  120, 195);
    public static readonly Color ColSelBg       = new(50,  80,  150, 70);
    public static readonly Color ColSep         = new(50,  58,  78);
    public static readonly Color ColDanger      = new(195, 60,  60);
    public static readonly Color ColDangerHot   = new(220, 85,  85);

    public static readonly FontSpec FontNormal = new("monospace", 13f);
    public static readonly FontSpec FontSmall  = new("monospace", 11f);
    public static readonly FontSpec FontBold   = new("monospace", 13f, bold: true);
    public static readonly FontSpec FontTitle  = new("monospace", 14f, bold: true);

    // ── Layout constants ──────────────────────────────────────────────────────────
    public const float RowH   = 24f;
    public const float Pad    = 8f;
    public const float LabelW = 148f; // label column in Slider / Toggle
    public const float ValW   = 56f;  // value column in Slider

    // ── Per-frame input ───────────────────────────────────────────────────────────
    private IRenderer _r = null!;
    private Vector2   _mouse;
    private bool      _mouseDown;
    private bool      _mousePressed;   // true on the first frame the button goes down
    private bool      _mouseReleased;  // true on the first frame the button comes up
    private bool      _prevMouseDown;

    // ── Per-frame keyboard / text state ──────────────────────────────────────────
    private string _textInput  = "";
    private bool   _backspace;
    private bool   _enterKey;
    private bool   _escapeKey;

    // ── Widget state ──────────────────────────────────────────────────────────────
    private ulong  _activeId;        // slider being dragged
    private ulong  _focusedInputId;  // text input with keyboard focus
    private string _inputBuffer = "";
    private int    _cursorPos;

    // ── Panel context ─────────────────────────────────────────────────────────────
    private float _px, _py, _pw, _ph;
    private float _cy; // layout cursor

    // ── Row layout ────────────────────────────────────────────────────────────────
    private bool  _inRow;
    private float _rowBaseY;
    private float _rowCurX;
    private float _rowItemW;
    private float _rowGap;

    // ─────────────────────────────────────────────────────────────────────────────
    // Properties
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Current layout cursor Y — lets callers measure consumed height.</summary>
    public float CursorY => _cy;

    // ─────────────────────────────────────────────────────────────────────────────
    // Frame lifecycle
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>True when any text-input widget is active (suppresses game hotkeys).</summary>
    public bool IsTextInputActive => _focusedInputId != 0;

    public void BeginFrame(IRenderer r, InputSystem input)
    {
        _r     = r;
        _mouse = input.MouseScreen;
        // Use the raw mouse state so UI widgets always respond, even when
        // SuppressMouseLeft is set to block gameplay shooting.
        bool now       = input.IsMouseLeftRaw;
        _mousePressed  = now  && !_prevMouseDown;
        _mouseReleased = !now && _prevMouseDown;
        _mouseDown     = now;
        _prevMouseDown = now;
        if (_mouseReleased) _activeId = 0;

        _textInput  = input.TextThisFrame;
        _backspace  = input.IsPressed(KeyCode.Back);
        _enterKey   = input.IsPressed(KeyCode.Enter);
        _escapeKey  = input.IsPressed(KeyCode.Escape);
    }

    public void EndFrame() { } // reserved

    // ─────────────────────────────────────────────────────────────────────────────
    // Panel
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Opens a panel.  Draws a filled background + border.
    /// If <paramref name="title"/> is provided, reserves a header row at the top.
    /// </summary>
    public void BeginPanel(float x, float y, float w, float h, string? title = null)
    {
        _px = x; _py = y; _pw = w; _ph = h;
        _inRow = false;

        FillRect(x, y, w, h, ColPanel);
        DrawRect(x, y, w, h, ColBorder, 1f);

        if (title is not null)
        {
            FillRect(x, y, w, RowH + 4f, ColHeader);
            _r.DrawText(title, new Vector2(x + Pad, y + 4f), ColText, FontTitle);
            _cy = y + RowH + 8f;
        }
        else
        {
            _cy = y + Pad;
        }
    }

    public void EndPanel() { _inRow = false; }

    // ─────────────────────────────────────────────────────────────────────────────
    // Row layout  (horizontal strip of equal-width items)
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Begin a horizontal row of <paramref name="count"/> equal-width cells.
    /// Only <see cref="Button"/> respects row mode; all other widgets ignore it.
    /// </summary>
    public void BeginRow(int count, float gap = 3f)
    {
        _inRow    = true;
        _rowBaseY = _cy;
        _rowCurX  = _px + Pad;
        _rowGap   = gap;
        _rowItemW = (_pw - 2f * Pad - gap * (count - 1)) / count;
    }

    /// <summary>End the row and advance the cursor past it.</summary>
    public void EndRow()
    {
        _inRow = false;
        _cy    = _rowBaseY + RowH + 2f;
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Widgets
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Advance the cursor by <paramref name="h"/> pixels without drawing.</summary>
    public void Space(float h = 6f) => _cy += h;

    /// <summary>Left-aligned text label.</summary>
    public void Label(string text, Color? color = null, FontSpec? font = null)
    {
        var f = font ?? FontNormal;
        _r.DrawText(text, new Vector2(_px + Pad, _cy + 3f), color ?? ColText, f);
        _cy += RowH;
    }

    /// <summary>Horizontal divider line.</summary>
    public void Separator()
    {
        float y = _cy + 6f;
        _r.DrawLine(new Vector2(_px + Pad, y), new Vector2(_px + _pw - Pad, y), ColSep, 1f);
        _cy += 14f;
    }

    /// <summary>
    /// Collapsible section header.  Clicking toggles <paramref name="open"/>.
    /// Returns the current <paramref name="open"/> state.
    /// </summary>
    public bool Header(string label, ref bool open)
    {
        float x = _px, y = _cy, w = _pw;
        bool hot = Hit(x, y, w, RowH);
        FillRect(x, y, w, RowH, hot ? ColHeaderHot : ColHeader);
        _r.DrawText((open ? "▼  " : "►  ") + label,
                    new Vector2(x + Pad, y + 4f), ColText, FontBold);
        _cy += RowH + 2f;
        if (hot && _mousePressed) open = !open;
        return open;
    }

    /// <summary>
    /// Button.  Inside a <see cref="BeginRow"/> / <see cref="EndRow"/> block: takes the
    /// next row cell.  Otherwise: full panel width minus padding.
    /// </summary>
    /// <param name="col">Override background colour (e.g. <see cref="ColDanger"/>).</param>
    public bool Button(string label, Color? col = null)
    {
        GetItemRect(out float x, out float y, out float w, out float h);
        bool hot     = Hit(x, y, w, h);
        bool pressed = hot && _mousePressed;
        Color bg = pressed ? ColBtnPress
                 : hot     ? ColBtnHot
                 :           (col ?? ColBtnBg);
        FillRect(x, y, w, h, bg);
        DrawRect(x, y, w, h, ColBorder, 1f);
        var sz = _r.MeasureText(label, FontNormal);
        _r.DrawText(label,
                    new Vector2(x + (w - sz.X) * 0.5f, y + (h - sz.Y) * 0.5f),
                    ColText, FontNormal);
        AdvanceItem();
        return pressed;
    }

    /// <summary>
    /// Float slider.  Returns <c>true</c> every frame the value changes.
    /// Click-drag on the track to scrub; click the number on the right to type a value directly.
    /// </summary>
    public bool Slider(string label, ref float value, float min, float max,
                       string? valueFmt = null)
    {
        ulong id      = IdFor(label);
        ulong inputId = id | 0x8000_0000_0000_0000UL; // high bit distinguishes text-input from drag IDs
        float lx = _px + Pad, ly = _cy;
        float tx = lx + LabelW;
        float tw = _pw - Pad * 2f - LabelW - ValW;
        float vx = tx + tw + 4f;
        float vw = ValW - 4f;

        _r.DrawText(label, new Vector2(lx, ly + 4f), ColText, FontNormal);

        bool inputFocused = _focusedInputId == inputId;
        bool changed      = false;

        // ── Value text input (right column, click to type) ────────────────────
        if (Hit(vx, ly, vw, RowH) && _mousePressed && !inputFocused)
        {
            _focusedInputId = inputId;
            _inputBuffer    = valueFmt is not null ? value.ToString(valueFmt) : AutoFmt(value, min, max);
            _cursorPos      = _inputBuffer.Length;
            inputFocused    = true;
        }

        if (inputFocused)
        {
            if (_textInput.Length > 0)
            {
                _inputBuffer = _inputBuffer[.._cursorPos] + _textInput + _inputBuffer[_cursorPos..];
                _cursorPos  += _textInput.Length;
            }
            if (_backspace && _cursorPos > 0)
            {
                _inputBuffer = _inputBuffer[..(_cursorPos - 1)] + _inputBuffer[_cursorPos..];
                _cursorPos--;
                _backspace = false;
            }
            if (_enterKey)
            {
                if (float.TryParse(_inputBuffer, System.Globalization.NumberStyles.Float,
                                   System.Globalization.CultureInfo.InvariantCulture, out float parsed))
                { value = Math.Clamp(parsed, min, max); changed = true; }
                _focusedInputId = 0;
                _enterKey       = false;
            }
            if (_escapeKey)
            {
                _focusedInputId = 0;
                _escapeKey      = false;
            }

            FillRect(vx, ly, vw, RowH, ColSliderBg);
            DrawRect(vx, ly, vw, RowH, ColAccent, 1f);
            _r.DrawText(_inputBuffer, new Vector2(vx + 4f, ly + 4f), ColText, FontNormal);
            var sz = _r.MeasureText(_inputBuffer[.._cursorPos], FontNormal);
            float cx = vx + 4f + sz.X;
            _r.DrawLine(new Vector2(cx, ly + 3f), new Vector2(cx, ly + RowH - 3f), ColAccent, 1.5f);
        }
        else
        {
            bool valHot = Hit(vx, ly, vw, RowH);
            string display = valueFmt is not null ? value.ToString(valueFmt) : AutoFmt(value, min, max);
            if (valHot) FillRect(vx, ly, vw, RowH, new Color(50, 55, 75, 80));
            _r.DrawText(display, new Vector2(vx + 4f, ly + 4f), valHot ? ColText : ColTextDim, FontNormal);
        }

        // ── Slider track (disabled while typing) ──────────────────────────────
        bool hot    = Hit(tx, ly, tw, RowH);
        bool active = _activeId == id;
        if (hot && _mousePressed && !inputFocused) { _activeId = id; active = true; }

        if (active && _mouseDown && !inputFocused)
        {
            float t    = Math.Clamp((_mouse.X - tx) / tw, 0f, 1f);
            float nval = min + t * (max - min);
            if (nval != value) { value = nval; changed = true; }
        }

        float fill = tw * Math.Clamp((value - min) / (max - min), 0f, 1f);
        float my   = ly + RowH * 0.5f;
        FillRect(tx, my - 4f, tw,   8f, ColSliderBg);
        if (fill > 0f)
            FillRect(tx, my - 4f, fill, 8f, active || hot ? ColSliderHot : ColSliderFill);
        FillRect(tx + fill - 5f, ly + 3f, 10f, RowH - 6f,
                 active ? ColAccent : hot ? ColSliderHot : ColSliderFill);

        _cy += RowH + 1f;
        return changed;
    }

    /// <summary>Integer slider.  Returns <c>true</c> when the integer value changes.</summary>
    public bool SliderInt(string label, ref int value, int min, int max)
    {
        float f = value;
        if (Slider(label, ref f, min, max, "0"))
        {
            int n = Math.Clamp((int)MathF.Round(f), min, max);
            if (n != value) { value = n; return true; }
        }
        return false;
    }

    /// <summary>Checkbox / toggle.  Returns <c>true</c> on the frame it is clicked.</summary>
    public bool Toggle(string label, ref bool value)
    {
        float lx = _px + Pad, ly = _cy;
        float bx = lx + LabelW, by = ly + 3f, bs = RowH - 6f;

        _r.DrawText(label, new Vector2(lx, ly + 4f), ColText, FontNormal);
        DrawRect(bx, by, bs, bs, ColSliderFill, 1f);
        if (value) FillRect(bx + 2f, by + 2f, bs - 4f, bs - 4f, ColAccent);

        bool clicked = Hit(bx, ly, bs + 24f, RowH) && _mousePressed;
        if (clicked) value = !value;
        _cy += RowH + 1f;
        return clicked;
    }

    /// <summary>
    /// Horizontal tab strip at an <em>explicit</em> screen rectangle (not in panel flow).
    /// Returns <c>true</c> when the selection changes.
    /// </summary>
    public bool Tabs(float x, float y, float w, float h,
                     ReadOnlySpan<string> labels, ref int selected)
    {
        float tw = w / labels.Length;
        bool changed = false;
        for (int i = 0; i < labels.Length; i++)
        {
            bool sel = i == selected;
            bool hot = Hit(x + i * tw, y, tw, h);
            FillRect(x + i * tw, y, tw - 1f, h,
                     sel ? ColAccentDim : hot ? ColBtnHot : ColBtnBg);
            if (sel) FillRect(x + i * tw, y + h - 3f, tw - 1f, 3f, ColAccent);

            var sz = _r.MeasureText(labels[i], FontNormal);
            _r.DrawText(labels[i],
                        new Vector2(x + i * tw + (tw - sz.X) * 0.5f,
                                    y + (h - sz.Y) * 0.5f),
                        sel || hot ? ColText : ColTextDim,
                        FontNormal);

            if (hot && _mousePressed && !sel) { selected = i; changed = true; }
        }
        return changed;
    }

    /// <summary>
    /// Clickable list item (full-panel-width row).  Returns <c>true</c> when clicked.
    /// </summary>
    public bool Selectable(string label, bool selected, Color? accent = null)
    {
        float x = _px, y = _cy, w = _pw;
        bool hot = Hit(x, y, w, RowH);
        if (selected) FillRect(x, y, w, RowH, ColSelBg);
        else if (hot)  FillRect(x, y, w, RowH, new Color(50, 55, 70, 30));
        _r.DrawText((selected ? "▶ " : "  ") + label,
                    new Vector2(x + Pad, y + 4f),
                    selected ? (accent ?? ColAccent) : ColText,
                    FontNormal);
        _cy += RowH;
        return hot && _mousePressed;
    }

    /// <summary>
    /// Editable single-line text box.
    /// Returns <c>true</c> when the user commits by pressing Enter.
    /// Escape cancels (defocuses without modifying <paramref name="value"/>).
    /// Pass <paramref name="autoFocus"/>=true to grab focus on the first frame it appears.
    /// </summary>
    public bool TextInput(string id, ref string value, bool autoFocus = false, float? width = null)
    {
        ulong wid  = IdFor(id);
        float x    = _inRow ? _rowCurX : _px + Pad;
        float y    = _inRow ? _rowBaseY : _cy;
        float w    = width ?? (_inRow ? _rowItemW : _pw - 2f * Pad);
        bool focused = _focusedInputId == wid;

        // Click to focus
        if (Hit(x, y, w, RowH) && _mousePressed)
        {
            _focusedInputId = wid;
            _inputBuffer    = value;
            _cursorPos      = _inputBuffer.Length;
            focused         = true;
        }
        // Auto-focus on first appearance when nothing else is focused
        if (autoFocus && !focused && _focusedInputId == 0)
        {
            _focusedInputId = wid;
            _inputBuffer    = value;
            _cursorPos      = _inputBuffer.Length;
            focused         = true;
        }

        bool committed = false;
        if (focused)
        {
            if (_textInput.Length > 0)
            {
                _inputBuffer = _inputBuffer[.._cursorPos] + _textInput + _inputBuffer[_cursorPos..];
                _cursorPos  += _textInput.Length;
            }
            if (_backspace && _cursorPos > 0)
            {
                _inputBuffer = _inputBuffer[..(_cursorPos - 1)] + _inputBuffer[_cursorPos..];
                _cursorPos--;
                _backspace   = false;
            }
            if (_enterKey)
            {
                value           = _inputBuffer;
                _focusedInputId = 0;
                _enterKey       = false;
                committed       = true;
            }
            if (_escapeKey)
            {
                _focusedInputId = 0;
                _escapeKey      = false;
            }
        }

        FillRect(x, y, w, RowH, focused ? ColSliderBg : ColBtnBg);
        DrawRect(x, y, w, RowH, focused ? ColAccent : ColBorder, 1f);
        _r.DrawText(focused ? _inputBuffer : value, new Vector2(x + 4f, y + 4f), ColText, FontNormal);

        if (focused)
        {
            string pre = _inputBuffer[.._cursorPos];
            var sz     = _r.MeasureText(pre, FontNormal);
            float cx   = x + 4f + sz.X;
            _r.DrawLine(new Vector2(cx, y + 3f), new Vector2(cx, y + RowH - 3f), ColAccent, 1.5f);
        }

        AdvanceItem();
        return committed;
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Direct drawing  (usable outside BeginPanel / EndPanel)
    // ─────────────────────────────────────────────────────────────────────────────

    public void FillRect(float x, float y, float w, float h, Color c)
    {
        if (w <= 0f || h <= 0f) return;
        Span<Vector2> v = stackalloc Vector2[4]
        { new(x, y), new(x + w, y), new(x + w, y + h), new(x, y + h) };
        _r.FillPolygon(v, c);
    }

    public void DrawRect(float x, float y, float w, float h, Color c, float lw = 1f)
    {
        Span<Vector2> v = stackalloc Vector2[4]
        { new(x, y), new(x + w, y), new(x + w, y + h), new(x, y + h) };
        _r.DrawPolygon(v, c, lw);
    }

    public void DrawLine(Vector2 a, Vector2 b, Color c, float w = 1f) =>
        _r.DrawLine(a, b, c, w);

    public void DrawText(string text, float x, float y, Color c, FontSpec? font = null) =>
        _r.DrawText(text, new Vector2(x, y), c, font ?? FontNormal);

    // ─────────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────────

    private bool Hit(float x, float y, float w, float h) =>
        _mouse.X >= x && _mouse.X < x + w &&
        _mouse.Y >= y && _mouse.Y < y + h;

    private void GetItemRect(out float x, out float y, out float w, out float h)
    {
        if (_inRow) { x = _rowCurX;  y = _rowBaseY; w = _rowItemW; h = RowH; }
        else        { x = _px + Pad; y = _cy;        w = _pw - 2f * Pad; h = RowH; }
    }

    private void AdvanceItem()
    {
        if (_inRow) _rowCurX += _rowItemW + _rowGap;
        else        _cy      += RowH + 1f;
    }

    // FNV-1a hash — stable per-label ID without allocating.
    private static ulong IdFor(string label)
    {
        ulong h = 14695981039346656037UL;
        foreach (char c in label) { unchecked { h ^= c; h *= 1099511628211UL; } }
        return h == 0UL ? 1UL : h;
    }

    private static string AutoFmt(float v, float min, float max) =>
        (max - min) switch
        {
            >= 100f => v.ToString("0"),
            >= 2f   => v.ToString("0.00"),
            _       => v.ToString("0.000"),
        };
}

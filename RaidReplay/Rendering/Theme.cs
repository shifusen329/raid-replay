using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using RaidReplay.Core.Analysis;

namespace RaidReplay.Rendering;

/// <summary>
/// Raid Replay's visual system: a dark slate palette with one accent and three severity colours, rounder and roomier
/// metrics, and larger heading fonts. Everything is scoped to our own windows: <see cref="PushWindow"/> in
/// <c>Window.PreDraw</c>, <see cref="PopWindow"/> in <c>Window.PostDraw</c>. The global ImGui style is never modified.
/// </summary>
public static class Theme
{
    // ---- palette (tune here) ----------------------------------------------------------------------------------

    public static readonly Vector4 WindowBg = Rgb(0x12151B, 0.98f);
    public static readonly Vector4 PanelBg = Rgb(0x171A21);
    public static readonly Vector4 CardBg = Rgb(0x1E222B);
    public static readonly Vector4 CardBgHover = Rgb(0x252A35);
    public static readonly Vector4 FrameBg = Rgb(0x232832);
    public static readonly Vector4 FrameBgHover = Rgb(0x2C323E);
    public static readonly Vector4 FrameBgActive = Rgb(0x343B49);
    public static readonly Vector4 Border = new(1, 1, 1, 0.07f);
    public static readonly Vector4 Text = Rgb(0xE8ECF2);
    public static readonly Vector4 TextDim = Rgb(0x8E97A8);
    public static readonly Vector4 TextFaint = Rgb(0x5E6676);

    /// <summary>The one accent colour (buttons, selection, active toggles, links).</summary>
    public static readonly Vector4 Accent = Rgb(0x5CB8E6);

    public static readonly Vector4 SevCritical = Rgb(0xF05C5C);
    public static readonly Vector4 SevMajor = Rgb(0xF2AA45);
    public static readonly Vector4 SevMinor = Rgb(0x8C9EBD);
    public static readonly Vector4 Good = Rgb(0x6BD98C);

    // ---- metrics (unscaled px; tune here) ---------------------------------------------------------------------

    public const float WindowRounding = 8;
    public const float ChildRounding = 8;
    public const float FrameRounding = 6;
    public const float PopupRounding = 6;
    public const float CardRounding = 8;
    public const float CardPadding = 10;
    public const float SeverityBarWidth = 4;
    public static readonly Vector2 WindowPadding = new(12, 10);
    public static readonly Vector2 FramePadding = new(8, 4);
    public static readonly Vector2 ItemSpacing = new(8, 6);
    public static readonly Vector2 ItemInnerSpacing = new(6, 4);
    public static readonly Vector2 CellPadding = new(6, 3);

    // ---- fonts ------------------------------------------------------------------------------------------------

    /// <summary>Verdict font: Dalamud's default font at <see cref="HeadingScale"/>× (icons merged).</summary>
    public const float HeadingScale = 1.55f;

    /// <summary>Section / card title font: Dalamud's default font at <see cref="TitleScale"/>×.</summary>
    public const float TitleScale = 1.18f;

    private static IFontHandle? heading;
    private static IFontHandle? title;

    private static float Scale => ImGuiHelpers.GlobalScale;

    public static void Init(IUiBuilder ui)
    {
        heading = ui.FontAtlas.NewDelegateFontHandle(e => e.OnPreBuild(tk => tk.AddDalamudDefaultFont(UiBuilder.DefaultFontSizePx * HeadingScale)));
        title = ui.FontAtlas.NewDelegateFontHandle(e => e.OnPreBuild(tk => tk.AddDalamudDefaultFont(UiBuilder.DefaultFontSizePx * TitleScale)));
    }

    public static void Dispose()
    {
        heading?.Dispose();
        title?.Dispose();
        heading = null;
        title = null;
    }

    /// <summary>Pushes the heading font (falls back to the current font until the atlas is built).</summary>
    public static IDisposable? HeadingFont() => heading?.Push();

    public static IDisposable? TitleFont() => title?.Push();

    // ---- window scope -----------------------------------------------------------------------------------------

    private static readonly System.Collections.Generic.Stack<(int Vars, int Colors)> Pushed = new();
    private static int colorCount;

    /// <summary>Pushes our style and colours. Call from <c>Window.PreDraw</c>; pair with <see cref="PopWindow"/>.</summary>
    public static void PushWindow()
    {
        var s = Scale;
        colorCount = 0;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, WindowRounding * s);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, ChildRounding * s);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, FrameRounding * s);
        ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, PopupRounding * s);
        ImGui.PushStyleVar(ImGuiStyleVar.GrabRounding, FrameRounding * s);
        ImGui.PushStyleVar(ImGuiStyleVar.TabRounding, FrameRounding * s);
        ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarRounding, 8 * s);
        ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarSize, 11 * s);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, WindowPadding * s);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, FramePadding * s);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, ItemSpacing * s);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemInnerSpacing, ItemInnerSpacing * s);
        ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, CellPadding * s);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1f);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildBorderSize, 1f);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 0f);

        Col(ImGuiCol.Text, Text);
        Col(ImGuiCol.TextDisabled, TextDim);
        Col(ImGuiCol.WindowBg, WindowBg);
        Col(ImGuiCol.ChildBg, new Vector4(0, 0, 0, 0));
        Col(ImGuiCol.PopupBg, Rgb(0x161A21, 0.98f));
        Col(ImGuiCol.Border, Border);
        Col(ImGuiCol.FrameBg, FrameBg);
        Col(ImGuiCol.FrameBgHovered, FrameBgHover);
        Col(ImGuiCol.FrameBgActive, FrameBgActive);
        Col(ImGuiCol.TitleBg, Rgb(0x0F1216));
        Col(ImGuiCol.TitleBgActive, Rgb(0x161A21));
        Col(ImGuiCol.TitleBgCollapsed, Rgb(0x0F1216, 0.8f));
        Col(ImGuiCol.ScrollbarBg, new Vector4(0, 0, 0, 0));
        Col(ImGuiCol.ScrollbarGrab, new Vector4(1, 1, 1, 0.10f));
        Col(ImGuiCol.ScrollbarGrabHovered, new Vector4(1, 1, 1, 0.18f));
        Col(ImGuiCol.ScrollbarGrabActive, With(Accent, 0.6f));
        Col(ImGuiCol.CheckMark, Accent);
        Col(ImGuiCol.SliderGrab, With(Accent, 0.8f));
        Col(ImGuiCol.SliderGrabActive, Accent);
        Col(ImGuiCol.Button, FrameBg);
        Col(ImGuiCol.ButtonHovered, FrameBgHover);
        Col(ImGuiCol.ButtonActive, With(Accent, 0.55f));
        Col(ImGuiCol.Header, With(Accent, 0.16f));
        Col(ImGuiCol.HeaderHovered, With(Accent, 0.24f));
        Col(ImGuiCol.HeaderActive, With(Accent, 0.32f));
        Col(ImGuiCol.Separator, new Vector4(1, 1, 1, 0.07f));
        Col(ImGuiCol.SeparatorHovered, With(Accent, 0.6f));
        Col(ImGuiCol.SeparatorActive, Accent);
        Col(ImGuiCol.Tab, Rgb(0x1A1E26));
        Col(ImGuiCol.TabHovered, With(Accent, 0.30f));
        Col(ImGuiCol.TabActive, Rgb(0x26303D));
        Col(ImGuiCol.TableHeaderBg, Rgb(0x1C2028));
        Col(ImGuiCol.TableBorderStrong, new Vector4(1, 1, 1, 0.08f));
        Col(ImGuiCol.TableBorderLight, new Vector4(1, 1, 1, 0.05f));
        Col(ImGuiCol.TextSelectedBg, With(Accent, 0.35f));
        Col(ImGuiCol.ResizeGrip, With(Accent, 0.15f));
        Col(ImGuiCol.ResizeGripHovered, With(Accent, 0.4f));
        Col(ImGuiCol.ResizeGripActive, With(Accent, 0.7f));
        Pushed.Push((StyleVarCount, colorCount));
    }

    private const int StyleVarCount = 16;

    public static void PopWindow()
    {
        if (!Pushed.TryPop(out var n))
            return;
        ImGui.PopStyleColor(n.Colors);
        ImGui.PopStyleVar(n.Vars);
    }

    private static void Col(ImGuiCol idx, Vector4 c)
    {
        ImGui.PushStyleColor(idx, c);
        colorCount++;
    }

    // ---- colours ----------------------------------------------------------------------------------------------

    public static Vector4 Rgb(uint hex, float alpha = 1f) =>
        new(((hex >> 16) & 0xFF) / 255f, ((hex >> 8) & 0xFF) / 255f, (hex & 0xFF) / 255f, alpha);

    public static Vector4 With(Vector4 c, float alpha) => new(c.X, c.Y, c.Z, c.W * alpha);

    public static uint U32(Vector4 c) => ImGui.ColorConvertFloat4ToU32(c);

    /// <summary>Severity colour of an incident: red for the root cause and severe incidents, amber, blue-grey.</summary>
    public static Vector4 Severity(Incident inc) =>
        inc.IsRootCause || inc.Severity >= 3 || inc.Kind is IncidentKind.Death or IncidentKind.FellOff or IncidentKind.Enrage ? SevCritical
        : inc.Severity == 2 ? SevMajor
        : SevMinor;

    public static string KindLabel(IncidentKind k) => k switch
    {
        IncidentKind.Death => "Death",
        IncidentKind.FellOff => "Fell off",
        IncidentKind.FailureAction => "Mechanic failed",
        IncidentKind.AvoidableHit => "Avoidable hit",
        IncidentKind.TowerUnderSoaked => "Tower under-soaked",
        IncidentKind.MissedStack => "Missed stack",
        IncidentKind.SpreadOverlap => "Spread overlap",
        IncidentKind.MissingMitigation => "Missing mitigation",
        IncidentKind.ArrowPuzzle => "Arrows",
        IncidentKind.Enrage => "Enrage",
        _ => k.ToString(),
    };

    // ---- widgets ----------------------------------------------------------------------------------------------

    /// <summary>A square FontAwesome icon button with a tooltip; <paramref name="active"/> draws it as a lit toggle.</summary>
    public static bool IconButton(string id, FontAwesomeIcon icon, string tooltip, bool active = false, bool enabled = true)
    {
        bool pressed;
        using (ImRaii.Disabled(!enabled))
        {
            using var c = ImRaii.PushColor(ImGuiCol.Button, With(Accent, 0.38f), active)
                                .Push(ImGuiCol.ButtonHovered, With(Accent, 0.5f), active)
                                .Push(ImGuiCol.Text, Accent, active);
            var h = ImGui.GetFrameHeight();
            pressed = ImGuiComponents.IconButton(id, icon, new Vector2(h * 1.15f, h));
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(tooltip);
        return pressed && enabled;
    }

    /// <summary>A text button with a tooltip.</summary>
    public static bool Button(string label, string tooltip, bool enabled = true)
    {
        bool pressed;
        using (ImRaii.Disabled(!enabled))
            pressed = ImGui.Button(label);
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(tooltip);
        return pressed && enabled;
    }

    /// <summary>Draws a FontAwesome glyph as text.</summary>
    public static void Icon(FontAwesomeIcon icon, Vector4 color)
    {
        using (ImRaii.PushFont(UiBuilder.IconFont))
            ImGui.TextColored(color, icon.ToIconString());
    }

    /// <summary>Hover-only "(?)" marker.</summary>
    public static void Help(string text)
    {
        ImGui.SameLine();
        ImGuiComponents.HelpMarker(text);
    }

    /// <summary>Tooltip for the last item.</summary>
    public static void Tip(string text)
    {
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(text);
    }

    public static void Dim(string text) => ImGui.TextColored(TextDim, text);

    /// <summary>Wrapped text in the given colour (honours an enclosing card's wrap position).</summary>
    public static void Wrapped(string text, Vector4 color)
    {
        using (ImRaii.PushColor(ImGuiCol.Text, color))
            ImGui.TextWrapped(text);
    }

    /// <summary>Small upper-case caption used above sections ("ROOT CAUSE").</summary>
    public static void Caption(string text, Vector4? color = null) => ImGui.TextColored(color ?? TextFaint, text);

    public static void SectionTitle(string text)
    {
        using (TitleFont())
            ImGui.TextUnformatted(text);
    }

    /// <summary>A thin horizontal rule in the border colour.</summary>
    public static void Rule()
    {
        var p = ImGui.GetCursorScreenPos();
        var w = ImGui.GetContentRegionAvail().X;
        ImGui.GetWindowDrawList().AddLine(p, p + new Vector2(w, 0), U32(Border), 1);
        ImGui.Dummy(new Vector2(w, 1));
    }

    // ---- chips ------------------------------------------------------------------------------------------------

    /// <summary>A row of pill-shaped chips that wraps at <see cref="MaxX"/> (screen space).</summary>
    public struct ChipRow
    {
        public float MaxX;
        public bool Started;

        public ChipRow(float maxX)
        {
            MaxX = maxX;
            Started = false;
        }

        /// <summary>A chip row that wraps at the current content region's right edge.</summary>
        public static ChipRow Here() => new(ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X);
    }

    /// <summary>Draws one chip: optional FontAwesome icon or job icon, then text. Returns true if hovered.</summary>
    public static bool Chip(ref ChipRow row, string text, Vector4 color, FontAwesomeIcon icon = FontAwesomeIcon.None, byte job = 0,
                            bool filled = true)
    {
        var s = Scale;
        var padX = 7 * s;
        var padY = 2 * s;
        var gap = 4 * s;
        var lineH = ImGui.GetTextLineHeight();
        var textW = ImGui.CalcTextSize(text).X;
        float iconW = 0;
        string? iconText = null;
        if (icon != FontAwesomeIcon.None)
        {
            iconText = icon.ToIconString();
            using (ImRaii.PushFont(UiBuilder.IconFont))
                iconW = ImGui.CalcTextSize(iconText).X;
        }
        else if (job != 0)
        {
            iconW = lineH;
        }

        var w = padX + (iconW > 0 ? iconW + gap : 0) + textW + padX;
        var h = lineH + (2 * padY);
        if (row.Started)
        {
            ImGui.SameLine(0, 5 * s);
            if (ImGui.GetCursorScreenPos().X + w > row.MaxX)
                ImGui.NewLine();
        }

        row.Started = true;
        var p = ImGui.GetCursorScreenPos();
        ImGui.Dummy(new Vector2(w, h));
        var hovered = ImGui.IsItemHovered();
        var dl = ImGui.GetWindowDrawList();
        dl.AddRectFilled(p, p + new Vector2(w, h), U32(With(color, filled ? (hovered ? 0.26f : 0.17f) : 0.0f)), h / 2);
        dl.AddRect(p, p + new Vector2(w, h), U32(With(color, filled ? 0.35f : 0.55f)), h / 2, ImDrawFlags.None, 1);
        var x = p.X + padX;
        if (iconText != null)
        {
            using (ImRaii.PushFont(UiBuilder.IconFont))
                dl.AddText(new Vector2(x, p.Y + padY), U32(color), iconText);
            x += iconW + gap;
        }
        else if (job != 0)
        {
            JobIconAt(dl, job, new Vector2(x, p.Y + padY), lineH, 1f);
            x += iconW + gap;
        }

        dl.AddText(new Vector2(x, p.Y + padY), U32(Text), text);
        return hovered;
    }

    /// <summary>Draws a job icon into a draw list (no layout).</summary>
    public static void JobIconAt(ImDrawListPtr dl, byte job, Vector2 min, float size, float alpha)
    {
        var icon = Core.GameData.Jobs.Get(job)?.Icon ?? 0;
        if (icon == 0)
            return;
        var tex = Plugin.TextureProvider.GetFromGameIcon(new Dalamud.Interface.Textures.GameIconLookup(icon)).GetWrapOrEmpty();
        dl.AddImage(tex.Handle, min, min + new Vector2(size), Vector2.Zero, Vector2.One, Palette.Rgba(1, 1, 1, alpha));
    }

    // ---- cards ------------------------------------------------------------------------------------------------

    /// <summary>State of an open card (see <see cref="BeginCard"/>).</summary>
    public struct Card
    {
        internal Vector2 Start;
        internal float Width;
        internal Vector4 Accent;
        internal ImDrawListPtr Dl;
        internal float Pad;

        /// <summary>Screen-space right edge of the card's content (for chip rows).</summary>
        public float ContentMaxX;
    }

    /// <summary>
    /// Starts a rounded card of the given width with a severity bar on the left. Content is laid out in a group and
    /// wraps at the card's inner edge. The background is drawn behind the content in <see cref="EndCard"/> (draw-list
    /// channels), so cards must not be nested.
    /// </summary>
    public static Card BeginCard(float width, Vector4 accent, float pad = CardPadding)
    {
        var s = Scale;
        var c = new Card
        {
            Start = ImGui.GetCursorScreenPos(),
            Width = width,
            Accent = accent,
            Dl = ImGui.GetWindowDrawList(),
            Pad = pad * s,
        };
        var bar = accent.W > 0 ? SeverityBarWidth * s : 0;
        c.Dl.ChannelsSplit(2);
        c.Dl.ChannelsSetCurrent(1);
        ImGui.SetCursorScreenPos(c.Start + new Vector2(c.Pad + bar, c.Pad));
        ImGui.BeginGroup();
        var inner = width - (2 * c.Pad) - bar;
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + inner);
        c.ContentMaxX = c.Start.X + width - c.Pad;
        return c;
    }

    /// <summary>Closes a card; returns true if it is hovered.</summary>
    public static bool EndCard(in Card c, bool highlight = false)
    {
        ImGui.PopTextWrapPos();
        ImGui.EndGroup();
        var max = new Vector2(c.Start.X + c.Width, ImGui.GetItemRectMax().Y + c.Pad);
        var hovered = ImGui.IsMouseHoveringRect(c.Start, max) && ImGui.IsWindowHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);
        c.Dl.ChannelsSetCurrent(0);
        var r = CardRounding * Scale;
        c.Dl.AddRectFilled(c.Start, max, U32(highlight ? CardBgHover : CardBg), r);
        if (highlight)
            c.Dl.AddRect(c.Start, max, U32(With(Accent, 0.45f)), r, ImDrawFlags.None, 1);
        if (c.Accent.W > 0)
            c.Dl.AddRectFilled(c.Start, new Vector2(c.Start.X + (SeverityBarWidth * Scale), max.Y), U32(c.Accent), r, ImDrawFlags.RoundCornersLeft);
        c.Dl.ChannelsMerge();
        ImGui.SetCursorScreenPos(new Vector2(c.Start.X, max.Y));
        ImGui.Dummy(new Vector2(c.Width, 0));
        return hovered;
    }

    // ---- tables -----------------------------------------------------------------------------------------------

    /// <summary>Header row with a tooltip per column (sorting and the hide/resize context menu keep working).</summary>
    public static void TableHeaders(string[] names, string[] tooltips)
    {
        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
        for (var i = 0; i < names.Length; i++)
        {
            if (!ImGui.TableSetColumnIndex(i))
                continue;
            ImGui.TableHeader(names[i]);
            if (i < tooltips.Length && tooltips[i].Length > 0 && ImGui.IsItemHovered())
                ImGui.SetTooltip(tooltips[i]);
        }
    }

    // ---- text -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Returns <paramref name="text"/> shortened with an ellipsis to fit <paramref name="maxWidth"/>. Recomputes only
    /// when the width changes (pass the same cache fields every frame).
    /// </summary>
    public static string Ellipsize(string text, float maxWidth, ref float cachedWidth, ref string? cached)
    {
        if (cached != null && Math.Abs(cachedWidth - maxWidth) < 0.5f)
            return cached;
        cachedWidth = maxWidth;
        if (ImGui.CalcTextSize(text).X <= maxWidth)
            return cached = text;
        int lo = 0, hi = text.Length;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (ImGui.CalcTextSize(string.Concat(text.AsSpan(0, mid), "…")).X <= maxWidth)
                lo = mid;
            else
                hi = mid - 1;
        }

        return cached = lo > 0 ? string.Concat(text.AsSpan(0, lo).TrimEnd(), "…") : "…";
    }
}

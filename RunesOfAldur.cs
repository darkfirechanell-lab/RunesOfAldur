using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using ExileCore2;
using ExileCore2.Shared;
using ExileCore2.Shared.Enums;
using RunesOfAldur.Api;

namespace RunesOfAldur;

public class RunesOfAldur : BaseSettingsPlugin<RunesOfAldurSettings>
{
    // Matches "1x Greater Iron Rune", "2x Exalted Orb", etc.
    private static readonly Regex RewardTextRegex = new(
        @"^\d+x?\s+.{3,}$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private PoeNinjaClient _prices = null!;

    private static readonly Color BestBorderColor = Color.FromArgb(255, 0, 255, 80);   // verde — borda melhor
    private static readonly Color PriceTextColor  = Color.FromArgb(255, 0, 0, 0);      // preto — todos os preços

    public RunesOfAldur() { Name = "Runes of Aldur"; }

    public override bool Initialise()
    {
        _prices = new PoeNinjaClient(msg => LogMessage(msg, 5));
        var league = GameController.IngameState.Data.ServerData.League;
        _prices.Start(league);
        return true;
    }

    public override void OnPluginDestroyForHotReload() => _prices?.Dispose();
    public override void Dispose() { base.Dispose(); _prices?.Dispose(); }

    public override void Tick()
    {
        if (!Settings.Enable) return;
        var league = GameController.IngameState.Data.ServerData.League;
        _prices.TryRefreshIfStale(league);
    }

    public override void Render()
    {
        if (!Settings.Enable) return;

        var panel = GetAltarPanel();

        // Always show status so we know Render() is running
        if (Settings.DebugLog)
        {
            var ui = GameController.Game.IngameState.IngameUi;
            var y = 180f;
            void L(string t, Color c) { Graphics.DrawText(t, new Vector2(10, y), c); y += 15f; }

            L($"[RunesOfAldur] panel={(panel == null ? "NULL" : "OK")} | cache={_prices.Prices.Count} | league={_prices.CurrentLeague}", Color.Yellow);
            L($"  LargePanels total={ui.LargePanels.Count} visible={ui.LargePanels.Count(p => p.IsVisible)}", Color.Cyan);
            L($"  LeftPanel={ui.OpenLeftPanel.IsVisible} RightPanel={ui.OpenRightPanel.IsVisible}", Color.Cyan);
            L($"  FullscreenPanels total={ui.FullscreenPanels.Count} visible={ui.FullscreenPanels.Count(p => p.IsVisible)}", Color.Cyan);
            L($"  IngameUi children={ui.Children.Count} visible={ui.Children.Count(c => c.IsVisible)}", Color.Cyan);

            // Show first few visible children of IngameUi for diagnosis
            var visChildren = ui.Children.Where(c => c.IsVisible).Take(5).ToList();
            foreach (var c in visChildren)
            {
                var r = c.GetClientRectCache;
                L($"    child visible children={c.Children.Count} rect={r.X:F0},{r.Y:F0} {r.Width:F0}x{r.Height:F0}", Color.Plum);
            }

            if (panel != null)
            {
                var r = panel.GetClientRectCache;
                L($"  Panel rect: {r.X:F0},{r.Y:F0} {r.Width:F0}x{r.Height:F0} children={panel.Children.Count}", Color.LimeGreen);
            }
        }

        if (panel == null || !panel.IsVisible) return;

        // Collect all visible reward rows: element + clean name
        var rows = CollectRewardRows(panel);

        if (rows.Count == 0)
        {
            if (Settings.DebugLog) DrawDebug([], panel);
            return;
        }

        // Score each row against poe.ninja
        var scored = rows.Select(r =>
        {
            // Get reward text — may be on a child element if r is the parent row
            var rawText = GetRewardText(r);
            var cleanName = StripQuantity(rawText);
            _prices.TryGetPrice(cleanName, out var entry);
            return (Element: r, Name: cleanName, Value: entry?.ExaltedValue ?? 0, Divine: entry?.DivineValue ?? 0);
        }).ToList();

        // If prices not loaded yet, show loading indicator on first row
        if (_prices.Prices.Count == 0)
        {
            if (rows.Count > 0)
            {
                var r = rows[0].GetClientRectCache;
                Graphics.DrawText("Loading prices...", new Vector2(r.X + r.Width + 4, r.Y), Color.Orange);
            }
            return;
        }

        var bestValue = scored.Max(r => r.Value);
        // Mark ALL rows that share the highest value (handles ties)
        var bestRows = bestValue > 0
            ? scored.Where(r => r.Value == bestValue).ToHashSet()
            : new HashSet<(ExileCore2.PoEMemory.Element, string, double, double)>();

        foreach (var row in scored)
        {
            var rect   = row.Element.GetClientRectCache;
            var isBest = bestRows.Contains(row);

            // Borda verde só na melhor linha
            if (isBest)
                Graphics.DrawFrame(rect, BestBorderColor, Settings.BestBorderThickness);

            // Preço — desenhado dentro da linha, alinhado à direita
            string priceStr;
            if (row.Value <= 0)
                priceStr = "?";
            else if (row.Divine >= Settings.ShowDivineThreshold)
                priceStr = $"{row.Divine:F1}d";
            else
                priceStr = $"{row.Value:F1} Ex";

            // Preço só aparece se ShowPriceOnAll estiver activo (ou se for a melhor linha)
            if (!Settings.ShowPriceOnAll.Value && !isBest) continue;

            var drawList = ImGuiNET.ImGui.GetBackgroundDrawList();
            const float scale = 1.4f;
            var lineH = ImGuiNET.ImGui.GetTextLineHeight() * scale;
            var textPos = new System.Numerics.Vector2(
                rect.X + rect.Width * (Settings.PriceXPercent / 100f),
                rect.Y + (rect.Height - lineH) / 2f);

            uint col = (uint)(PriceTextColor.A << 24 | PriceTextColor.B << 16 | PriceTextColor.G << 8 | PriceTextColor.R);

            // Desenhar 3x com pequeno offset para simular bold
            var offsets = new System.Numerics.Vector2[]
            {
                new(0, 0), new(1, 0), new(0, 1)
            };

            var font = ImGuiNET.ImGui.GetFont();
            foreach (var off in offsets)
            {
                var textSize = font.CalcTextSizeA(font.FontSize * scale, float.MaxValue, 0, priceStr);
                var pos = new System.Numerics.Vector2(textPos.X - textSize.X / 2f + off.X, textPos.Y + off.Y);
                drawList.AddText(font, font.FontSize * scale, pos, col, priceStr);
            }
        }

        if (Settings.DebugLog)
            DrawDebug(scored, panel);
        else if (rows.Count == 0 && Settings.DebugLog)
            DrawDebug([], panel);
    }

    // ── Altar panel ──────────────────────────────────────────────────────────

    private ExileCore2.PoEMemory.Element? GetAltarPanel()
    {
        var ui = GameController.Game.IngameState.IngameUi;
        var screenW = GameController.Window.GetWindowRectangle().Width;
        var screenH = GameController.Window.GetWindowRectangle().Height;

        // Walk the full IngameUi tree recursively looking for the altar panel.
        // The altar is ~430x420, sits on the left side, and contains "1x ..." reward text.
        return FindAltarInTree(ui, screenW, screenH, 0);
    }

    private static ExileCore2.PoEMemory.Element? FindAltarInTree(
        ExileCore2.PoEMemory.Element el, float screenW, float screenH, int depth)
    {
        if (depth > 6 || !el.IsVisible) return null;

        var rect = el.GetClientRectCache;

        // Altar is roughly 300-600px wide and 300-700px tall, left side of screen
        bool sizeOk   = rect.Width  is > 200 and < 700
                     && rect.Height is > 200 and < 800;
        bool leftSide = rect.X < screenW * 0.4f;
        bool notFull  = rect.Width < screenW * 0.9f && rect.Height < screenH * 0.9f;

        if (sizeOk && leftSide && notFull && ContainsRewardText(el))
            return el;

        foreach (var child in el.Children)
        {
            var found = FindAltarInTree(child, screenW, screenH, depth + 1);
            if (found != null) return found;
        }

        return null;
    }

    private static bool ContainsRewardText(ExileCore2.PoEMemory.Element el, int depth = 0)
    {
        if (depth > 5) return false;
        if (RewardTextRegex.IsMatch(el.Text?.Trim() ?? "")) return true;
        foreach (var child in el.Children)
            if (ContainsRewardText(child, depth + 1)) return true;
        return false;
    }

    // ── Collect reward row elements ───────────────────────────────────────────

    private static List<ExileCore2.PoEMemory.Element> CollectRewardRows(ExileCore2.PoEMemory.Element panel)
    {
        var rows = new List<ExileCore2.PoEMemory.Element>();
        FindRewardElements(panel, rows, 0);
        // Deduplicate by address, keep order
        return rows.DistinctBy(e => e.Address).ToList();
    }

    private static void FindRewardElements(ExileCore2.PoEMemory.Element el, List<ExileCore2.PoEMemory.Element> result, int depth)
    {
        if (depth > 10 || !el.IsVisible) return;

        // If any direct child has reward text, this element IS the row — add it and stop descending
        foreach (var child in el.Children)
        {
            if (RewardTextRegex.IsMatch(child.Text?.Trim() ?? ""))
            {
                result.Add(el);
                return; // don't recurse further — row found
            }
        }

        // Own text fallback (no children with reward text)
        if (RewardTextRegex.IsMatch(el.Text?.Trim() ?? ""))
        {
            result.Add(el);
            return;
        }

        foreach (var child in el.Children)
            FindRewardElements(child, result, depth + 1);
    }

    private static string GetRewardText(ExileCore2.PoEMemory.Element el)
    {
        // Try own text first
        var own = el.Text?.Trim() ?? "";
        if (RewardTextRegex.IsMatch(own)) return own;
        // Then children
        foreach (var child in el.Children)
        {
            var t = child.Text?.Trim() ?? "";
            if (RewardTextRegex.IsMatch(t)) return t;
        }
        return own;
    }

    private static string StripQuantity(string text)
    {
        // "1x Greater Iron Rune" → "Greater Iron Rune"
        // "2x Exalted Orb" → "Exalted Orb"
        var match = Regex.Match(text, @"^\d+x?\s+(.+)$");
        return match.Success ? match.Groups[1].Value.Trim() : text.Trim();
    }

    // ── Debug ─────────────────────────────────────────────────────────────────

    private void DrawDebug(
        List<(ExileCore2.PoEMemory.Element Element, string Name, double Value, double Divine)> rows,
        ExileCore2.PoEMemory.Element panel)
    {
        var y = 200f;
        void Line(string t, Color c) { Graphics.DrawText(t, new Vector2(10, y), c); y += 15f; }

        Line($"Cache: {_prices.Prices.Count} | Stale: {_prices.IsStale}", Color.Yellow);
        Line($"Rows found: {rows.Count}", Color.Cyan);
        foreach (var r in rows)
            Line($"  '{r.Name}'  {r.Value:F1}Ex  rect={r.Element.GetClientRectCache.X:F0},{r.Element.GetClientRectCache.Y:F0}", Color.White);

        // Also show all texts in panel for diagnosis
        var allTexts = new List<string>();
        CollectAllTexts(panel, allTexts, 0);
        y += 5f;
        Line($"All visible texts ({allTexts.Count}):", Color.Magenta);
        foreach (var t in allTexts.Take(30))
            Line($"  '{t}'", Color.Plum);
    }

    private static void CollectAllTexts(ExileCore2.PoEMemory.Element el, List<string> result, int depth)
    {
        if (depth > 6 || !el.IsVisible) return;
        var t = el.Text?.Trim();
        if (!string.IsNullOrEmpty(t)) result.Add(t);
        foreach (var child in el.Children)
            CollectAllTexts(child, result, depth + 1);
    }

    public override void DrawSettings()
    {
        base.DrawSettings();

        if (ImGuiNET.ImGui.Button("Force price refresh"))
        {
            _prices.Dispose();
            _prices = new PoeNinjaClient(msg => LogMessage(msg, 5));
            _prices.Start(GameController.IngameState.Data.ServerData.League);
        }

        // Mostrar slider de posição só quando ShowPriceOnAll está activo
        if (Settings.ShowPriceOnAll.Value)
        {
            ImGuiNET.ImGui.Spacing();
            ImGuiNET.ImGui.Separator();
            ImGuiNET.ImGui.Text("Price position settings:");
            var xPct = Settings.PriceXPercent.Value;
            if (ImGuiNET.ImGui.SliderFloat("Price X position (%)", ref xPct, 0f, 100f))
                Settings.PriceXPercent.Value = xPct;
        }
    }
}

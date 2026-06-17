using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    private static readonly Regex RewardTextRegex = new(
        @"^\d+x?\s+.{3,}$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private PoeNinjaClient _prices = null!;

    private static readonly TimeSpan RescanInterval = TimeSpan.FromMilliseconds(1000);
    private readonly Stopwatch _scanTimer = Stopwatch.StartNew();
    private TimeSpan _nextScan = TimeSpan.Zero;
    private List<(ExileCore2.PoEMemory.Element Element, string Name, double Value, double Divine)> _scoredCache = [];
    private ExileCore2.PoEMemory.Element? _panelCache;

    private static readonly Color BestBorderColor = Color.FromArgb(255, 0, 255, 80);
    private static readonly Color PriceTextColor = Color.FromArgb(255, 255, 255, 255);
    private static readonly Color PriceBgColor   = Color.FromArgb(190, 0, 0, 0);

    public RunesOfAldur() { Name = "Runes of Aldur"; }

    private long _lastTickUs;
    private long _lastRenderUs;

    public override bool Initialise()
    {
        _prices = new PoeNinjaClient(msg => LogMessage(msg, 5));
        var league = GameController.IngameState.Data.ServerData.League;
        _prices.Start(league);

        GameController.PluginBridge.SaveMethod("RunesOfAldur.GetTickUs", (Func<long>)(() => _lastTickUs));
        GameController.PluginBridge.SaveMethod("RunesOfAldur.GetRenderUs", (Func<long>)(() => _lastRenderUs));
        return true;
    }

    public override void OnPluginDestroyForHotReload() => _prices?.Dispose();
    public override void Dispose() { base.Dispose(); _prices?.Dispose(); }

    public override void Tick()
    {
        var _sw = Stopwatch.StartNew();
        try
        {
        if (!Settings.Enable) return;
        var league = GameController.IngameState.Data.ServerData.League;
        _prices.TryRefreshIfStale(league);
        }
        finally { _lastTickUs = _sw.ElapsedTicks * 1_000_000 / Stopwatch.Frequency; }
    }

    public override void Render()
    {
        var _sw = Stopwatch.StartNew();
        try
        {
        if (!Settings.Enable) return;

        if (!CanScan()) { _panelCache = null; _scoredCache = []; return; }

        if (_scanTimer.Elapsed >= _nextScan)
        {
            _nextScan = _scanTimer.Elapsed + RescanInterval;
            Rescan();
        }

        var panel = _panelCache;
        var scored = _scoredCache;

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

        if (scored.Count == 0)
        {
            if (Settings.DebugLog) DrawDebug([], panel);
            return;
        }

        if (_prices.Prices.Count == 0)
        {
            var r = scored[0].Element.GetClientRectCache;
            Graphics.DrawText("Loading prices...", new Vector2(r.X + r.Width + 4, r.Y), Color.Orange);
            return;
        }

        var bestValue = scored.Max(r => r.Value);
        var bestRows = bestValue > 0
            ? scored.Where(r => r.Value == bestValue).ToHashSet()
            : new HashSet<(ExileCore2.PoEMemory.Element, string, double, double)>();

        foreach (var row in scored)
        {
            var rect   = row.Element.GetClientRectCache;
            var isBest = bestRows.Contains(row);

            if (isBest)
                Graphics.DrawFrame(rect, BestBorderColor, Settings.BestBorderThickness);

            string priceStr;
            if (row.Value <= 0)
                priceStr = "?";
            else if (row.Divine >= Settings.ShowDivineThreshold)
                priceStr = $"{row.Divine:F1}d";
            else
                priceStr = $"{row.Value:F1} Ex";

            if (!Settings.ShowPriceOnAll.Value && !isBest) continue;

            var drawList = ImGuiNET.ImGui.GetBackgroundDrawList();
            const float scale = 1.4f;
            const float padX = 4f;
            const float padY = 2f;

            var win     = GameController.Window.GetWindowRectangle();
            var offsetX = win.Width  * ((Settings.PriceXPercent / 100f) - 0.5f);
            var offsetY = win.Height * ((Settings.PriceYPercent / 100f) - 0.5f);
            var centerX = rect.X + rect.Width  / 2f + offsetX;
            var centerY = rect.Y + rect.Height / 2f + offsetY;

            uint textCol = (uint)(PriceTextColor.A << 24 | PriceTextColor.B << 16 | PriceTextColor.G << 8 | PriceTextColor.R);
            uint bgCol   = (uint)(PriceBgColor.A   << 24 | PriceBgColor.B   << 16 | PriceBgColor.G   << 8 | PriceBgColor.R);

            var font = ImGuiNET.ImGui.GetFont();
            var textSize = font.CalcTextSizeA(font.FontSize * scale, float.MaxValue, 0, priceStr);

            var textPos = new System.Numerics.Vector2(centerX - textSize.X / 2f, centerY - textSize.Y / 2f);
            var bgMin   = new System.Numerics.Vector2(textPos.X - padX, textPos.Y - padY);
            var bgMax   = new System.Numerics.Vector2(textPos.X + textSize.X + padX, textPos.Y + textSize.Y + padY);

            drawList.AddRectFilled(bgMin, bgMax, bgCol);
            drawList.AddText(font, font.FontSize * scale, textPos, textCol, priceStr);
        }

        if (Settings.DebugLog)
            DrawDebug(scored, panel);
        }
        finally { _lastRenderUs = _sw.ElapsedTicks * 1_000_000 / Stopwatch.Frequency; }
    }

    private bool CanScan()
    {
        var gc = GameController;
        if (gc is null || !gc.InGame || gc.IsLoading) return false;
        if (gc.Area?.CurrentArea is { IsTown: true } or { IsHideout: true }) return false;

        var ui = gc.Game.IngameState.IngameUi;
        if (ui.StashElement.IsVisibleLocal) return false;

        return true;
    }

    private void Rescan()
    {
        var panel = GetAltarPanel();
        _panelCache = panel;

        if (panel == null || !panel.IsVisible)
        {
            _scoredCache = [];
            return;
        }

        var rows = CollectRewardRows(panel);
        if (rows.Count == 0)
        {
            _scoredCache = [];
            return;
        }

        var playerLevel = GameController.Player?.GetComponent<ExileCore2.PoEMemory.Components.Player>()?.Level ?? 0;
        _scoredCache = rows.Select(r =>
        {
            var rawText = GetRewardText(r);
            var (cleanName, qty) = StripQuantityWithCount(rawText);
            _prices.TryGetPrice(cleanName, playerLevel, out var entry);
            var unitEx  = entry?.ExaltedValue ?? 0;
            var unitDiv = entry?.DivineValue  ?? 0;
            return (Element: r, Name: cleanName, Value: unitEx * qty, Divine: unitDiv * qty);
        }).ToList();
    }

    // Título do painel da mecânica. Ancorar nele torna a deteção IMUNE à resolução
    // (a heurística por % do ecrã partia ao mudar de resolução / ligar 2º ecrã).
    private const string PanelTitle = "Runeshape Combinations";

    private ExileCore2.PoEMemory.Element? GetAltarPanel()
    {
        var ui = GameController.Game.IngameState.IngameUi;

        // 1) Deteção por TÍTULO (robusta, independente de resolução).
        var title = FindByText(ui, PanelTitle, 0);
        if (title != null)
        {
            // Sobe aos pais até apanhar o contentor que tem rows de reward no subtree.
            var node = title;
            for (int i = 0; i < 6 && node != null; i++)
            {
                if (ContainsRewardText(node)) return node;
                node = node.Parent;
            }
            return title.Parent ?? title;
        }

        // 2) Fallback: heurística antiga por geometria (caso o título mude numa patch).
        var screenW = GameController.Window.GetWindowRectangle().Width;
        var screenH = GameController.Window.GetWindowRectangle().Height;
        return FindAltarInTree(ui, screenW, screenH, 0);
    }

    private static ExileCore2.PoEMemory.Element? FindByText(
        ExileCore2.PoEMemory.Element el, string text, int depth)
    {
        if (depth > 14 || !el.IsVisible) return null;
        if (string.Equals(el.Text?.Trim(), text, StringComparison.OrdinalIgnoreCase))
            return el;
        foreach (var child in el.Children)
        {
            var found = FindByText(child, text, depth + 1);
            if (found != null) return found;
        }
        return null;
    }

    private static ExileCore2.PoEMemory.Element? FindAltarInTree(
        ExileCore2.PoEMemory.Element el, float screenW, float screenH, int depth)
    {
        if (depth > 6 || !el.IsVisible) return null;

        var rect = el.GetClientRectCache;

        float wFrac = rect.Width  / screenW;
        float hFrac = rect.Height / screenH;
        bool sizeOk   = wFrac is > 0.08f and < 0.42f
                     && hFrac is > 0.15f and < 0.85f;
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

    private static List<ExileCore2.PoEMemory.Element> CollectRewardRows(ExileCore2.PoEMemory.Element panel)
    {
        var rows = new List<ExileCore2.PoEMemory.Element>();
        FindRewardElements(panel, rows, 0);
        rows = rows.DistinctBy(e => e.Address).ToList();

        var bonusY = FindBonusRewardY(panel, 0);
        if (bonusY > 0)
            rows = rows.Where(r => r.GetClientRectCache.Y < bonusY).ToList();

        return rows;
    }

    private static float FindBonusRewardY(ExileCore2.PoEMemory.Element el, int depth)
    {
        if (depth > 8) return 0;
        var text = el.Text?.Trim() ?? "";
        if (text.Equals("Bonus Reward", System.StringComparison.OrdinalIgnoreCase))
            return el.GetClientRectCache.Y;
        foreach (var child in el.Children)
        {
            var y = FindBonusRewardY(child, depth + 1);
            if (y > 0) return y;
        }
        return 0;
    }

    private static void FindRewardElements(ExileCore2.PoEMemory.Element el, List<ExileCore2.PoEMemory.Element> result, int depth)
    {
        if (depth > 10 || !el.IsVisible) return;

        foreach (var child in el.Children)
        {
            if (RewardTextRegex.IsMatch(child.Text?.Trim() ?? ""))
            {
                result.Add(el);
                return;
            }
        }

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
        var own = el.Text?.Trim() ?? "";
        if (RewardTextRegex.IsMatch(own)) return own;
        foreach (var child in el.Children)
        {
            var t = child.Text?.Trim() ?? "";
            if (RewardTextRegex.IsMatch(t)) return t;
        }
        return own;
    }

    private static (string Name, int Qty) StripQuantityWithCount(string text)
    {
        var match = Regex.Match(text.Trim(), @"^(\d+)x?\s+(.+)$");
        if (match.Success)
            return (match.Groups[2].Value.Trim(), int.Parse(match.Groups[1].Value));
        return (text.Trim(), 1);
    }

    private static string StripQuantity(string text)
    {
        var match = Regex.Match(text, @"^\d+x?\s+(.+)$");
        return match.Success ? match.Groups[1].Value.Trim() : text.Trim();
    }

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

        if (Settings.ShowPriceOnAll.Value)
        {
            ImGuiNET.ImGui.Spacing();
            ImGuiNET.ImGui.Separator();
            ImGuiNET.ImGui.Text("Price position settings:");
            var xPct = Settings.PriceXPercent.Value;
            if (ImGuiNET.ImGui.SliderFloat("Price X position (%)", ref xPct, 0f, 100f))
                Settings.PriceXPercent.Value = xPct;
            var yPct = Settings.PriceYPercent.Value;
            if (ImGuiNET.ImGui.SliderFloat("Price Y position (%)", ref yPct, 0f, 100f))
                Settings.PriceYPercent.Value = yPct;
        }
    }
}

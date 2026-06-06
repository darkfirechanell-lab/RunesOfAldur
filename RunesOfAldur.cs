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
    // Matches "1x Greater Iron Rune", "2x Exalted Orb", etc.
    private static readonly Regex RewardTextRegex = new(
        @"^\d+x?\s+.{3,}$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private PoeNinjaClient _prices = null!;

    // A travessia da árvore de UI (encontrar o painel + apanhar os rows) é cara: faz leituras de
    // memória + regex em cada elemento. Em vez de a refazer a cada frame, fazemo-la no máximo a cada
    // RescanInterval e reaproveitamos os Elements em cache para desenhar (os rects atualizam-se
    // sozinhos via GetClientRectCache, por isso o overlay continua alinhado todos os frames).
    private static readonly TimeSpan RescanInterval = TimeSpan.FromMilliseconds(250);
    private readonly Stopwatch _scanTimer = Stopwatch.StartNew();
    private TimeSpan _nextScan = TimeSpan.Zero;
    private List<(ExileCore2.PoEMemory.Element Element, string Name, double Value, double Divine)> _scoredCache = [];
    private ExileCore2.PoEMemory.Element? _panelCache;

    private static readonly Color BestBorderColor = Color.FromArgb(255, 0, 255, 80);   // verde — borda melhor
    private static readonly Color PriceTextColor  = Color.FromArgb(255, 0, 0, 0);      // preto — todos os preços

    public RunesOfAldur() { Name = "Runes of Aldur"; }

    // PerfWatchdog bridge: tempo do último Tick/Render em microsegundos. O PerfWatchdog lê via
    // PluginBridge ("RunesOfAldur.GetTickUs"/"GetRenderUs") para medir o custo deste plugin por frame.
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

        // Gate barato: enquanto não há altar possível à vista não fazemos travessia nenhuma.
        // Estes checks são leituras simples (sem regex, sem descer a árvore) e cortam o custo a
        // zero na cidade, em loading, ou com a stash aberta (que tapa o sítio do altar).
        if (!CanScan()) { _panelCache = null; _scoredCache = []; return; }

        // Só refazemos a travessia cara periodicamente; entre scans desenhamos a partir do cache.
        // (Comparamos Elapsed com o próximo instante agendado em vez de subtrair TimeSpans, que
        // podia transbordar quando _nextScan ainda estava no valor inicial.)
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
            var lineH = ImGuiNET.ImGui.GetTextLineHeight() * scale;
            var textPos = new System.Numerics.Vector2(
                rect.X + rect.Width * (Settings.PriceXPercent / 100f),
                rect.Y + (rect.Height - lineH) / 2f);

            uint col = (uint)(PriceTextColor.A << 24 | PriceTextColor.B << 16 | PriceTextColor.G << 8 | PriceTextColor.R);

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
        }
        finally { _lastRenderUs = _sw.ElapsedTicks * 1_000_000 / Stopwatch.Frequency; }
    }

    // Pré-condições baratas para valer a pena procurar o altar. Tudo leituras simples — sem regex,
    // sem descer a árvore de UI. Quando isto devolve false não há sequer travessia.
    private bool CanScan()
    {
        var gc = GameController;
        if (gc is null || !gc.InGame || gc.IsLoading) return false;
        if (gc.Area?.CurrentArea is { IsTown: true } or { IsHideout: true }) return false;

        // A stash (e outros painéis grandes à esquerda) sobrepõem-se ao sítio do altar e teriam de
        // ser filtrados na travessia; mais barato é simplesmente não procurar com a stash aberta.
        if (gc.Game.IngameState.IngameUi.StashElement.IsVisibleLocal) return false;

        return true;
    }

    // Travessia cara (memória + regex) feita no máximo a cada RescanInterval, não por frame.
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

    private ExileCore2.PoEMemory.Element? GetAltarPanel()
    {
        var ui = GameController.Game.IngameState.IngameUi;
        var screenW = GameController.Window.GetWindowRectangle().Width;
        var screenH = GameController.Window.GetWindowRectangle().Height;
        return FindAltarInTree(ui, screenW, screenH, 0);
    }

    private static ExileCore2.PoEMemory.Element? FindAltarInTree(
        ExileCore2.PoEMemory.Element el, float screenW, float screenH, int depth)
    {
        if (depth > 6 || !el.IsVisible) return null;

        var rect = el.GetClientRectCache;

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

    private static List<ExileCore2.PoEMemory.Element> CollectRewardRows(ExileCore2.PoEMemory.Element panel)
    {
        var rows = new List<ExileCore2.PoEMemory.Element>();
        FindRewardElements(panel, rows, 0);
        rows = rows.DistinctBy(e => e.Address).ToList();

        // Find Y position of "Bonus Reward" label and exclude rows below it
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
        }
    }
}

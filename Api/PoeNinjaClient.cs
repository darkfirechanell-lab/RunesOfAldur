using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace RunesOfAldur.Api;

/// <summary>
/// Fetches prices from poe.ninja using the same URLs as NinjaPricer.
/// Detects the current league automatically from the game server.
/// Base: https://poe.ninja/poe2/api/economy/exchange/current/overview?league=X&type=Y
/// </summary>
public class PoeNinjaClient : IDisposable
{
    private const string BaseUrl = "https://poe.ninja/poe2/api/economy/exchange/current/overview";

    private static readonly (string Type, PriceCategory Cat)[] Categories =
    [
        ("Currency",   PriceCategory.Currency),
        ("Runes",      PriceCategory.Rune),
        ("Verisium",   PriceCategory.Verisium),
        ("Expedition", PriceCategory.Expedition),
        ("UncutGems",  PriceCategory.Other),
    ];

    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(30);

    private readonly HttpClient _http;
    private readonly Action<string> _log;

    private Dictionary<string, PriceEntry> _cache = [];
    private DateTime _lastFetch = DateTime.MinValue;
    private string _lastLeague = string.Empty;
    private CancellationTokenSource? _cts;
    private Task? _fetchTask;

    public IReadOnlyDictionary<string, PriceEntry> Prices => _cache;
    public DateTime LastUpdated => _lastFetch;
    public string CurrentLeague => _lastLeague;
    public bool IsStale => DateTime.UtcNow - _lastFetch > CacheDuration;

    public PoeNinjaClient(Action<string>? log = null)
    {
        _log = log ?? (_ => { });
        _http = new HttpClient(new HttpClientHandler { UseCookies = false });
        _http.Timeout = TimeSpan.FromSeconds(20);
        _http.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
    }

    public void Start(string league)
    {
        _cts = new CancellationTokenSource();
        // Fetch immediately on start
        _fetchTask = FetchAllAsync(league, _cts.Token);
    }

    /// <summary>Call from Tick() with the current league name from the game server.</summary>
    public void TryRefreshIfStale(string league)
    {
        var leagueChanged = !string.Equals(league, _lastLeague, StringComparison.OrdinalIgnoreCase);
        if (!IsStale && !leagueChanged) return;
        if (_fetchTask is { IsCompleted: false }) return;

        _fetchTask = FetchAllAsync(league, _cts?.Token ?? CancellationToken.None);
    }

    public bool TryGetPrice(string name, out PriceEntry entry)
    {
        return TryGetPriceInternal(name, out entry);
    }

    // keep old overload for compatibility
    public bool TryGetPrice(string name, int playerLevel, out PriceEntry entry) =>
        TryGetPriceInternal(name, out entry);

    private bool TryGetPriceInternal(string name, out PriceEntry entry)
    {
        // Exact match first
        if (_cache.TryGetValue(name, out entry!)) return true;
        foreach (var kv in _cache)
        {
            if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase))
            { entry = kv.Value; return true; }
        }

        // Partial match for levelled items e.g. "Uncut Spirit Gem" → "Uncut Spirit Gem (Level 15)"
        // Collect all entries that start with the base name
        var candidates = new List<(int Level, PriceEntry Entry)>();
        foreach (var kv in _cache)
        {
            if (!kv.Key.StartsWith(name, StringComparison.OrdinalIgnoreCase)) continue;

            // Try to extract level from "(Level X)"
            var match = System.Text.RegularExpressions.Regex.Match(kv.Key, @"\(Level (\d+)\)");
            var level = match.Success ? int.Parse(match.Groups[1].Value) : 0;
            candidates.Add((level, kv.Value));
        }

        if (candidates.Count == 0) { entry = null!; return false; }

        // No price for levelled items without exact level — show nothing
        entry = null!;
        return false;
    }

    private async Task RefreshLoop(CancellationToken token)
    {
        // Initial fetch will happen via TryRefreshIfStale from Tick()
        while (!token.IsCancellationRequested)
        {
            try { await Task.Delay(CacheDuration, token).ConfigureAwait(false); }
            catch (TaskCanceledException) { break; }
        }
    }

    private async Task FetchAllAsync(string league, CancellationToken token)
    {
        if (string.IsNullOrEmpty(league)) return;

        try
        {
            _log($"[RunesOfAldur] Fetching prices for league '{league}'...");

            var tasks = Categories.Select(c => (
                Cat: c,
                Task: _http.GetStringAsync(
                    $"{BaseUrl}?league={Uri.EscapeDataString(league)}&type={c.Type}", token)
            )).ToList();

            await Task.WhenAll(tasks.Select(t => t.Task)).ConfigureAwait(false);

            var newCache = new Dictionary<string, PriceEntry>(512, StringComparer.OrdinalIgnoreCase);
            double divineInEx = 78.0;

            // First pass — get divine rate from Currency
            foreach (var t in tasks.Where(t => t.Cat.Cat == PriceCategory.Currency))
                divineInEx = ExtractDivineRate(t.Task.Result, divineInEx);

            // Second pass — build all entries
            foreach (var t in tasks)
                ParseCategory(t.Task.Result, t.Cat.Cat, divineInEx, newCache);

            Interlocked.Exchange(ref _cache, newCache);
            _lastFetch  = DateTime.UtcNow;
            _lastLeague = league;

            _log($"[RunesOfAldur] {newCache.Count} prices loaded for '{league}' (1 Divine = {divineInEx:F0} Ex)");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log($"[RunesOfAldur] Fetch failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static double ExtractDivineRate(string json, double fallback)
    {
        try
        {
            var root    = JObject.Parse(json);
            var primary = (string?)root["core"]?["primary"] ?? "exalted";
            var rates   = root["core"]?["rates"];

            if (primary == "divine")
                return (double?)rates?["exalted"] ?? fallback;

            // primary = exalted — find divine line
            var idToName = BuildIdToName(root);
            foreach (var line in root["lines"] as JArray ?? [])
            {
                var id = (string?)line["id"] ?? "";
                if (idToName.TryGetValue(id, out var name) &&
                    name.Equals("Divine Orb", StringComparison.OrdinalIgnoreCase))
                    return (double?)line["primaryValue"] ?? fallback;
            }
        }
        catch { }
        return fallback;
    }

    private static void ParseCategory(
        string json, PriceCategory cat, double divineInEx,
        Dictionary<string, PriceEntry> target)
    {
        var root     = JObject.Parse(json);
        var primary  = (string?)root["core"]?["primary"] ?? "exalted";
        var idToName = BuildIdToName(root);

        foreach (var line in root["lines"] as JArray ?? [])
        {
            var id  = (string?)line["id"] ?? "";
            if (!idToName.TryGetValue(id, out var name) || string.IsNullOrEmpty(name)) continue;

            var pv = (double?)line["primaryValue"] ?? 0;

            // Normalise to Exalted
            double exVal  = primary == "divine" ? pv * divineInEx : pv;
            double divVal = divineInEx > 0 ? exVal / divineInEx : 0;

            target[name] = new PriceEntry
            {
                Name         = name,
                ExaltedValue = exVal,
                DivineValue  = divVal,
                Category     = cat,
            };
        }
    }

    private static Dictionary<string, string> BuildIdToName(JObject root)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in root["items"] as JArray ?? [])
        {
            var id   = (string?)item["id"]   ?? "";
            var name = (string?)item["name"] ?? "";
            if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(name))
                dict[id] = name;
        }
        return dict;
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _http.Dispose();
    }
}

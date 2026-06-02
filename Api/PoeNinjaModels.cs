using System.Collections.Generic;
using Newtonsoft.Json;

namespace RunesOfAldur.Api;

// ── Exchange overview (currency-type endpoint) ───────────────────────────────

public class ExchangeOverview
{
    [JsonProperty("core")]
    public CoreData Core { get; set; } = new();

    [JsonProperty("lines")]
    public List<ExchangeLine> Lines { get; set; } = [];

    [JsonProperty("items")]
    public List<ExchangeItem> Items { get; set; } = [];
}

public class CoreData
{
    [JsonProperty("primary")]
    public string Primary { get; set; } = string.Empty;

    [JsonProperty("rates")]
    public Rates Rates { get; set; } = new();
}

public class Rates
{
    [JsonProperty("exalted")]
    public double? Exalted { get; set; }

    [JsonProperty("chaos")]
    public double? Chaos { get; set; }
}

public class ExchangeItem
{
    [JsonProperty("id")]
    public string Id { get; set; } = string.Empty;

    [JsonProperty("name")]
    public string Name { get; set; } = string.Empty;
}

public class ExchangeLine
{
    [JsonProperty("id")]
    public string Id { get; set; } = string.Empty;

    [JsonProperty("primaryValue")]
    public double PrimaryValue { get; set; }
}

// ── Unified price entry ───────────────────────────────────────────────────────

public enum PriceCategory { Currency, Rune, Verisium, Expedition, Other }

public class PriceEntry
{
    public string Name { get; init; } = string.Empty;

    /// <summary>Value in Exalted Orbs (primary currency on poe.ninja for PoE 2).</summary>
    public double ExaltedValue { get; init; }

    /// <summary>Value in Chaos (computed from ExaltedValue / chaos rate).</summary>
    public double ChaosValue { get; init; }

    /// <summary>Value in Divine (computed from ExaltedValue / divine rate).</summary>
    public double DivineValue { get; init; }

    public PriceCategory Category { get; init; }
}

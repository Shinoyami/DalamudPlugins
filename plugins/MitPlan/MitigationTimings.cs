using System;
using System.Collections.Generic;
using System.Linq;

namespace MitPlan;

internal static class MitigationTimings
{
    // Lead times use the action's effective mitigation or barrier window. For upgraded
    // tank short cooldowns, use the strongest four-second portion of the effect.
    private static readonly IReadOnlyDictionary<string, int> Timings =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            // Tank role and shared party mitigation.
            ["Rampart"] = 20,
            ["Reprisal"] = 15,
            ["Arm's Length"] = 15,

            // Warrior.
            ["Thrill of Battle"] = 10,
            ["Vengeance"] = 15,
            ["Damnation"] = 15,
            ["Raw Intuition"] = 4,
            ["Bloodwhetting"] = 4,
            ["Nascent Flash"] = 4,
            ["Holmgang"] = 10,
            ["Shake It Off"] = 30,

            // Paladin.
            ["Bulwark"] = 10,
            ["Sentinel"] = 15,
            ["Guardian"] = 15,
            ["Sheltron"] = 4,
            ["Holy Sheltron"] = 4,
            ["Intervention"] = 4,
            ["Hallowed Ground"] = 10,
            ["Divine Veil"] = 30,
            ["Passage of Arms"] = 5,

            // Dark knight.
            ["Dark Mind"] = 10,
            ["Shadow Wall"] = 15,
            ["Shadowed Vigil"] = 15,
            ["The Blackest Night"] = 7,
            ["Oblation"] = 10,
            ["Living Dead"] = 10,
            ["Dark Missionary"] = 15,

            // Gunbreaker.
            ["Camouflage"] = 20,
            ["Nebula"] = 15,
            ["Great Nebula"] = 15,
            ["Heart of Stone"] = 4,
            ["Heart of Corundum"] = 4,
            ["Superbolide"] = 10,
            ["Heart of Light"] = 15,

            // Healer role and white mage.
            ["Temperance"] = 20,
            ["Divine Benison"] = 15,
            ["Aquaveil"] = 8,
            ["Asylum"] = 24,
            ["Plenary Indulgence"] = 10,
            ["Confession"] = 10,
            ["Divine Caress"] = 10,
            ["Liturgy of the Bell"] = 20,

            // Astrologian.
            ["Collective Unconscious"] = 10,
            ["Neutral Sect"] = 20,
            ["Sun Sign"] = 15,
            ["Exaltation"] = 8,
            ["Celestial Intersection"] = 30,
            ["Aspected Benefic"] = 30,
            ["Aspected Helios"] = 30,
            ["The Bole"] = 15,
            ["The Spire"] = 15,

            // Scholar.
            ["Adloquium"] = 30,
            ["Succor"] = 30,
            ["Concitation"] = 30,
            ["Deployment Tactics"] = 30,
            ["Sacred Soil"] = 15,
            ["Expedient"] = 20,
            ["Fey Illumination"] = 20,
            ["Protraction"] = 10,
            ["Recitation"] = 15,
            ["Consolation"] = 30,
            ["Summon Seraph"] = 22,
            ["Seraph"] = 22,

            // Sage.
            ["Eukrasian Diagnosis"] = 30,
            ["Eukrasian Prognosis"] = 30,
            ["Kerachole"] = 15,
            ["Taurochole"] = 15,
            ["Haima"] = 22,
            ["Panhaima"] = 22,
            ["Holos"] = 20,
            ["Physis"] = 15,
            ["Physis II"] = 15,
            ["Zoe"] = 30,
            ["Philosophia"] = 20,
            ["Krasis"] = 10,

            // DPS role and job mitigation.
            ["Feint"] = 15,
            ["Addle"] = 15,
            ["Shade Shift"] = 20,
            ["Third Eye"] = 4,
            ["Tengentsu"] = 4,
            ["Riddle of Earth"] = 10,
            ["Troubadour"] = 15,
            ["Tactician"] = 15,
            ["Shield Samba"] = 15,
            ["Dismantle"] = 10,
            ["Nature's Minne"] = 15,
            ["Improvisation"] = 15,
            ["Magick Barrier"] = 10,
            ["Mantra"] = 15,
            ["Arcane Crest"] = 5,
            ["Manaward"] = 20,
            ["Radiant Aegis"] = 30,
            ["Tempera Coat"] = 10,
            ["Tempera Grassa"] = 10,

            // Blue mage mitigation commonly used in synced group content.
            ["Diamondback"] = 10,
            ["Gobskin"] = 30,
        };

    private static readonly (string Text, int Seconds)[] SearchTimings = Timings
        .OrderByDescending(pair => pair.Key.Length)
        .Select(pair => (pair.Key, pair.Value))
        .Concat(
        [
            ("Collective Unconsious", 10),
            ("Sacred Sacred Soil", 15),
            ("Fey Illum", 20),
            ("Spreadlo", 30),
            ("Spreadlow", 30),
            ("EDiag", 30),
            ("Zoe Shields", 30),
        ])
        .ToArray();

    internal static int LeadSeconds(string skill, int fallbackSeconds)
    {
        var normalized = skill.Trim();
        if (normalized.EndsWith(" (Buddy)", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[..^8].TrimEnd();
        if (Timings.TryGetValue(normalized, out var exact))
            return exact;

        foreach (var (text, seconds) in SearchTimings)
        {
            if (normalized.Contains(text, StringComparison.OrdinalIgnoreCase))
                return seconds;
        }

        return Math.Clamp(fallbackSeconds, 0, 60);
    }
}

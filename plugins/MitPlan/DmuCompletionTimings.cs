using System;
using System.Collections.Generic;
using System.Linq;

namespace MitPlan;

internal static class DmuCompletionTimings
{
    // Global encounter-clock times for the completed mechanic or first damaging hit.
    // The fight's display offset is applied later by the overlay and alert scheduler.
    private static readonly IReadOnlyDictionary<int, float> CompletionTimes = new Dictionary<int, float>
    {
        [16] = 15.6f, [38] = 37.4f, [43] = 42.5f, [50] = 49.7f, [63] = 62.7f,
        [65] = 65.8f, [88] = 87.2f, [98] = 97.1f, [106] = 105.8f, [118] = 118.1f,
        [132] = 132.4f, [136] = 135.6f, [165] = 170.6f, [173] = 173.4f, [187] = 186.3f,

        [220] = 220.1f, [221] = 220.1f, [236] = 235.3f, [250] = 249.1f, [260] = 259.1f,
        [271] = 269.1f, [281] = 280.1f, [292] = 290.4f, [302] = 301.1f, [312] = 311.2f,
        [323] = 322.0f, [343] = 341.1f, [371] = 369.5f, [378] = 376.7f,

        [450] = 447.9f, [470] = 468.0f, [478] = 476.3f, [479] = 476.3f, [497] = 495.0f,
        [507] = 505.3f, [514] = 512.3f, [518] = 516.3f, [536] = 534.7f, [545] = 542.7f,
        [554] = 551.8f, [559] = 556.8f, [578] = 575.9f, [595] = 593.4f, [609] = 606.2f,
        [616] = 613.6f, [621] = 618.8f, [626] = 624.0f, [637] = 634.7f, [650] = 647.8f,
        [677] = 674.3f, [691] = 688.5f, [705] = 702.9f,

        [763] = 763.7f, [769] = 768.7f, [778] = 778.4f, [783] = 783.5f, [793] = 793.3f,
        [794] = 793.3f, [805] = 805.4f, [815] = 814.4f, [816] = 814.4f, [833] = 832.5f,
        [840] = 839.4f, [841] = 839.4f, [872] = 871.3f, [873] = 871.3f,

        [911] = 906.4f, [916] = 912.7f, [928] = 925.3f, [940] = 937.3f, [948] = 938.2f,
        [953] = 949.5f, [971] = 959.3f, [993] = 988.6f, [998] = 994.9f, [1024] = 1018.4f,
        [1033] = 1027.5f, [1040] = 1028.4f, [1045] = 1039.8f, [1048] = 1043.0f,
        [1051] = 1046.2f, [1062] = 1057.5f, [1067] = 1062.5f, [1070] = 1065.5f,
        [1076] = 1070.5f, [1079] = 1073.5f, [1084] = 1078.5f, [1087] = 1081.5f,
        [1092] = 1086.5f, [1126] = 1117.5f,
    };

    public static void Link(FightPlan fight)
    {
        foreach (var item in fight.Timeline)
        {
            var tankThunder = TankThunderTiming(item);
            if (tankThunder is null && !CompletionTimes.TryGetValue(item.TimeSeconds, out _))
                continue;
            var completionTime = tankThunder?.Time ?? CompletionTimes[item.TimeSeconds];
            var phase = PhaseFor(fight, item);
            if (phase is null)
                continue;
            var rawPhaseStart = phase.EncounterTimelineStartSeconds > 0
                ? phase.EncounterTimelineStartSeconds
                : EncounterTimelines.PhaseStart(fight.Id, phase.Key) ?? phase.StartSeconds;
            var rawCompletionTime = rawPhaseStart + completionTime - phase.StartSeconds;
            var id = tankThunder is null
                ? $"completion-dmu-{item.TimeSeconds}"
                : $"completion-dmu-thunder-{item.TimeSeconds}-{item.TargetRole.ToLowerInvariant()}-hit{tankThunder.Value.Hit}";
            if (fight.EncounterTimeline.All(candidate => candidate.Id != id))
            {
                fight.EncounterTimeline.Add(new EncounterTimelineEvent
                {
                    Id = id,
                    TimeSeconds = rawCompletionTime,
                    SyncToSeconds = rawCompletionTime,
                    Name = MechanicName(item.Note),
                    Phase = phase.Key,
                });
            }
            item.EncounterEventId = id;
        }
    }

    private static (float Time, int Hit)? TankThunderTiming(TimelineItem item)
    {
        if (!item.Note.Equals("Tank personal plan: Thunder III", StringComparison.OrdinalIgnoreCase))
            return null;

        return (item.TimeSeconds, item.TargetRole) switch
        {
            (479, "MT") => (476.3f, 1),
            (479, "OT") => (479.3f, 2),
            (536, "MT") => (537.7f, 2),
            (554, "OT") => (551.8f, 1),
            (554, "MT") => (554.8f, 2),
            (595, "OT") => (596.4f, 2),
            (637, "OT") => (634.7f, 1),
            (637, "MT") => (637.7f, 2),
            _ => null,
        };
    }

    private static FightPhase? PhaseFor(FightPlan fight, TimelineItem item)
    {
        var separator = item.Note.IndexOf('|');
        if (separator > 0)
        {
            var label = item.Note[..separator].Trim();
            var labeled = fight.Phases.FirstOrDefault(phase =>
                phase.Key.Equals(label, StringComparison.OrdinalIgnoreCase) ||
                phase.Key.StartsWith($"{label} ", StringComparison.OrdinalIgnoreCase));
            if (labeled is not null)
                return labeled;
        }
        return fight.Phases.Where(phase => phase.StartSeconds <= item.TimeSeconds)
            .OrderByDescending(phase => phase.StartSeconds)
            .FirstOrDefault();
    }

    private static string MechanicName(string note)
    {
        var colon = note.LastIndexOf(':');
        return (colon >= 0 ? note[(colon + 1)..] : note).Trim();
    }
}

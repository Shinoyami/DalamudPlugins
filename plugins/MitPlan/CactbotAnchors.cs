using System.Collections.Generic;
using System.Linq;

namespace MitPlan;

// Stable action syncs from cactbot's encounter timelines, translated to MitPlan's phase clocks.
internal static class CactbotAnchors
{
    // Keep a wider acquisition window than cactbot's default so a one-shot
    // anchor can recover a clock that has already drifted. Matching within the
    // window still follows cactbot's chronological order.
    private const int DefaultMatchWindowSeconds = 20;

    public static IEnumerable<TimelineSyncTrigger> For(string fightId) => (fightId switch
    {
        "m12s" => M12S,
        "ucob" => UCOB,
        "uwu" => UWU,
        "dsr" => DSR,
        "top" => TOP,
        "fru" => FRU,
        "dmu" => DMU,
        _ => [],
    }).Select(anchor => new TimelineSyncTrigger
    {
        EventType = anchor.Type,
        EventId = anchor.Id,
        TimelineSeconds = anchor.Time,
        Name = $"0x{anchor.Id:X} cactbot sync",
        RequiredPhase = anchor.Phase,
        ResultPhase = string.Empty,
        SuppressSeconds = 0,
        MatchWindowSeconds = DefaultMatchWindowSeconds,
    });

    private readonly record struct Anchor(TimelineSyncEventType Type, uint Id, int Time, string Phase);

    private static readonly Anchor[] M12S =
    [
        new(TimelineSyncEventType.CastStart, 0xB4D7u, 11, "P1 Lindwurm"), new(TimelineSyncEventType.Ability, 0xB7C4u, 26, "P1 Lindwurm"), new(TimelineSyncEventType.Ability, 0xB495u, 41, "P1 Lindwurm"),
        new(TimelineSyncEventType.Ability, 0xB7C5u, 53, "P1 Lindwurm"), new(TimelineSyncEventType.Ability, 0xB9DBu, 61, "P1 Lindwurm"), new(TimelineSyncEventType.Ability, 0xB4C1u, 144, "P1 Lindwurm"),
        new(TimelineSyncEventType.Ability, 0xB4CBu, 190, "P1 Lindwurm"), new(TimelineSyncEventType.Ability, 0xB479u, 232, "P1 Lindwurm"), new(TimelineSyncEventType.Ability, 0xB46Eu, 266, "P1 Lindwurm"),
        new(TimelineSyncEventType.Ability, 0xB4CCu, 290, "P1 Lindwurm"), new(TimelineSyncEventType.Ability, 0xB7C4u, 299, "P1 Lindwurm"), new(TimelineSyncEventType.Ability, 0xB495u, 314, "P1 Lindwurm"),
        new(TimelineSyncEventType.Ability, 0xB7C5u, 326, "P1 Lindwurm"), new(TimelineSyncEventType.Ability, 0xB4CBu, 343, "P1 Lindwurm"), new(TimelineSyncEventType.Ability, 0xB4CDu, 343, "P1 Lindwurm"),
        new(TimelineSyncEventType.Ability, 0xB4CEu, 343, "P1 Lindwurm"), new(TimelineSyncEventType.Ability, 0xB4CCu, 343, "P1 Lindwurm"), new(TimelineSyncEventType.Ability, 0xB4E2u, 704, "P2 Lindwurm"),
        new(TimelineSyncEventType.Ability, 0xB506u, 786, "P2 Lindwurm"), new(TimelineSyncEventType.Ability, 0xBCB0u, 805, "P2 Lindwurm"), new(TimelineSyncEventType.Ability, 0xBCB0u, 808, "P2 Lindwurm"),
        new(TimelineSyncEventType.Ability, 0xBCB0u, 811, "P2 Lindwurm"), new(TimelineSyncEventType.Ability, 0xB4FCu, 819, "P2 Lindwurm"), new(TimelineSyncEventType.Ability, 0xB4FCu, 824, "P2 Lindwurm"),
        new(TimelineSyncEventType.Ability, 0xB4E2u, 885, "P2 Lindwurm"), new(TimelineSyncEventType.Ability, 0xB51Eu, 1060, "P2 Lindwurm"), new(TimelineSyncEventType.CastStart, 0xB537u, 1144, "P2 Lindwurm"),
    ];

    private static readonly Anchor[] UCOB =
    [
        new(TimelineSyncEventType.Ability, 0x2707u, 1231, "P5 Golden Bahamut"),
    ];

    private static readonly Anchor[] UWU =
    [
        new(TimelineSyncEventType.CastStart, 0x2B53u, 6, "P1 Garuda"), new(TimelineSyncEventType.CastStart, 0x2B5Eu, 370, "P2 Ifrit"), new(TimelineSyncEventType.Ability, 0x2B68u, 635, "P3 Titan"),
        new(TimelineSyncEventType.Ability, 0x2B68u, 722, "P3 Titan"), new(TimelineSyncEventType.CastStart, 0x2B72u, 803, "Transition"), new(TimelineSyncEventType.CastStart, 0x2B74u, 824, "Transition"),
        new(TimelineSyncEventType.CastStart, 0x2B8Bu, 845, "Transition"), new(TimelineSyncEventType.CastStart, 0x2B76u, 1021, "P4 Ultima Weapon"), new(TimelineSyncEventType.Ability, 0x2B68u, 1068, "P4 Ultima Weapon"),
        new(TimelineSyncEventType.CastStart, 0x2D4Cu, 1099, "P4 Ultima Weapon"), new(TimelineSyncEventType.CastStart, 0x2D4Du, 1192, "P4 Ultima Weapon"), new(TimelineSyncEventType.Ability, 0x2CD3u, 1300, "P4 Ultima Weapon"),
        new(TimelineSyncEventType.Ability, 0x2CD4u, 1400, "P4 Ultima Weapon"), new(TimelineSyncEventType.Ability, 0x2CD5u, 1500, "P4 Ultima Weapon"),
    ];

    private static readonly Anchor[] DSR =
    [
        new(TimelineSyncEventType.CastStart, 0x62D4u, 6, "P1 Vault Knights"), new(TimelineSyncEventType.Ability, 0x63EBu, 24, "P1 Vault Knights"), new(TimelineSyncEventType.Ability, 0x62D6u, 44, "P1 Vault Knights"),
        new(TimelineSyncEventType.Ability, 0x6315u, 51, "P1 Vault Knights"), new(TimelineSyncEventType.Ability, 0x63C9u, 183, "P2 Thordan"), new(TimelineSyncEventType.Ability, 0x63C4u, 190, "P2 Thordan"),
        new(TimelineSyncEventType.Ability, 0x63D5u, 209, "P2 Thordan"), new(TimelineSyncEventType.Ability, 0x63C9u, 218, "P2 Thordan"), new(TimelineSyncEventType.Ability, 0x63C4u, 218, "P2 Thordan"),
        new(TimelineSyncEventType.Ability, 0x63DEu, 228, "P2 Thordan"), new(TimelineSyncEventType.Ability, 0x63EBu, 228, "P2 Thordan"), new(TimelineSyncEventType.Ability, 0x63C4u, 231, "P2 Thordan"),
        new(TimelineSyncEventType.Ability, 0x63C4u, 259, "P2 Thordan"), new(TimelineSyncEventType.Ability, 0x63C4u, 287, "P2 Thordan"), new(TimelineSyncEventType.Ability, 0x63E8u, 303, "P2 Thordan"),
        new(TimelineSyncEventType.Ability, 0x63BCu, 321, "P2 Thordan"), new(TimelineSyncEventType.Ability, 0x63BDu, 326, "P2 Thordan"), new(TimelineSyncEventType.Ability, 0x63BFu, 340, "P2 Thordan"),
        new(TimelineSyncEventType.CastStart, 0x63C3u, 360, "P2 Thordan"), new(TimelineSyncEventType.Ability, 0x68C3u, 580, "P4 Eyes"), new(TimelineSyncEventType.CastStart, 0x69B5u, 661, "Rewind"),
        new(TimelineSyncEventType.Ability, 0x6317u, 700, "P5 Dark Thordan"), new(TimelineSyncEventType.Ability, 0x63C4u, 736, "P5 Dark Thordan"), new(TimelineSyncEventType.Ability, 0x63E4u, 766, "P5 Dark Thordan"),
        new(TimelineSyncEventType.Ability, 0x63C4u, 796, "P5 Dark Thordan"), new(TimelineSyncEventType.CastStart, 0x63C6u, 921, "P5 Dark Thordan"), new(TimelineSyncEventType.CastStart, 0x6B88u, 949, "P5 Dark Thordan"),
        new(TimelineSyncEventType.Ability, 0x6D40u, 1145, "P6 Double Dragons"), new(TimelineSyncEventType.Ability, 0x63F3u, 1219, "P7 Dragon King"),
        new(TimelineSyncEventType.Ability, 0x6D9Bu, 1252, "P7 Dragon King"), new(TimelineSyncEventType.Ability, 0x6D9Eu, 1260, "P7 Dragon King"), new(TimelineSyncEventType.Ability, 0x6D9Eu, 1264, "P7 Dragon King"),
        new(TimelineSyncEventType.Ability, 0x6D93u, 1273, "P7 Dragon King"), new(TimelineSyncEventType.Ability, 0x6D9Eu, 1285, "P7 Dragon King"), new(TimelineSyncEventType.Ability, 0x6D9Eu, 1289, "P7 Dragon King"),
        new(TimelineSyncEventType.Ability, 0x6D99u, 1300, "P7 Dragon King"), new(TimelineSyncEventType.Ability, 0x6D9Eu, 1318, "P7 Dragon King"), new(TimelineSyncEventType.Ability, 0x6D9Eu, 1322, "P7 Dragon King"),
        new(TimelineSyncEventType.Ability, 0x6D9Bu, 1331, "P7 Dragon King"), new(TimelineSyncEventType.Ability, 0x6D9Eu, 1339, "P7 Dragon King"), new(TimelineSyncEventType.Ability, 0x6D9Eu, 1343, "P7 Dragon King"),
        new(TimelineSyncEventType.Ability, 0x6D93u, 1352, "P7 Dragon King"), new(TimelineSyncEventType.Ability, 0x6D9Eu, 1365, "P7 Dragon King"), new(TimelineSyncEventType.Ability, 0x6D9Eu, 1369, "P7 Dragon King"),
        new(TimelineSyncEventType.Ability, 0x6D9Eu, 1398, "P7 Dragon King"), new(TimelineSyncEventType.Ability, 0x6D9Eu, 1402, "P7 Dragon King"), new(TimelineSyncEventType.Ability, 0x6D9Bu, 1411, "P7 Dragon King"),
        new(TimelineSyncEventType.Ability, 0x6D9Eu, 1419, "P7 Dragon King"), new(TimelineSyncEventType.Ability, 0x6D9Eu, 1423, "P7 Dragon King"), new(TimelineSyncEventType.Ability, 0x6D93u, 1432, "P7 Dragon King"),
        new(TimelineSyncEventType.Ability, 0x6D9Eu, 1446, "P7 Dragon King"), new(TimelineSyncEventType.Ability, 0x6D9Eu, 1450, "P7 Dragon King"), new(TimelineSyncEventType.Ability, 0x6E2Eu, 1463, "P7 Dragon King"),
        new(TimelineSyncEventType.Ability, 0x6E2Fu, 1466, "P7 Dragon King"), new(TimelineSyncEventType.Ability, 0x6E2Fu, 1469, "P7 Dragon King"),
    ];

    private static readonly Anchor[] TOP =
    [
        new(TimelineSyncEventType.CastStart, 0x7B03u, 11, "P1 Omega"), new(TimelineSyncEventType.CastStart, 0x7B07u, 21, "P1 Omega"), new(TimelineSyncEventType.Ability, 0x7B07u, 29, "P1 Omega"),
        new(TimelineSyncEventType.CastStart, 0x7B0Bu, 64, "P1 Omega"), new(TimelineSyncEventType.CastStart, 0x7AF8u, 123, "P1 Omega"), new(TimelineSyncEventType.Ability, 0x7B1Bu, 241, "P2 M/F"),
        new(TimelineSyncEventType.Ability, 0x7B1Fu, 251, "P2 M/F"), new(TimelineSyncEventType.Ability, 0x7B42u, 286, "P2 M/F"), new(TimelineSyncEventType.Ability, 0x7B4Au, 409, "P3 Reconfigured"),
        new(TimelineSyncEventType.Ability, 0x7B4Bu, 412, "P3 Reconfigured"), new(TimelineSyncEventType.CastStart, 0x7B55u, 436, "P3 Reconfigured"), new(TimelineSyncEventType.CastStart, 0x7B6Fu, 455, "P3 Reconfigured"),
        new(TimelineSyncEventType.CastStart, 0x7B6Fu, 476, "P3 Reconfigured"), new(TimelineSyncEventType.CastStart, 0x7B6Fu, 498, "P3 Reconfigured"), new(TimelineSyncEventType.CastStart, 0x7B6Fu, 519, "P3 Reconfigured"),
        new(TimelineSyncEventType.CastStart, 0x7B64u, 542, "P3 Reconfigured"), new(TimelineSyncEventType.Ability, 0x7B46u, 560, "P3 Reconfigured"), new(TimelineSyncEventType.CastStart, 0x7B48u, 577, "P3 Reconfigured"),
        new(TimelineSyncEventType.Ability, 0x7B7Au, 607, "P4 Blue Screen"), new(TimelineSyncEventType.Ability, 0x7B46u, 615, "P4 Blue Screen"), new(TimelineSyncEventType.Ability, 0x5779u, 619, "P4 Blue Screen"),
        new(TimelineSyncEventType.Ability, 0x5779u, 629, "P4 Blue Screen"), new(TimelineSyncEventType.Ability, 0x5779u, 639, "P4 Blue Screen"), new(TimelineSyncEventType.Ability, 0x7B86u, 706, "P5 Delta"),
        new(TimelineSyncEventType.Ability, 0x7B85u, 706, "P5 Delta"), new(TimelineSyncEventType.Ability, 0x7B42u, 734, "P5 Delta"), new(TimelineSyncEventType.Ability, 0x7B42u, 816, "P5 Delta"),
        new(TimelineSyncEventType.Ability, 0x7B14u, 840, "P5 Sigma"), new(TimelineSyncEventType.Ability, 0x7B16u, 841, "P5 Sigma"), new(TimelineSyncEventType.Ability, 0x7F30u, 845, "P5 Sigma"),
        new(TimelineSyncEventType.Ability, 0x7B15u, 849, "P5 Sigma"), new(TimelineSyncEventType.Ability, 0x7B20u, 849, "P5 Sigma"), new(TimelineSyncEventType.Ability, 0x7B43u, 851, "P5 Sigma"),
        new(TimelineSyncEventType.Ability, 0x7C02u, 887, "P5 Sigma"), new(TimelineSyncEventType.Ability, 0x7B43u, 905, "P5 Sigma"), new(TimelineSyncEventType.Ability, 0x7B43u, 988, "P5 Omega"),
        new(TimelineSyncEventType.Ability, 0x7C03u, 1172, "P6 Alpha Omega"), new(TimelineSyncEventType.Ability, 0x7C03u, 1175, "P6 Alpha Omega"), new(TimelineSyncEventType.Ability, 0x7C03u, 1206, "P6 Alpha Omega"),
        new(TimelineSyncEventType.Ability, 0x7C03u, 1209, "P6 Alpha Omega"), new(TimelineSyncEventType.Ability, 0x7BA9u, 1244, "P6 Alpha Omega"), new(TimelineSyncEventType.Ability, 0x7C03u, 1248, "P6 Alpha Omega"),
        new(TimelineSyncEventType.Ability, 0x7C03u, 1252, "P6 Alpha Omega"), new(TimelineSyncEventType.Ability, 0x7BA9u, 1278, "P6 Alpha Omega"), new(TimelineSyncEventType.Ability, 0x7C03u, 1282, "P6 Alpha Omega"),
        new(TimelineSyncEventType.Ability, 0x7C03u, 1286, "P6 Alpha Omega"), new(TimelineSyncEventType.Ability, 0x7C03u, 1319, "P6 Alpha Omega"), new(TimelineSyncEventType.Ability, 0x7C03u, 1322, "P6 Alpha Omega"),
        new(TimelineSyncEventType.CastStart, 0x7BB6u, 1362, "P6 Alpha Omega"), new(TimelineSyncEventType.CastStart, 0x7BB6u, 1378, "P6 Alpha Omega"), new(TimelineSyncEventType.CastStart, 0x7BA0u, 1392, "P6 Alpha Omega"),
    ];

    private static readonly Anchor[] FRU =
    [
        new(TimelineSyncEventType.Ability, 0x9CDDu, 50, "P1 Fatebreaker"), new(TimelineSyncEventType.CastStart, 0x9CC0u, 151, "P1 Fatebreaker"), new(TimelineSyncEventType.Ability, 0x9CB5u, 431, "P3 Oracle of Darkness"),
        new(TimelineSyncEventType.Ability, 0x9CB5u, 540, "P3 Oracle of Darkness"), new(TimelineSyncEventType.Ability, 0x9CEFu, 606, "P4 Enter the Dragon"), new(TimelineSyncEventType.Ability, 0x9CEFu, 691, "P4 Enter the Dragon"),
        new(TimelineSyncEventType.Ability, 0x9CEFu, 698, "P4 Enter the Dragon"), new(TimelineSyncEventType.CastStart, 0x9D88u, 1071, "P5 Pandora"),
    ];

    private static readonly Anchor[] DMU =
    [
        new(TimelineSyncEventType.CastStart, 0xC403u, 11, "P1 Kefka"), new(TimelineSyncEventType.Ability, 0xC554u, 169, "P1 Kefka"), new(TimelineSyncEventType.Ability, 0xC555u, 178, "P1 Kefka"),
        new(TimelineSyncEventType.Ability, 0xC554u, 435, "P3 Chaos & Exdeath"), new(TimelineSyncEventType.Ability, 0xBB09u, 478, "P3 Chaos & Exdeath"),
        new(TimelineSyncEventType.Ability, 0xC555u, 484, "P3 Chaos & Exdeath"), new(TimelineSyncEventType.Ability, 0xBB09u, 537, "P3 Chaos & Exdeath"), new(TimelineSyncEventType.Ability, 0xBB09u, 554, "P3 Chaos & Exdeath"),
        new(TimelineSyncEventType.Ability, 0xBB09u, 595, "P3 Chaos & Exdeath"), new(TimelineSyncEventType.Ability, 0xBB09u, 637, "P3 Chaos & Exdeath"), new(TimelineSyncEventType.Ability, 0xC533u, 692, "P3 Chaos & Exdeath"),
        new(TimelineSyncEventType.Ability, 0xC554u, 795, "P4 Kefka Says"), new(TimelineSyncEventType.Ability, 0xC555u, 809, "P4 Kefka Says"), new(TimelineSyncEventType.Ability, 0xC652u, 916, "P5 Kefka Reimagined"),
        new(TimelineSyncEventType.Ability, 0xC652u, 920, "P5 Kefka Reimagined"), new(TimelineSyncEventType.Ability, 0xC652u, 923, "P5 Kefka Reimagined"), new(TimelineSyncEventType.Ability, 0xC652u, 953, "P5 Kefka Reimagined"),
        new(TimelineSyncEventType.Ability, 0xC652u, 956, "P5 Kefka Reimagined"), new(TimelineSyncEventType.Ability, 0xC652u, 999, "P5 Kefka Reimagined"), new(TimelineSyncEventType.Ability, 0xC652u, 1002, "P5 Kefka Reimagined"),
        new(TimelineSyncEventType.Ability, 0xBB3Cu, 1010, "P5 Kefka Reimagined"), new(TimelineSyncEventType.Ability, 0xBB3Cu, 1012, "P5 Kefka Reimagined"), new(TimelineSyncEventType.Ability, 0xBB3Cu, 1014, "P5 Kefka Reimagined"),
        new(TimelineSyncEventType.Ability, 0xBB3Cu, 1016, "P5 Kefka Reimagined"), new(TimelineSyncEventType.Ability, 0xBB3Cu, 1018, "P5 Kefka Reimagined"), new(TimelineSyncEventType.Ability, 0xC652u, 1044, "P5 Kefka Reimagined"),
        new(TimelineSyncEventType.Ability, 0xC652u, 1047, "P5 Kefka Reimagined"), new(TimelineSyncEventType.Ability, 0xC652u, 1050, "P5 Kefka Reimagined"), new(TimelineSyncEventType.Ability, 0xBABCu, 235, "P2 Forsaken Kefka"),
    ];
}

using System.Runtime.InteropServices;
using GameOffsets.Native;

namespace Augury;

public enum RulesetRow
{
    Augury = 174,
}

public enum AuguryFlag
{
    EffigyUnlocked = 1921,
    TutorialEncounterComplete = 1926,
    AtlasMapMarkerAuguryBoss = 1927,
    EnableAlternateBossBehavior = 1929,
    MoraLineFirst = 1962,
    MoraLineFirstInteractWickerman = 1987,
    MoraWickermanTutorialStartComplete = 1988,
    EffigyVisceraEquipped = 1989,
}

public enum GrimFate
{
    Shaper,
    Elder,
    UberElder,
    Cortex,
    Maven,
    Sirus,
    Eater,
    Exarch,
    Fear,
    Neglect,
    Dread,
}

public enum RewardClass : long
{
    Low = 1,
    High = 2,
}

public static class AuguryPaths
{
    // Coven's Cauldron, StateMachine card_chosen flips 0 -> 1 on pick
    public const string Cauldron = "Metadata/Terrain/Leagues/Augury/Objects/AuguryCardObject";
    // the Fate altar where Fates are invoked on a boss
    public const string Altar = "Metadata/Terrain/Leagues/Augury/Objects/AuguryBossObject";
    // crow stream spawned on a pick; Beam.BeamEnd is the next cauldron, far outside the network bubble
    public const string Beam = "Metadata/Effects/SleepableServerBeamEffect";
    public const string CardChosenState = "card_chosen";
}

public static class ServerDataOffsets
{
    // pointer to the client flag object, see FlagObject
    public const int FlagObject = 0xCC88;
    // std::vector<ClientRuleset*>, every league handler live in this area
    public const int Rulesets = 0xD450;
    // byte[11], one per AuguryPinnacleCards row in GrimFate order, written by ServerData packet 747
    public const int GrimFates = 0xE620;
    public const int GrimFateCount = 11;
}

[StructLayout(LayoutKind.Explicit, Size = 0x10)]
public struct DatRowRef
{
    // pointer to the row inside the loaded table
    [FieldOffset(0x00)] public long Row;
    // owning table, refcounted
    [FieldOffset(0x08)] public long Table;
}

[StructLayout(LayoutKind.Explicit)]
public struct ClientRuleset
{
    // Rulesets.dat row index; 3.30 moved it from +0x08, ExileAPI MechanicHandler.Id still reads +0x08
    [FieldOffset(0x30)] public RulesetRow Id;
    // owning ServerData
    [FieldOffset(0x48)] public long ServerData;
}

[StructLayout(LayoutKind.Explicit, Size = 0x220)]
public struct ClientAugury
{
    [FieldOffset(0x30)] public RulesetRow Id;
    [FieldOffset(0x48)] public long ServerData;
    // std::vector<ListenerEntry*>, entry +0x00 is the listener; the Fates window registers here
    [FieldOffset(0x58)] public StdVector Listeners;
    // bumped on deserialiser opcodes 0 and 4
    [FieldOffset(0x188)] public int ChangeCounter;
    // std::vector<AuguryCard>, opcode 1 appends, opcode 3 replaces
    [FieldOffset(0x190)] public StdVector Collected;
    // std::vector<AuguryCard>, the hand offered on the Fates screen, opcode 0
    [FieldOffset(0x1A8)] public StdVector Hand;
    // std::list head of the unordered_map<AuguryRewardOutcomes row, count>, see TallyNode
    [FieldOffset(0x1C8)] public long TallyHead;
    [FieldOffset(0x1D0)] public long TallySize;
    // how many Fates may be invoked on the boss, opcode 4
    [FieldOffset(0x200)] public int FateLimit;
    // card tier selector; matched the per-card tier on the Fates screen
    [FieldOffset(0x204)] public byte Tier;
    // std::vector<asset handle*>, effects/daemons preloaded for collected Fates' boss empowerments (~22 per card), opcode 1; no positions
    [FieldOffset(0x208)] public StdVector Spawns;
}

[StructLayout(LayoutKind.Explicit, Size = 0x40)]
public struct AuguryCard
{
    // AuguryCards row
    [FieldOffset(0x00)] public DatRowRef Card;
    // AuguryRewardOutcomes rows, unused slots are zero
    [FieldOffset(0x10)] public DatRowRef Outcome1;
    [FieldOffset(0x20)] public DatRowRef Outcome2;
    [FieldOffset(0x30)] public DatRowRef Outcome3;
}

[StructLayout(LayoutKind.Explicit)]
public struct TallyNode
{
    [FieldOffset(0x00)] public long Next;
    [FieldOffset(0x08)] public long Prev;
    // AuguryRewardOutcomes row
    [FieldOffset(0x10)] public DatRowRef Outcome;
    [FieldOffset(0x20)] public int Count;
}

[StructLayout(LayoutKind.Explicit)]
public struct FlagObject
{
    public const int WordSize = 9;

    // sorted vector of {byte word, byte bits[8]}; flag id = word * 64 + bit + 1
    [FieldOffset(0x1D0)] public StdVector Flags;
}

[StructLayout(LayoutKind.Explicit)]
public struct FatesWindow
{
    // the listener registered in ClientAugury.Listeners sits this far into the window
    public const int ListenerOffset = 0x2F0;
    public const int VisibleBit = 0x800;

    // element flags, VisibleBit set while the Fates screen is open
    [FieldOffset(0x1E0)] public int ElementFlags;
    // Confirm button element
    [FieldOffset(0x350)] public long ConfirmButton;
    // element right of the title, likely the info button
    [FieldOffset(0x358)] public long InfoButton;
    // round check button beside Confirm
    [FieldOffset(0x360)] public long CheckButton;
    // std::vector<FateCardWidget*>: the offered hand in pick mode, the top invoke slots in boss mode; unused widgets keep stale cards
    [FieldOffset(0x378)] public StdVector CardWidgets;
    // std::vector<FateCardWidget*>, 6 widgets: the bottom pool of collected Fates in boss mode
    [FieldOffset(0x390)] public StdVector PoolWidgets;
    // reward preview element, child rows per reward
    [FieldOffset(0x3A8)] public long RewardPreview;
    // slot -> hand index, -1 empty
    [FieldOffset(0x3B0)] public int Slot0;
    [FieldOffset(0x3B4)] public int Slot1;
    [FieldOffset(0x3B8)] public int Slot2;
    // std::vector<AuguryCard>, copy of the hand; its length is the live card count
    [FieldOffset(0x3C8)] public StdVector Hand;
    // back pointer to ClientAugury
    [FieldOffset(0x3E0)] public long Ruleset;
    [FieldOffset(0x3F0)] public FatesMode Mode;
    // set by the opener, cleared by Confirm
    [FieldOffset(0x3F4)] public byte OpenPending;
}

public enum FatesMode
{
    Pick = 0,
    Collect = 1,
    Invoke = 2,
    Other = 3,
}

[StructLayout(LayoutKind.Explicit)]
public struct FateCardWidget
{
    // 2 selected, 3 not, 0 untouched since open
    [FieldOffset(0x48C)] public int DisplayState;
    // the card this widget shows
    [FieldOffset(0x498)] public AuguryCard Card;
    // AuguryCardTiers row for this card at the current tier
    [FieldOffset(0x4D8)] public DatRowRef TierRow;
    [FieldOffset(0x4F4)] public int Index;
    // 1 when the same card is placed twice (stacked), set on both slots of the pair
    [FieldOffset(0x4FC)] public byte Stacked;
    // 1 right after a click lands in this slot, 0 again with two placed; not occupancy, use FatesWindow.Slot0..2
    [FieldOffset(0x4FE)] public byte Highlight;
    [FieldOffset(0x4FF)] public byte Selected;
    // within a stacked pair 1 on the slot whose reward is kept and tripled, 0 on the one whose reward is dropped
    [FieldOffset(0x500)] public byte StackPrimary;
    // 1 on invoke slots at or past ClientAugury.FateLimit, drawn as the locked feather panel
    [FieldOffset(0x501)] public byte Locked;
}

[StructLayout(LayoutKind.Explicit, Size = 88)]
public struct AuguryCardRow
{
    // wchar* Id, e.g. Torchbearer
    [FieldOffset(0x00)] public long Id;
    // AuguryFactions row this card belongs to
    [FieldOffset(0x14)] public DatRowRef Faction;
    // wchar* display name
    [FieldOffset(0x24)] public long Name;
    // wchar* card art path
    [FieldOffset(0x2C)] public long Art;
    // client flag set once Mora has voiced this card, AuguryFlag.MoraLineFirst + row
    [FieldOffset(0x54)] public int MoraLineFlag;
}

[StructLayout(LayoutKind.Explicit, Size = 52)]
public struct AuguryFactionRow
{
    // wchar* Id: Savage, Filth or Classic
    [FieldOffset(0x00)] public long Id;
    // wchar* faction icon art path
    [FieldOffset(0x14)] public long Icon;
    // wchar* display name: Wickerlight, Darktongue or Dreadmaiden
    [FieldOffset(0x1C)] public long Name;
}

[StructLayout(LayoutKind.Explicit, Size = 68)]
public struct AuguryCardTierRow
{
    // AuguryCards row
    [FieldOffset(0x00)] public DatRowRef Card;
    // 1 to 3, the feathers on the card
    [FieldOffset(0x10)] public int Tier;
    // wchar* one line under the card name
    [FieldOffset(0x14)] public long Subtitle;
    // Mods row applied to the empowered monster, the downside
    [FieldOffset(0x1C)] public DatRowRef DownsideMod;
    // wchar* hover popup, the downside text inside <augurypopuptext>{...}
    [FieldOffset(0x3C)] public long Popup;
}

[StructLayout(LayoutKind.Explicit, Size = 109)]
public struct AuguryRewardOutcomeRow
{
    // wchar* Id, e.g. CurrencyLow1
    [FieldOffset(0x00)] public long Id;
    [FieldOffset(0x08)] public RewardClass Class;
    // wchar* reward line shown on the card
    [FieldOffset(0x10)] public long Text;
    // area level band this outcome rolls in
    [FieldOffset(0x18)] public int LevelMin;
    [FieldOffset(0x1C)] public int LevelMax;
}

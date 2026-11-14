using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using ExileCore.Shared.Helpers;
using ExileCore.Shared.Interfaces;
using GameOffsets.Native;
using ExileImGui;
using Win = ExileImGui.Windows;
using Vector2 = System.Numerics.Vector2;

namespace Augury;

public class Augury : BaseSettingsPlugin<AugurySettings>
{
    private const int MaxCards = 64;
    private const int MaxListeners = 16;
    private const int MaxRulesets = 512;
    private const int MaxTally = 256;
    private const int MaxFlagWords = 64;

    private static readonly string[] MoraLines =
    {
        "Crowkeeper", "Sanguine", "Incandescent", "Rot", "Dryad", "Deathbringer", "Torchbearer", "Bloodthirsty", "Wildfire",
        "Murderous", "Gravewalker", "Houndmaster", "BlackFrost", "Conjurer", "Golemancer", "Doomsayer", "Avalanche", "Butcher",
        "Alchemist", "Stormcaller", "Tempest", "BloodMoon", "Savage", "Pestilent", "Sadist",
    };

    private static readonly AuguryFlag[] ShownFlags = Enum.GetValues<AuguryFlag>().Where(f => f != AuguryFlag.MoraLineFirst).ToArray();

    private sealed class Card
    {
        public string Name;
        public string[] Outcomes;
        public override string ToString() => $"{Name} [{string.Join(", ", Outcomes)}]";
    }

    private sealed class Reward
    {
        public string Id;
        public string Text;
        public RewardClass Class;
        public int LevelMin;
        public int LevelMax;
    }

    private sealed class FateCard
    {
        public string Id;
        public string Name;
        public string Faction;
        public int Tier;
        public string Subtitle;
        public string Downside;
        public string DownsideMod;
        public List<Reward> Rewards = new();
        public bool Selected;
        public override string ToString() => $"{Name} T{Tier} [{DownsideMod}] -> {string.Join(", ", Rewards.Select(r => r.Id))}{(Selected ? " *" : "")}";
    }

    private sealed class Snapshot
    {
        public long ServerData;
        public long Handler;
        public ClientAugury Augury;
        public long Window;
        public bool WindowVisible;
        public FatesMode Mode;
        public List<FateCard> Fates = new();
        public List<Card> Hand = new();
        public List<Card> Collected = new();
        public List<(string outcome, int count)> Tally = new();
        public byte[] GrimFates = new byte[ServerDataOffsets.GrimFateCount];
        public HashSet<int> SetFlags = new();
        public List<string> Viscera = new();
        public string Error;
    }

    private readonly Stopwatch _poll = Stopwatch.StartNew();
    private readonly Dictionary<string, string> _last = new();
    private readonly List<string> _log = new();
    private readonly Dictionary<long, string> _rowNames = new();
    private Snapshot _snap = new();
    private string _logPath;
    private string _cardsPath;
    private string _zoneFactions;
    private DateTime _zoneScanDue = DateTime.MaxValue;
    private string _handSig;
    private int _handSeq;
    private List<FateCard> _offered = new();
    private int _collectedCount = -1;
    private const float BeamOnCauldronGrid = 30f;
    private const float PickBeamGrid = 20f;
    private readonly Dictionary<uint, (string kind, Vector2 grid, int chosen)> _mapObjects = new();
    private readonly List<(Vector2 from, Vector2 to)> _beamTargets = new();
    private readonly HashSet<uint> _beamsSeen = new();
    private readonly HashSet<uint> _beamsIgnoredLogged = new();
    private readonly Dictionary<uint, DateTime> _beamBorn = new();
    private Func<Vector2, Vector2> _minimapToScreen;
    private int _minimapProbe;
    private const string MinimapOverlayKey = "Augury";
    private Vector2 _pickWatchAt;
    private DateTime _pickWatchUntil = DateTime.MinValue;
    private readonly HashSet<uint> _pickWatchSeen = new();
    private const uint TargetColorU32 = 0xFFFF40FF;

    private static readonly (string prefix, string faction)[] FactionMonsters =
    {
        ("Metadata/Monsters/Augury/Cenobite", "Darktongue"),
        ("Metadata/Monsters/Augury/CauldronCrone", "Darktongue"),
        ("Metadata/Monsters/Augury/SimulacrumGolem", "Darktongue"),
        ("Metadata/Monsters/Augury/WitchFamiliar", "Darktongue"),
        ("Metadata/Monsters/Augury/Savage", "Wickerlight"),
        ("Metadata/Monsters/Augury/PaleHag", "Dreadmaiden"),
        ("Metadata/Monsters/Augury/AuguryScreecher", "Dreadmaiden"),
        ("Metadata/Monsters/Augury/SpinningWheelHag", "Dreadmaiden"),
        ("Metadata/Monsters/Augury/AuguryUrchin", "Dreadmaiden"),
    };

    private const string CardsHeader = "time,area,areaLevel,zoneFactions,event,hand,slot,card,cardFaction,tier,downsideMod,reward,rewardClass,rewardLevelMin,rewardLevelMax";

    private IMemory Mem => GameController.Memory;

    public override bool Initialise()
    {
        _logPath = Path.Combine(ConfigDirectory, "augury-log.txt");
        _cardsPath = Path.Combine(ConfigDirectory, "augury-cards.csv");
        _zoneScanDue = DateTime.Now.AddSeconds(2);
        HookMinimap();
        return true;
    }

    public override void OnUnload()
    {
        GameController?.PluginBridge?.GetMethod<Action<string, Action>>("ExileMinimap.RegisterPanelOverlay")?.Invoke(MinimapOverlayKey, null);
        base.OnUnload();
    }

    private void HookMinimap()
    {
        var bridge = GameController?.PluginBridge;
        _minimapToScreen = bridge?.GetMethod<Func<Vector2, Vector2>>("ExileMinimap.GridToScreen");
        var register = bridge?.GetMethod<Action<string, Action>>("ExileMinimap.RegisterPanelOverlay");
        if (_minimapToScreen == null || register == null)
        {
            _minimapToScreen = null;
            return;
        }

        register(MinimapOverlayKey, DrawOnMinimap);
    }

    private IEnumerable<Vector2> PendingTargets() =>
        _beamTargets.Select(t => t.to).Where(to => !_mapObjects.Values.Any(o => Vector2.Distance(o.grid, to) <= BeamOnCauldronGrid));

    private void DrawOnMinimap()
    {
        if (!Settings.Enable || !Settings.DrawBeamTargets || _minimapToScreen == null) return;
        var dl = ImGui.GetBackgroundDrawList();
        foreach (var to in PendingTargets())
        {
            var p = _minimapToScreen(to);
            if (float.IsNaN(p.X)) return;
            dl.AddCircle(p, 9f, TargetColorU32, 0, 3f);
            dl.AddCircleFilled(p, 3f, TargetColorU32);
        }
    }

    private void DrawOnLargeMap()
    {
        if (!Settings.DrawBeamTargets || _minimapToScreen != null) return;
        if (GameController.IngameState?.IngameUi?.Map?.LargeMap?.IsVisible != true) return;
        foreach (var to in PendingTargets())
            Graphics.DrawCircleOnMap(to, true, 8f, SharpDX.Color.Magenta, 3f, 24, VisibleSubMap.Large);
    }

    public override void AreaChange(AreaInstance area)
    {
        _rowNames.Clear();
        _zoneFactions = null;
        _zoneScanDue = DateTime.Now.AddSeconds(2);
        _handSig = null;
        _offered = new List<FateCard>();
        _collectedCount = -1;
        _mapObjects.Clear();
        _beamTargets.Clear();
        _beamsSeen.Clear();
        _beamsIgnoredLogged.Clear();
        _beamBorn.Clear();
        Log($"area -> {area?.Name}");
    }

    public override Job Tick()
    {
        if (_poll.ElapsedMilliseconds < Settings.PollMs) return null;
        _poll.Restart();
        _snap = Read();
        Diff(_snap);
        if (Settings.LogCards) LogCards(_snap);
        TrackMap();
        return null;
    }

    private void TrackMap()
    {
        var beams = new List<Entity>();
        var aliveBeams = new HashSet<uint>();
        foreach (var e in GameController.Entities)
        {
            if (e == null || !e.IsValid) continue;
            if (e.Path == AuguryPaths.Beam)
            {
                aliveBeams.Add(e.Id);
                _beamBorn.TryAdd(e.Id, DateTime.Now);
                if (!_beamsSeen.Contains(e.Id)) beams.Add(e);
                continue;
            }

            var kind = e.Path == AuguryPaths.Cauldron ? "cauldron" : e.Path == AuguryPaths.Altar ? "altar" : null;
            if (kind == null) continue;
            var chosen = e.GetComponent<StateMachine>()?.States?.FirstOrDefault(st => st.Name == AuguryPaths.CardChosenState)?.Value ?? -1;
            var grid = e.GridPosNum;
            if (e.CacheComp.TryGetValue("Guarded", out var guardedAddr) && guardedAddr != 0)
                Track($"guarded {kind} {grid.X:F0},{grid.Y:F0}", Mem.Read<GuardedComponent>(guardedAddr).IsGuarded.ToString());
            if (!_mapObjects.TryGetValue(e.Id, out var old)) Log($"map: {kind} at {grid.X:F0},{grid.Y:F0}{(chosen >= 0 ? $" card_chosen={chosen}" : "")}");
            else if (old.chosen != chosen)
            {
                Log($"map: {kind} at {grid.X:F0},{grid.Y:F0} card_chosen {old.chosen} -> {chosen}");
                _pickWatchAt = grid;
                _pickWatchUntil = DateTime.Now.AddSeconds(15);
                _pickWatchSeen.Clear();
                foreach (var near in GameController.Entities)
                    if (near != null && Vector2.Distance(near.GridPosNum, grid) <= 80f) _pickWatchSeen.Add(near.Id);
            }
            _mapObjects[e.Id] = (kind, grid, (int)chosen);
        }

        foreach (var id in _beamBorn.Keys.Where(id => !aliveBeams.Contains(id)).ToList())
        {
            Log($"map: beam id={id} despawned after {(DateTime.Now - _beamBorn[id]).TotalSeconds:F1}s (+-{Settings.PollMs / 1000.0:F1}s, wall clock, pauses count)");
            _beamBorn.Remove(id);
        }

        if (DateTime.Now <= _pickWatchUntil)
        {
            foreach (var e in GameController.Entities)
            {
                if (e == null || !e.IsValid || _pickWatchSeen.Contains(e.Id) || Vector2.Distance(e.GridPosNum, _pickWatchAt) > 80f) continue;
                _pickWatchSeen.Add(e.Id);
                var beam = e.GetComponent<Beam>();
                var end = beam == null ? "" : $" beamEnd {beam.BeamEndNum.X * PoeMapExtension.WorldToGridConversion:F0},{beam.BeamEndNum.Y * PoeMapExtension.WorldToGridConversion:F0}";
                Log($"pick-watch: new {e.Path} id={e.Id} at {e.GridPosNum.X:F0},{e.GridPosNum.Y:F0} [{string.Join(",", e.CacheComp.Keys)}]{end}");
            }
        }

        foreach (var b in beams)
        {
            var beam = b.GetComponent<Beam>();
            if (beam == null) continue;
            var from = new Vector2(beam.BeamStartNum.X, beam.BeamStartNum.Y) * PoeMapExtension.WorldToGridConversion;
            var to = new Vector2(beam.BeamEndNum.X, beam.BeamEndNum.Y) * PoeMapExtension.WorldToGridConversion;
            var inPickWindow = DateTime.Now <= _pickWatchUntil && Vector2.Distance(_pickWatchAt, from) <= PickBeamGrid;
            if (!inPickWindow)
            {
                var onCauldron = _mapObjects.Values.Any(o => o.kind == "cauldron" && Vector2.Distance(o.grid, from) <= BeamOnCauldronGrid);
                if (!onCauldron) _beamsSeen.Add(b.Id);
                if (_beamsIgnoredLogged.Add(b.Id))
                    Log($"map: ignored beam {from.X:F0},{from.Y:F0} -> {to.X:F0},{to.Y:F0} ({(onCauldron ? "at a cauldron but not after a pick" : "not on a cauldron")})");
                continue;
            }

            _beamsSeen.Add(b.Id);
            _beamTargets.Add((from, to));
            Log($"map: cauldron beam {from.X:F0},{from.Y:F0} -> next target at {to.X:F0},{to.Y:F0} ({Vector2.Distance(from, to):F0} grid)");
        }
    }

    private void LogCards(Snapshot s)
    {
        if (_zoneFactions == null && DateTime.Now >= _zoneScanDue)
        {
            _zoneFactions = ScanZoneFactions();
            WriteCardRow("zone", 0, 0, null, null);
        }

        if (s.Hand.Count == 0) _handSig = null;
        if (s.Hand.Count > 0 && s.WindowVisible && s.Mode != FatesMode.Invoke && s.Fates.Count == s.Hand.Count)
        {
            var sig = string.Join("|", s.Fates.Select(f => f.Id + "/" + string.Join("+", f.Rewards.Select(r => r.Id))));
            if (sig != _handSig)
            {
                _handSig = sig;
                _handSeq++;
                _offered = s.Fates;
                for (var i = 0; i < s.Fates.Count; i++) WriteFate("offered", i, s.Fates[i]);
            }
        }

        if (s.Handler == 0) return;
        if (_collectedCount >= 0 && s.Collected.Count > _collectedCount)
        {
            foreach (var c in s.Collected.Skip(_collectedCount))
            {
                var slot = _offered.FindIndex(f => f.Id == c.Name && (f.Rewards.FirstOrDefault()?.Id ?? "-") == c.Outcomes[0]);
                if (slot >= 0) WriteFate("picked", slot, _offered[slot]);
                else WriteCardRow("carried", -1, 0, null, null, c.Name, rewardId: c.Outcomes[0]);
            }
        }

        _collectedCount = s.Collected.Count;
    }

    private void WriteFate(string evt, int slot, FateCard f)
    {
        if (f.Rewards.Count == 0) WriteCardRow(evt, slot, f.Tier, f, null);
        foreach (var r in f.Rewards) WriteCardRow(evt, slot, f.Tier, f, r);
    }

    private void WriteCardRow(string evt, int slot, int tier, FateCard f, Reward r, string cardId = null, string rewardId = null)
    {
        if (_cardsPath == null) return;
        var area = GameController.Area?.CurrentArea;
        var fields = new object[]
        {
            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), area?.Name, area?.RealLevel, _zoneFactions, evt, evt == "zone" ? "" : _handSeq.ToString(),
            evt == "zone" ? "" : slot.ToString(), f?.Name ?? cardId, f?.Faction, tier == 0 ? "" : tier.ToString(), f?.DownsideMod,
            r?.Id ?? rewardId, r == null ? "" : ((int)r.Class).ToString(), r?.LevelMin, r?.LevelMax,
        };
        var line = string.Join(",", fields.Select(v => Csv(v?.ToString())));
        try
        {
            if (!File.Exists(_cardsPath)) File.WriteAllText(_cardsPath, CardsHeader + Environment.NewLine);
            File.AppendAllText(_cardsPath, line + Environment.NewLine);
        }
        catch (IOException) { }
    }

    private static string Csv(string v)
    {
        if (string.IsNullOrEmpty(v)) return "";
        return v.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0 ? v : "\"" + v.Replace("\"", "\"\"") + "\"";
    }

    private string ScanZoneFactions()
    {
        try
        {
            var change = GameController.Game.AreaChangeCount;
            var found = new SortedSet<string>();
            foreach (var file in new FilesFromMemory(Mem).GetAllFiles())
            {
                if (file.Value.ChangeCount != change) continue;
                foreach (var (prefix, faction) in FactionMonsters)
                    if (file.Key.StartsWith(prefix, StringComparison.Ordinal))
                        found.Add(faction);
            }

            var result = found.Count == 0 ? "none" : string.Join("+", found);
            Log($"zone factions: {result}");
            return result;
        }
        catch (Exception e)
        {
            Log($"zone faction scan failed: {e.Message}");
            return "error";
        }
    }

    private Snapshot Read()
    {
        var s = new Snapshot();
        try
        {
            s.ServerData = GameController.IngameState?.ServerData?.Address ?? 0;
            if (s.ServerData == 0) return Fail(s, "no ServerData");

            s.Handler = FindRuleset(s.ServerData, RulesetRow.Augury);
            s.GrimFates = Mem.ReadBytes(s.ServerData + ServerDataOffsets.GrimFates, ServerDataOffsets.GrimFateCount);
            ReadFlags(s);
            ReadViscera(s);

            if (s.Handler == 0) return Fail(s, $"ruleset {(int)RulesetRow.Augury} not in ServerData+0x{ServerDataOffsets.Rulesets:X}");
            s.Augury = Mem.Read<ClientAugury>(s.Handler);
            if (s.Augury.ServerData != s.ServerData) s.Error = "ClientAugury.ServerData mismatch";

            s.Collected = ReadCards(s.Augury.Collected);
            s.Hand = ReadCards(s.Augury.Hand);
            s.Tally = ReadTally(s.Augury);
            ReadFates(s);
        }
        catch (Exception e)
        {
            s.Error = e.Message;
        }

        return s;
    }

    private static Snapshot Fail(Snapshot s, string why)
    {
        s.Error ??= why;
        return s;
    }

    private long FindRuleset(long serverData, RulesetRow id)
    {
        var rulesets = Mem.Read<StdVector>(serverData + ServerDataOffsets.Rulesets);
        if (!Sane(rulesets, 8, MaxRulesets)) return 0;
        foreach (var r in Mem.ReadStdVector<long>(rulesets))
            if (r != 0 && Mem.Read<ClientRuleset>(r).Id == id)
                return r;
        return 0;
    }

    private static bool Sane(StdVector v, int elementSize, int max)
    {
        var bytes = v.Last - v.First;
        return v.First > 0 && bytes > 0 && bytes % elementSize == 0 && bytes / elementSize <= max;
    }

    private List<Card> ReadCards(StdVector vec)
    {
        var list = new List<Card>();
        if (!Sane(vec, 0x40, MaxCards)) return list;
        foreach (var c in Mem.ReadStdVector<AuguryCard>(vec))
            list.Add(new Card { Name = RowName(c.Card.Row), Outcomes = new[] { RowName(c.Outcome1.Row), RowName(c.Outcome2.Row), RowName(c.Outcome3.Row) } });
        return list;
    }

    private List<(string, int)> ReadTally(ClientAugury augury)
    {
        var list = new List<(string, int)>();
        if (augury.TallyHead == 0 || augury.TallySize is <= 0 or > MaxTally) return list;
        var node = Mem.Read<TallyNode>(augury.TallyHead).Next;
        for (var i = 0; i < augury.TallySize && node != 0 && node != augury.TallyHead; i++)
        {
            var n = Mem.Read<TallyNode>(node);
            list.Add((RowName(n.Outcome.Row), n.Count));
            node = n.Next;
        }

        return list;
    }

    private void ReadFates(Snapshot s)
    {
        if (!Sane(s.Augury.Listeners, 8, MaxListeners)) return;
        foreach (var entry in Mem.ReadStdVector<long>(s.Augury.Listeners))
        {
            var listener = entry == 0 ? 0 : Mem.Read<long>(entry);
            if (listener == 0) continue;
            var candidate = listener - FatesWindow.ListenerOffset;
            if (Mem.Read<FatesWindow>(candidate).Ruleset != s.Handler) continue;
            s.Window = candidate;
            break;
        }

        if (s.Window == 0) return;
        var window = Mem.Read<FatesWindow>(s.Window);
        s.WindowVisible = (window.ElementFlags & FatesWindow.VisibleBit) != 0;
        s.Mode = window.Mode;
        if (!Sane(window.Hand, 0x40, MaxCards) || !Sane(window.CardWidgets, 8, MaxCards)) return;
        var count = (int)((window.Hand.Last - window.Hand.First) / 0x40);
        var widgets = Mem.ReadStdVector<long>(window.CardWidgets);
        for (var i = 0; i < Math.Min(count, widgets.Length); i++)
        {
            var widget = Mem.Read<FateCardWidget>(widgets[i]);
            if (widget.Card.Card.Row != 0) s.Fates.Add(ReadFate(widget));
        }
    }

    private FateCard ReadFate(FateCardWidget widget)
    {
        var card = Mem.Read<AuguryCardRow>(widget.Card.Card.Row);
        var fate = new FateCard
        {
            Id = ReadText(card.Id),
            Name = ReadText(card.Name),
            Faction = card.Faction.Row == 0 ? null : ReadText(Mem.Read<AuguryFactionRow>(card.Faction.Row).Name),
            Selected = widget.Selected != 0,
        };
        if (widget.TierRow.Row != 0)
        {
            var tier = Mem.Read<AuguryCardTierRow>(widget.TierRow.Row);
            fate.Tier = tier.Tier;
            fate.Subtitle = ReadText(tier.Subtitle);
            fate.Downside = PopupBody(ReadText(tier.Popup, 2048));
            fate.DownsideMod = RowName(tier.DownsideMod.Row);
        }

        foreach (var outcome in new[] { widget.Card.Outcome1, widget.Card.Outcome2, widget.Card.Outcome3 })
        {
            if (outcome.Row == 0) continue;
            var row = Mem.Read<AuguryRewardOutcomeRow>(outcome.Row);
            fate.Rewards.Add(new Reward
            {
                Id = ReadText(row.Id),
                Text = ReadText(row.Text),
                Class = row.Class,
                LevelMin = row.LevelMin,
                LevelMax = row.LevelMax,
            });
        }

        return fate;
    }

    private static string PopupBody(string popup)
    {
        if (popup == null) return null;
        const string tag = "<augurypopuptext>{";
        var i = popup.IndexOf(tag, StringComparison.Ordinal);
        if (i < 0) return popup;
        var body = popup[(i + tag.Length)..];
        return body.EndsWith('}') ? body[..^1] : body;
    }

    private void ReadFlags(Snapshot s)
    {
        var obj = Mem.Read<long>(s.ServerData + ServerDataOffsets.FlagObject);
        if (obj == 0) return;
        var flags = Mem.Read<FlagObject>(obj).Flags;
        if (!Sane(flags, FlagObject.WordSize, MaxFlagWords)) return;
        var raw = Mem.ReadBytes(flags.First, (int)(flags.Last - flags.First));
        for (var i = 0; i + FlagObject.WordSize <= raw.Length; i += FlagObject.WordSize)
        for (var bit = 0; bit < 64; bit++)
            if ((raw[i + 1 + (bit >> 3)] & (1 << (bit & 7))) != 0)
                s.SetFlags.Add(raw[i] * 64 + bit + 1);
    }

    private void ReadViscera(Snapshot s)
    {
        foreach (var holder in GameController.IngameState.ServerData.PlayerInventories)
        {
            var items = holder?.Inventory?.Items;
            if (items == null) continue;
            foreach (var item in items)
                if (item?.Path?.Contains("/Viscera/") == true)
                    s.Viscera.Add($"inv {holder.Id}: {item.Path.Split('/').Last()}");
        }
    }

    private string RowName(long row)
    {
        if (row == 0) return "-";
        if (_rowNames.TryGetValue(row, out var name)) return name;
        name = ReadText(Mem.Read<long>(row));
        if (string.IsNullOrEmpty(name) || name.Any(c => c < 32 || c > 0x7E)) return $"0x{row:X}";
        _rowNames[row] = name;
        return name;
    }

    private string ReadText(long ptr, int maxBytes = 512)
    {
        if (ptr == 0) return null;
        var raw = Mem.ReadBytes(ptr, maxBytes);
        var sb = new StringBuilder();
        for (var i = 0; i + 1 < raw.Length; i += 2)
        {
            var c = (char)(raw[i] | raw[i + 1] << 8);
            if (c == 0) break;
            if (c != '\r') sb.Append(c);
        }

        return sb.ToString();
    }

    private void Diff(Snapshot s)
    {
        var a = s.Augury;
        Track("error", s.Error ?? "");
        Track("handler", s.Handler == 0 ? "missing" : $"0x{s.Handler:X}");
        Track("changeCounter", a.ChangeCounter.ToString());
        Track("fatesWindow", s.Window == 0 ? "missing" : $"0x{s.Window:X} {(s.WindowVisible ? "open" : "closed")}");
        Track("fates", string.Join(" | ", s.Fates));
        Track("hand", string.Join(" | ", s.Hand));
        Track("collected", string.Join(" | ", s.Collected));
        Track("tally", string.Join(", ", s.Tally.Select(t => $"{t.outcome}={t.count}")));
        Track("fateLimit", a.FateLimit.ToString());
        Track("tier", a.Tier.ToString());
        Track("spawns", (Sane(a.Spawns, 8, MaxCards) ? (a.Spawns.Last - a.Spawns.First) / 8 : 0).ToString());
        Track("grimFates", string.Join(",", s.GrimFates.Select((v, i) => v == 0 ? null : $"{(GrimFate)i}={v}").Where(x => x != null)));
        Track("flags", string.Join(",", s.SetFlags.Where(f => f >= (int)AuguryFlag.EffigyUnlocked).OrderBy(f => f)));
        Track("viscera", string.Join(" | ", s.Viscera));
    }

    private void Track(string key, string value)
    {
        if (_last.TryGetValue(key, out var old) && old == value) return;
        var first = !_last.ContainsKey(key);
        _last[key] = value;
        if (first && value.Length == 0) return;
        Log($"{key}: {(first ? "" : $"{old} -> ")}{value}");
    }

    private void Log(string line)
    {
        var stamped = $"{DateTime.Now:HH:mm:ss.fff} {line}";
        _log.Add(stamped);
        if (_log.Count > Settings.MaxLogLines) _log.RemoveRange(0, _log.Count - Settings.MaxLogLines);
        if (!Settings.LogToFile || _logPath == null) return;
        try { File.AppendAllText(_logPath, stamped + Environment.NewLine); }
        catch (IOException) { }
    }

    public override void Render()
    {
        if (++_minimapProbe >= 30)
        {
            _minimapProbe = 0;
            HookMinimap();
        }

        DrawOnLargeMap();
        if (!Settings.ShowWindow) return;
        var open = Settings.ShowWindow;
        if (Win.Begin("Augury", "augury_main", ref open, 0, new Vector2(520, 640))) DrawBody();
        Win.End();
        Settings.ShowWindow = open;
    }

    public override void DrawSettings()
    {
        base.DrawSettings();
        ImGui.Checkbox("Show window", ref Settings.ShowWindow);
        ImGui.Checkbox("Log changes to file", ref Settings.LogToFile);
        ImGui.Checkbox("Log every offered/picked card to CSV", ref Settings.LogCards);
        ImGui.Checkbox("Draw unreached beam targets (ExileMinimap, else large map)", ref Settings.DrawBeamTargets);
        ImGui.TextUnformatted(_minimapToScreen != null ? "drawing on: ExileMinimap" : "drawing on: large map");
        ImGui.TextUnformatted(_cardsPath ?? "");
        ImGui.InputInt("Poll ms", ref Settings.PollMs);
        Settings.PollMs = Math.Clamp(Settings.PollMs, 50, 5000);
        ImGui.TextUnformatted(_logPath ?? "");
    }

    private void DrawBody()
    {
        var s = _snap;
        var a = s.Augury;
        ImGui.TextUnformatted($"ServerData 0x{s.ServerData:X}   ClientAugury {(s.Handler == 0 ? "missing" : $"0x{s.Handler:X}")}");
        if (s.Error != null) ImGui.TextColored(new System.Numerics.Vector4(1, 0.4f, 0.4f, 1), Esc(s.Error));
        ImGui.TextUnformatted($"changes {a.ChangeCounter}   fate limit {a.FateLimit}   tier {a.Tier}");

        Win.TabBar("augury_tabs",
            ("Fates", () => { DrawFates(s); return false; }),
            ("Cards", () => { DrawCards("Hand", s.Hand); DrawCards("Collected", s.Collected); DrawTally(s); return false; }),
            ("Map", () => { DrawMap(); return false; }),
            ("Grim Fates", () => { DrawGrim(s); return false; }),
            ("Flags", () => { DrawFlags(s); return false; }),
            ("Log", () => { DrawLog(); return false; }));
    }

    private static void DrawCards(string title, List<Card> cards)
    {
        ImGui.SeparatorText($"{title}: {cards.Count}");
        if (cards.Count == 0 || !Tables.Begin(title, new[] { ("Card", 0f), ("Outcome 1", 0f), ("Outcome 2", 0f), ("Outcome 3", 0f) })) return;
        for (var i = 0; i < cards.Count; i++)
        {
            using var _ = Tables.Row(i);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(cards[i].Name);
            foreach (var o in cards[i].Outcomes)
            {
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(o);
            }
        }

        Tables.End();
    }

    private static void DrawFates(Snapshot s)
    {
        ImGui.TextUnformatted(s.Window == 0 ? "Fates window not found" : $"window 0x{s.Window:X} {(s.WindowVisible ? "open" : "closed")}   cards {s.Fates.Count}");
        foreach (var f in s.Fates)
        {
            ImGui.SeparatorText($"{f.Name}  T{f.Tier}  {f.Faction}{(f.Selected ? "  (selected)" : "")}");
            ImGui.TextDisabled(Esc(f.Subtitle ?? ""));
            ImGui.TextUnformatted($"Downside ({f.DownsideMod}):");
            ImGui.Indent();
            ImGui.TextWrapped(Esc(f.Downside ?? "-"));
            ImGui.Unindent();
            foreach (var r in f.Rewards)
            {
                ImGui.TextUnformatted($"Reward {r.Id} ({r.Class}, lvl {r.LevelMin}-{r.LevelMax}):");
                ImGui.Indent();
                ImGui.TextWrapped(Esc(r.Text ?? "-"));
                ImGui.Unindent();
            }
        }
    }

    private void DrawMap()
    {
        ImGui.SeparatorText($"Seen this area: {_mapObjects.Count}");
        foreach (var o in _mapObjects.Values.OrderBy(o => o.kind))
            ImGui.TextUnformatted($"{o.kind,-8} {o.grid.X,5:F0},{o.grid.Y,5:F0}{(o.chosen >= 0 ? $"  card_chosen={o.chosen}" : "")}");
        ImGui.SeparatorText($"Beam targets: {_beamTargets.Count}");
        foreach (var (from, to) in _beamTargets)
        {
            var found = _mapObjects.Values.FirstOrDefault(o => Vector2.Distance(o.grid, to) <= BeamOnCauldronGrid);
            ImGui.TextUnformatted($"{from.X,5:F0},{from.Y,5:F0} -> {to.X,5:F0},{to.Y,5:F0}  {(found.kind == null ? "not reached yet" : "= " + found.kind)}");
        }
    }

    private static void DrawTally(Snapshot s)
    {
        ImGui.SeparatorText($"Outcome tally: {s.Tally.Count}");
        foreach (var (outcome, count) in s.Tally) ImGui.TextUnformatted($"{count,3}  {outcome}");
    }

    private static void DrawGrim(Snapshot s)
    {
        for (var i = 0; i < s.GrimFates.Length; i++) ImGui.TextUnformatted($"{s.GrimFates[i],3}  {(GrimFate)i}");
    }

    private static void DrawFlags(Snapshot s)
    {
        foreach (var f in ShownFlags) ImGui.TextUnformatted($"{(s.SetFlags.Contains((int)f) ? "[x]" : "[ ]")} {(int)f} {f}");
        ImGui.SeparatorText("Mora lines heard");
        for (var i = 0; i < MoraLines.Length; i++)
            if (s.SetFlags.Contains((int)AuguryFlag.MoraLineFirst + i)) ImGui.TextUnformatted($"{(int)AuguryFlag.MoraLineFirst + i} {MoraLines[i]}");
        ImGui.SeparatorText($"Viscera items: {s.Viscera.Count}");
        foreach (var v in s.Viscera) ImGui.TextUnformatted(v);
    }

    private void DrawLog()
    {
        if (ImGui.Button("Clear")) _log.Clear();
        ImGui.SameLine();
        if (ImGui.Button("Copy")) ImGui.SetClipboardText(string.Join(Environment.NewLine, _log));
        ImGui.SameLine();
        if (ImGui.Button("Mark")) Log("---- mark ----");
        if (!ImGui.BeginChild("augury_log", new Vector2(0, 0), ImGuiChildFlags.Borders)) { ImGui.EndChild(); return; }
        foreach (var line in _log) ImGui.TextUnformatted(line);
        if (ImGui.GetScrollY() >= ImGui.GetScrollMaxY()) ImGui.SetScrollHereY(1f);
        ImGui.EndChild();
    }

    private static string Esc(string s) => s.Replace("%", "%%");
}

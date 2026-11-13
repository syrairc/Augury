using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using ExileCore;
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

    private IMemory Mem => GameController.Memory;

    public override bool Initialise()
    {
        _logPath = Path.Combine(ConfigDirectory, "augury-log.txt");
        return true;
    }

    public override void AreaChange(AreaInstance area)
    {
        _rowNames.Clear();
        Log($"area -> {area?.Name}");
    }

    public override Job Tick()
    {
        if (_poll.ElapsedMilliseconds < Settings.PollMs) return null;
        _poll.Restart();
        _snap = Read();
        Diff(_snap);
        return null;
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

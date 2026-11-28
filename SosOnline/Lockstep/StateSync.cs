using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using SosOnline.Game;
using UnityEngine;

namespace SosOnline.Lockstep;

/// <summary>
/// A description of the simulation at one lockstep frame, split into named components (level, party, stats, gold,
/// inventory, combat, NPCs) so a mismatch says what diverged. Party positions are also kept as numbers, for the
/// guests' in-place drift correction.
/// </summary>
internal sealed class StateSnapshot
{
    public int Frame;
    /// <summary>Captured while a level was loading: not compared (scene loads and unloads finish at different times).</summary>
    public bool Loading;
    /// <summary>(component name, value). Same order on every peer.</summary>
    public readonly List<(string key, string value)> Components = new();
    /// <summary>Per-component hashes. A snapshot read from the network has only these, not the values.</summary>
    public readonly List<(string key, uint hash)> Hashes = new();
    public readonly List<(string name, Vector3 pos)> Characters = new();
    /// <summary>Facing of each entry of <see cref="Characters"/> (-1 unknown). Corrected in place like positions, not hashed.</summary>
    public readonly List<int> Angles = new();
    /// <summary>Enemies that entered or left a battle in this snapshot's window (<see cref="SpawnTrace"/>), so a guest names the spawn that differs.</summary>
    public List<(int frame, string what)> Spawns = new();
    /// <summary>
    /// Plain numbers a guest may correct in place when they differ (HP, MP, combo points): (key, value). Sent with the
    /// snapshot, like <see cref="Characters"/>.
    /// </summary>
    public readonly List<(string key, int value)> Numbers = new();

    public static double CaptureMsTotal { get; private set; }
    public static int CaptureCount { get; private set; }
    public static double CaptureMsMax { get; private set; }

    public static StateSnapshot CaptureLocal(int frame)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        // A held level-up start counts as loading: its scene appears on each PC's own frame (LevelUps.Holding).
        var s = new StateSnapshot { Frame = frame, Loading = GameBridge.LoadingLevel || LevelUps.Holding };
        s.Add("lvl", () => GameBridge.CurrentLevelId);
        s.Add("scenes", () => GameBridge.ActiveSceneName);
        s.Add("party", s.CaptureParty);
        s.Add("stats", s.CaptureStats);
        s.Add("gold", CaptureCurrency);
        s.Add("inv", CaptureInventory);
        s.Add("combat", s.CaptureCombat);
        s.Add("hp", s.CaptureHp);
        s.Add("combo", s.CaptureCombo);
        s.Add("npcs", CaptureNpcs);
        s.Add("spawn", () => $"{SpawnTrace.Count}/{SpawnTrace.Hash:x8}");
        s.Spawns = SpawnTrace.Window(frame, StateSync.Interval);
        // Tests: a divergence injected on purpose (--sosonline-hpdiverge) that no in-place correction removes.
        s.Add("test", () => TestHarness.Diverged ? "diverged" : "");
        // The game's own pause (menus, pause screen, questions). One PC stuck in a menu while the other plays on
        // otherwise only shows as endless position corrections (2026-11-15: a save question left open on the host).
        s.Add("pause", () => (Manager<PauseManager>.instance?.isPaused ?? false) ? "on" : "off");
        foreach (var (k, v) in s.Components) s.Hashes.Add((k, Fnv(v)));
        double ms = sw.Elapsed.TotalMilliseconds;
        CaptureMsTotal += ms;
        CaptureCount++;
        if (ms > CaptureMsMax) CaptureMsMax = ms;
        return s;
    }

    private void Add(string key, Func<string> capture)
    {
        string v;
        try { v = capture() ?? ""; }
        catch (Exception e) { v = "!" + e.GetType().Name; }
        Components.Add((key, v));
    }

    private static string P(float f) => f.ToString("F2", CultureInfo.InvariantCulture);
    private static string P1(float f) => f.ToString("F1", CultureInfo.InvariantCulture);

    /// <summary>
    /// Leader and followers, by name. Positions and facing go to <see cref="Characters"/>/<see cref="Angles"/>: the
    /// guest corrects those in place. Facing used to be part of this hash, and a one-step facing difference after a
    /// drop-in forced a full re-sync again and again (2026-11-15).
    /// </summary>
    private string CaptureParty()
    {
        var list = GameBridge.Party?.LeaderAndFollowers;
        if (list == null) return "";
        var parts = new List<string>();
        for (int i = 0; i < list.Count; i++)
        {
            var c = list[i];
            if (c == null) continue;
            int angle = -1;
            try { angle = c.lookDirectionController?.CurrentLookAngle ?? -1; } catch { }
            Characters.Add((c.gameObject.name, c.transform.position));
            Angles.Add(angle);
            parts.Add(c.gameObject.name);
        }
        return string.Join(" ", parts);
    }

    /// <summary>Current party: HP and MP of each member. Numbers: the guest corrects them in place.</summary>
    private string CaptureStats()
    {
        var ids = GameBridge.Party?.CurrentParty;
        var stats = GameBridge.Stats;
        if (ids == null || stats == null) return "";
        var parts = new List<string>();
        for (int i = 0; i < ids.Count; i++)
        {
            var id = ids[i];
            var data = stats.GetCharacterData(id);
            if (data == null) continue;
            parts.Add($"{id.characterId}:{data.currentHP}/{data.currentSP}");
            Numbers.Add(("hp:" + id.characterId, data.currentHP));
            Numbers.Add(("sp:" + id.characterId, data.currentSP));
        }
        return string.Join(",", parts);
    }

    private static string CaptureCurrency()
    {
        var dict = GameBridge.Currency?.currencyQuantities;
        if (dict == null) return "";
        var values = new List<int>();
        foreach (var kv in dict) values.Add(kv.Value);
        values.Sort();
        return string.Join(",", values);
    }

    /// <summary>Kinds of items owned / total quantity.</summary>
    private static string CaptureInventory()
    {
        var dict = GameBridge.Inventory?.ownedInventoryItems?.dictionary;
        if (dict == null) return "";
        int kinds = 0;
        long total = 0;
        foreach (var kv in dict) { kinds++; total += kv.Value; }
        return $"{kinds}/{total}";
    }

    /// <summary>
    /// The battle's structure: whether an encounter runs, every actor by name and slot (dead or not), whose turn it
    /// is and the round, each enemy's casting state, turns to its action and spell locks, and every actor's gameplay
    /// modifiers (statuses and buffs). A difference here can't be patched: the guest re-syncs.
    /// </summary>
    private string CaptureCombat()
    {
        var cm = GameBridge.Combat;
        if (cm == null || !cm.IsAnEncounterRunning()) return "off";
        var enc = cm.CurrentEncounter;
        var actors = enc?.AllActors;
        if (actors == null) return "on";
        var parts = new List<string>();
        for (int i = 0; i < actors.Count; i++)
        {
            var a = actors[i];
            if (a == null) continue;
            string extra = "";
            try { if (a.CombatTarget != null && a.CombatTarget.Dead) extra += "/dead"; } catch { }
            try
            {
                var enemy = a.TryCast<EnemyCombatActor>();
                if (enemy != null)
                {
                    // An enemy charging a spell takes less damage; the PCs once disagreed on it with every HP equal.
                    if (enemy.IsCasting()) extra += "/cast";
                    var cd = enemy.castingData;
                    if (cd != null) extra += $"/t{cd.turnsToAction}l{cd.totalSpellLocks}:{cd.spellLocks?.Count ?? 0}[{CombatTrace.LockDesc(cd, false)}]";
                }
            }
            catch { }
            try
            {
                var mods = a.CombatTarget?.GetGameplayModifiersHandler()?.gameplayModifiers;
                if (mods != null && mods.Count > 0)
                {
                    var names = new List<string>();
                    for (int m = 0; m < mods.Count; m++) if (mods[m] != null) names.Add(mods[m].GetIl2CppType().Name);
                    names.Sort(StringComparer.Ordinal);
                    extra += "/m[" + string.Join("+", names) + "]";
                }
            }
            catch { }
            parts.Add(ActorId(a) + extra);
        }
        string turn = "";
        try
        {
            var ctl = enc.controller;
            if (ctl != null) turn = $" turn={ActorId(ctl.currentTurnMainActor)} round={ctl.roundElapsed}";
        }
        catch { }
        return "on " + string.Join(",", parts) + turn;
    }

    /// <summary>Every combat actor's HP. Numbers: the guest corrects enemies' in place when the battle's structure agrees.</summary>
    private string CaptureHp()
    {
        var cm = GameBridge.Combat;
        if (cm == null || !cm.IsAnEncounterRunning()) return "";
        var actors = cm.CurrentEncounter?.AllActors;
        if (actors == null) return "";
        var parts = new List<string>();
        for (int i = 0; i < actors.Count; i++)
        {
            var a = actors[i];
            if (a == null) continue;
            int hp = -1;
            try { hp = a.CombatTarget?.GetCurrentHP() ?? -1; } catch { }
            string id = ActorId(a);
            parts.Add($"{id}:{hp}");
            // Party members' HP lives in their character data ("stats"); only enemies are corrected from here.
            try { if (hp >= 0 && a.TryCast<EnemyCombatActor>() != null) Numbers.Add(("ehp:" + id, hp)); } catch { }
        }
        return string.Join(",", parts);
    }

    /// <summary>The party's combo points.</summary>
    private string CaptureCombo()
    {
        var stats = GameBridge.Stats;
        if (stats == null) return "";
        int combo = stats.comboPoints;
        Numbers.Add(("combo", combo));
        return combo.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Name and slot of a combat actor ("AcidBugEnemy#3"): two adds of one kind share a name.</summary>
    internal static string ActorId(CombatActor a)
    {
        if (a == null) return "none";
        string name;
        try { name = a.gameObject.name.Replace("PlayableCharacter_", "").Replace("(Clone)", "").Trim(); } catch { name = "?"; }
        int slot = -1;
        try { var sl = a.Slot; if (sl != null) slot = sl.transform.GetSiblingIndex(); } catch { }
        return $"{name}#{slot}";
    }

    /// <summary>Every active NPC or enemy in the level: name and position, sorted.</summary>
    internal static string CaptureNpcs()
    {
        var all = UnityEngine.Object.FindObjectsOfType<NPCController>();
        var parts = new List<string>();
        foreach (var n in all)
        {
            if (n == null || !n.isActiveAndEnabled) continue;
            var p = n.transform.position;
            parts.Add($"{n.gameObject.name}@({P1(p.x)},{P1(p.y)},{P1(p.z)})");
        }
        parts.Sort(StringComparer.Ordinal);
        return string.Join(" ", parts);
    }

    public int AngleAt(int i) => i < Angles.Count ? Angles[i] : -1;

    public int? NumberOf(string key)
    {
        foreach (var (k, v) in Numbers) if (k == key) return v;
        return null;
    }

    public string Get(string key)
    {
        foreach (var (k, v) in Components) if (k == key) return v;
        return null;
    }

    public uint? HashOf(string key)
    {
        foreach (var (k, h) in Hashes) if (k == key) return h;
        return null;
    }

    public void Write(BinaryWriter w)
    {
        w.Write(Frame);
        w.Write(Loading);
        w.Write((byte)Hashes.Count);
        foreach (var (k, h) in Hashes) { w.Write(k); w.Write(h); }
        w.Write((byte)Characters.Count);
        for (int i = 0; i < Characters.Count; i++)
        {
            var (name, pos) = Characters[i];
            w.Write(name);
            w.Write(pos.x); w.Write(pos.y); w.Write(pos.z);
            w.Write((short)AngleAt(i));
        }
        w.Write((byte)Math.Min(Spawns.Count, 255));
        for (int i = 0; i < Spawns.Count && i < 255; i++) { w.Write(Spawns[i].frame); w.Write(Spawns[i].what); }
        w.Write((byte)Math.Min(Numbers.Count, 255));
        for (int i = 0; i < Numbers.Count && i < 255; i++) { w.Write(Numbers[i].key); w.Write(Numbers[i].value); }
    }

    public static StateSnapshot Read(BinaryReader r)
    {
        var s = new StateSnapshot { Frame = r.ReadInt32(), Loading = r.ReadBoolean() };
        int c = r.ReadByte();
        for (int i = 0; i < c; i++) s.Hashes.Add((r.ReadString(), r.ReadUInt32()));
        int n = r.ReadByte();
        for (int i = 0; i < n; i++)
        {
            s.Characters.Add((r.ReadString(), new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle())));
            s.Angles.Add(r.ReadInt16());
        }
        for (int m = r.ReadByte(); m > 0; m--) s.Spawns.Add((r.ReadInt32(), r.ReadString()));
        for (int m = r.ReadByte(); m > 0; m--) s.Numbers.Add((r.ReadString(), r.ReadInt32()));
        return s;
    }

    public uint Hash()
    {
        uint h = 2166136261;
        foreach (var (_, ch) in Hashes) h = (h ^ ch) * 16777619;
        foreach (var (name, pos) in Characters)
        {
            h = (h ^ Fnv(name)) * 16777619;
            h = (h ^ (uint)Mathf.RoundToInt(pos.x * 100)) * 16777619;
            h = (h ^ (uint)Mathf.RoundToInt(pos.y * 100)) * 16777619;
            h = (h ^ (uint)Mathf.RoundToInt(pos.z * 100)) * 16777619;
        }
        return h;
    }

    /// <summary>One <c>key=[value]</c> group per component, so a diff shows which one differs.</summary>
    public override string ToString()
    {
        var parts = new List<string> { Loading ? $"f{Frame} loading" : $"f{Frame}" };
        foreach (var (k, v) in Components) parts.Add($"{k}=[{v}]");
        var pos = new List<string>();
        for (int i = 0; i < Characters.Count; i++)
        {
            var (name, p) = Characters[i];
            pos.Add($"{name}@({P(p.x)},{P(p.y)},{P(p.z)})/{AngleAt(i)}");
        }
        parts.Add($"pos=[{string.Join(" ", pos)}]");
        return string.Join(" ", parts);
    }

    public static uint Fnv(string s)
    {
        uint h = 2166136261;
        foreach (char c in s ?? "") h = (h ^ c) * 16777619;
        return h;
    }
}

/// <summary>
/// "Host fixes": the host periodically sends its state; guests compare it to their own state at that same frame.
/// Small positional drift (floating point, physics) is corrected in place by applying the difference.
/// Any other difference is reported as a desync that names the components which differ.
/// </summary>
internal sealed class StateSync
{
    /// <summary>Snapshot interval in frames (--sosonline-stateevery).</summary>
    public static int Interval = 30;

    private readonly Dictionary<int, StateSnapshot> _localHistory = new();
    private readonly Dictionary<int, StateSnapshot> _pendingHost = new();

    public int Corrections { get; private set; }
    /// <summary>Snapshots whose HP, MP or combo points were corrected in place.</summary>
    public int NumberCorrections { get; private set; }

    /// <summary>Components made only of numbers a guest may correct in place (<see cref="StateSnapshot.Numbers"/>).</summary>
    private static readonly HashSet<string> Correctable = new() { "stats", "hp", "combo" };
    public int Checks { get; private set; }
    public int SkippedLoading { get; private set; }
    public int DesyncStreak { get; private set; }
    public bool Desynced => DesyncStreak >= 2;
    public string LastDesyncReason { get; private set; } = "";

    public void Reset()
    {
        _localHistory.Clear();
        _pendingHost.Clear();
        Corrections = NumberCorrections = Checks = DesyncStreak = SkippedLoading = 0;
        LastDesyncReason = "";
    }

    /// <summary>Called by every peer at the end of input application for <paramref name="frame"/>.</summary>
    public StateSnapshot OnFrame(int frame)
    {
        if (frame % Interval != 0) return null;
        var local = StateSnapshot.CaptureLocal(frame);
        _localHistory[frame] = local;
        _localHistory.Remove(frame - Math.Max(Interval * 20, 600));
        if (_pendingHost.Remove(frame, out var host)) Compare(host, local);
        return local;
    }

    /// <summary>Guest side: a host snapshot arrived.</summary>
    public void OnHostSnapshot(StateSnapshot host)
    {
        if (_localHistory.TryGetValue(host.Frame, out var local)) Compare(host, local);
        else _pendingHost[host.Frame] = host;
    }

    /// <summary>Metres a character may differ in a battle before it counts as out of sync (nothing is corrected there).</summary>
    private const float BattleTolerance = 0.5f;

    private void Compare(StateSnapshot host, StateSnapshot local)
    {
        if (host.Loading || local.Loading) { SkippedLoading++; return; }
        Checks++;
        var reasons = new List<string>();
        var numeric = new List<string>();
        foreach (var (key, hostHash) in host.Hashes)
        {
            var localHash = local.HashOf(key);
            if (localHash == null) reasons.Add($"{key} missing locally");
            else if (localHash == hostHash) continue;
            else if (Correctable.Contains(key)) numeric.Add(key);
            else reasons.Add(key == "spawn" ? SpawnDifference(host, local) : $"{key} differs (local [{Trim(local.Get(key))}])");
        }
        // The spawn names the cause best: put it first.
        int si = reasons.FindIndex(x => x.StartsWith("spawn", StringComparison.Ordinal));
        if (si > 0) { var first = reasons[si]; reasons.RemoveAt(si); reasons.Insert(0, first); }
        // HP, MP and combo points are corrected in place, like positions, as long as nothing structural differs (same
        // level, roster, turn, statuses and spawns). Otherwise they are part of the mismatch.
        if (numeric.Count > 0)
        {
            string fixedWhat = reasons.Count == 0 ? CorrectNumbers(host, local) : null;
            if (fixedWhat != null)
            {
                NumberCorrections++;
                Plugin.Log.LogInfo($"[sync] frame {host.Frame}: corrected {fixedWhat}");
            }
            else foreach (var key in numeric) reasons.Add($"{key} differs (local [{Trim(local.Get(key))}])");
        }
        bool sameLevel = host.HashOf("lvl") == local.HashOf("lvl") && host.HashOf("scenes") == local.HashOf("scenes");

        // Positional corrections, applied as a delta so movement since that frame is preserved. Facing is set to the
        // host's only if this character hasn't turned since that frame (a later turn came from synced input).
        int corrected = 0, turned = 0;
        // Not in battles: there characters move along the game's own attack paths, and a difference means the PCs
        // are at different points of the battle, not drift (in-sync battles need 0 corrections). Moving a character
        // mid-attack broke the guest's battle and its game crashed soon after (2026-11-15). Report it instead.
        bool battle = local.Get("combat") is string c0 && c0 != "off" || GameBridge.InBattle;
        if (sameLevel && battle)
        {
            foreach (var (name, hostPos) in host.Characters)
            {
                int li = local.Characters.FindIndex(c => c.name == name);
                if (li < 0) { reasons.Add($"character {name} missing locally"); continue; }
                float off = (hostPos - local.Characters[li].pos).magnitude;
                if (off > BattleTolerance) reasons.Add($"{name} off by {off:F1}m in battle");
            }
        }
        else if (sameLevel)
        {
            var party = GameBridge.Party?.LeaderAndFollowers;
            for (int hi = 0; hi < host.Characters.Count; hi++)
            {
                var (name, hostPos) = host.Characters[hi];
                int li = local.Characters.FindIndex(c => c.name == name);
                if (li < 0) { reasons.Add($"character {name} missing locally"); continue; }
                var delta = hostPos - local.Characters[li].pos;
                bool move = delta.sqrMagnitude >= 0.0004f; // >= 2 cm
                if (delta.sqrMagnitude > 25f) { reasons.Add($"{name} off by {delta.magnitude:F1}m"); }
                int hostAngle = host.AngleAt(hi), localAngle = local.AngleAt(li);
                bool turn = hostAngle >= 0 && localAngle >= 0 && hostAngle != localAngle;
                if ((!move && !turn) || party == null) continue;
                for (int i = 0; i < party.Count; i++)
                {
                    var c = party[i];
                    if (c == null || c.gameObject.name != name) continue;
                    if (move) { c.transform.position += delta; corrected++; }
                    if (turn)
                    {
                        try
                        {
                            var look = c.lookDirectionController;
                            if (look != null && look.CurrentLookAngle == localAngle) { look.SetAngle(hostAngle, true, true); turned++; }
                        }
                        catch { }
                    }
                }
            }
        }
        if (corrected > 0 || turned > 0)
        {
            Corrections += corrected + turned;
            Plugin.Log.LogInfo($"[sync] frame {host.Frame}: corrected {corrected} character position(s), {turned} facing(s)");
        }

        if (reasons.Count > 0)
        {
            DesyncStreak++;
            LastDesyncReason = string.Join("; ", reasons);
            Plugin.Log.LogWarning($"[sync] frame {host.Frame} mismatch #{DesyncStreak}: {LastDesyncReason}\n  local: {local}");
        }
        else DesyncStreak = 0;
    }

    /// <summary>The first enemy spawn or removal in the window that differs between the host and this PC, with its frame.</summary>
    private static string SpawnDifference(StateSnapshot host, StateSnapshot local)
    {
        int start = Net.Session.Instance.Engine.StartFrame;
        var h = host.Spawns.FindAll(e => e.frame >= start);
        var l = local.Spawns.FindAll(e => e.frame >= start);
        for (int i = 0; i < Math.Max(h.Count, l.Count); i++)
        {
            var he = i < h.Count ? h[i] : (frame: -1, what: null);
            var le = i < l.Count ? l[i] : (frame: -1, what: null);
            if (he.frame == le.frame && he.what == le.what) continue;
            int f = he.frame < 0 ? le.frame : le.frame < 0 ? he.frame : Math.Min(he.frame, le.frame);
            return $"spawn differs at f{f}: host [{(he.what == null ? "none" : $"f{he.frame} {he.what}")}], local [{(le.what == null ? "none" : $"f{le.frame} {le.what}")}]";
        }
        return $"spawn differs (local [{local.Get("spawn")}], same spawns in this window)";
    }

    /// <summary>
    /// Applies the host's numbers as a delta (host minus this PC at the snapshot's frame) to the live values, so
    /// changes since then are kept. All or nothing: a value missing on either side, or one side at 0 or below and the
    /// other above (a death is structural), corrects nothing. Returns what was corrected, or null.
    /// </summary>
    private static string CorrectNumbers(StateSnapshot host, StateSnapshot local)
    {
        var changes = new List<(string key, int delta, int from, int to)>();
        foreach (var (key, hv) in host.Numbers)
        {
            int? lv = local.NumberOf(key);
            if (lv == null) return null;
            if (hv == lv.Value) continue;
            if ((hv <= 0) != (lv.Value <= 0)) return null;
            changes.Add((key, hv - lv.Value, lv.Value, hv));
        }
        foreach (var (key, _) in local.Numbers) if (host.NumberOf(key) == null) return null;
        if (changes.Count == 0) return null; // the hashes differ but no number does: something else differs
        // Check every live value first, then write.
        var writes = new List<(Action<int> set, int value)>();
        foreach (var (key, delta, _, _) in changes)
        {
            var (get, set) = LiveNumber(key);
            if (get == null) return null;
            int now = get();
            int value = now + delta;
            if ((now <= 0) != (value <= 0)) return null;
            writes.Add((set, value));
        }
        foreach (var (set, value) in writes) set(value);
        return string.Join(", ", changes.ConvertAll(c => $"{c.key} {c.from}->{c.to}"));
    }

    /// <summary>Getter and setter of the live value behind a <see cref="StateSnapshot.Numbers"/> key; (null, null) if it's gone.</summary>
    private static (Func<int> get, Action<int> set) LiveNumber(string key)
    {
        try
        {
            if (key == "combo")
            {
                var stats = GameBridge.Stats;
                if (stats == null) return (null, null);
                return (() => stats.comboPoints, v => stats.comboPoints = v);
            }
            if (key.StartsWith("hp:", StringComparison.Ordinal) || key.StartsWith("sp:", StringComparison.Ordinal))
            {
                string who = key[3..];
                bool hp = key[0] == 'h';
                var ids = GameBridge.Party?.CurrentParty;
                var stats = GameBridge.Stats;
                if (ids == null || stats == null) return (null, null);
                for (int i = 0; i < ids.Count; i++)
                {
                    if (ids[i].characterId != who) continue;
                    var data = stats.GetCharacterData(ids[i]);
                    if (data == null) return (null, null);
                    return hp ? (() => data.currentHP, v => data.currentHP = v) : (() => data.currentSP, v => data.currentSP = v);
                }
                return (null, null);
            }
            if (key.StartsWith("ehp:", StringComparison.Ordinal))
            {
                string id = key[4..];
                var actors = GameBridge.Combat?.CurrentEncounter?.AllActors;
                if (actors == null) return (null, null);
                for (int i = 0; i < actors.Count; i++)
                {
                    var enemy = actors[i]?.TryCast<EnemyCombatActor>();
                    if (enemy == null || StateSnapshot.ActorId(enemy) != id) continue;
                    var target = enemy.CombatTarget;
                    if (target == null) return (null, null);
                    return (() => target.currentHP, v => { target.currentHP = v; try { target.UpdateHPStatus(); } catch { } });
                }
            }
        }
        catch { }
        return (null, null);
    }

    private static string Trim(string v) => v == null ? "" : v.Length > 160 ? v[..160] + "…" : v;
}

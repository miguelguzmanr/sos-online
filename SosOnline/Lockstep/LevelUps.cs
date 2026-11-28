using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Rewired;
using SosOnline.Game;
using SosOnline.Net;

namespace SosOnline.Lockstep;

/// <summary>
/// "Your character, your level up" (the same host setting as <see cref="BattleTurns"/>): after a battle, the player
/// who controls a character chooses that character's stat bonus.
///
/// How the game does it: <c>LevelUpSceneController</c> levels the party up one character at a time
/// (<c>LevelUpCharacter</c>), and for each opens a <c>LevelUpScreen</c> whose stars (the bonus choices) are moved and
/// confirmed with the game's "UI player" (<c>SinglePlayerPlusManager.GetUIInputsPlayer</c>). Nothing in it looks at who
/// controls the character: the UI player is whoever had it last, usually the player of the battle's last turn.
///
/// With the rule on, every PC makes the character's owner (who controlled it outside the battle, the same record the
/// battle turns use) the UI player as the character's level up starts, and gives the UI player back to the game's
/// own when the level-up sequence ends. A character nobody playing owns keeps the game's UI player. The owner and the
/// level-up calls are synced state, so every PC switches on the same frame.
///
/// The UI player only moves the cursor: the screen's own <c>Update</c> confirms a star with the game's first player
/// (<c>InputManager.FirstPlayer</c>, P1), whoever the UI player is (local co-op: P1 picks every bonus). While a
/// character's owner isn't the first player, its <c>Update</c> is replaced by the same check on the owner's synced
/// input, so only the owner confirms.
///
/// The level-up has a barrier of its own (<see cref="Session.HoldLevelUp"/>): the game loads its <c>LevelUp</c> scene
/// asynchronously after the fade, and the PCs got it a frame apart, so the whole level-up ran a frame apart and the
/// cursor started on another star (a character's position decides it). Game time stands still from the end of the
/// fade (the same frame everywhere), <c>LevelUpSceneController.DoLevelUp</c> waits until every PC has the scene, and
/// every PC starts it on the release frame the host picks. The hold starts and ends on the same frames everywhere, so
/// the clock needs no new base.
/// </summary>
internal static class LevelUps
{
    private static Session S => Session.Instance;
    private static int Frame => S.Engine.Running ? S.Engine.AppliedFrame : -1;

    /// <summary>The game's UI player before the first switch of this level-up sequence (null: none switched).</summary>
    private static Player _gameUiPlayer;
    /// <summary>Slot choosing the current character's bonus (-1: none, or the game's own UI player).</summary>
    public static int ChoosingSlot { get; private set; } = -1;
    /// <summary>Slot of the current character's owner, who confirms its bonus (-1: nobody playing owns it: the game's way).</summary>
    private static int _ownerSlot = -1;

    /// <summary>The bonus choice on screen (tests: <c>--sosonline-botlevelup</c>), and a count of the ones opened.</summary>
    private static LevelUpScreen _screen;
    public static int ScreenCount { get; private set; }

    /// <summary>A bonus choice is on screen and takes input (this PC's view; the bot decides its presses from it).</summary>
    public static bool CanChoose
    {
        get
        {
            try { return _screen != null && _screen.gameObject.activeInHierarchy && !_screen.closing && _screen.canSelectUpgrade; }
            catch { return false; }
        }
    }

    /// <summary>The level-up start is held (from the end of the fade to the release): state isn't compared meanwhile.</summary>
    public static bool Holding => _holdFrom >= 0;
    private static int _holdFrom = -1;
    private static LevelUpSceneController _held;
    private static bool _releasing;

    /// <summary>The release frame: start the held level-up (every PC, on the same frame).</summary>
    public static void Release(int frame)
    {
        var held = _held;
        int from = _holdFrom;
        _held = null;
        _holdFrom = -1;
        GameClock.EndLoadHold();
        if (held == null) { Plugin.Log.LogError($"[levelup] f{frame} release, but no level-up is held here"); return; }
        Plugin.Log.LogInfo($"[levelup] f{frame} released (held {frame - from} frames)");
        _releasing = true;
        try { held.DoLevelUp(); }
        catch (Exception e) { Plugin.Log.LogWarning($"[levelup] start: {e.Message}"); }
        finally { _releasing = false; }
    }

    public static void Reset()
    {
        // Lockstep stopped during a hold: the game's level-up carries on at once.
        if (_holdFrom >= 0)
        {
            var held = _held;
            _held = null;
            _holdFrom = -1;
            GameClock.EndLoadHold();
            if (held != null)
            {
                _releasing = true;
                try { held.DoLevelUp(); } catch { } finally { _releasing = false; }
            }
        }
        _gameUiPlayer = null;
        ChoosingSlot = -1;
        _ownerSlot = -1;
        _screen = null;
    }

    public static void Install(Harmony harmony)
    {
        Patch(harmony, typeof(LevelUpSceneController), nameof(LevelUpSceneController.LevelUpCharacter), nameof(CharacterPrefix), true);
        Patch(harmony, typeof(LevelUpScreen), nameof(LevelUpScreen.Init), nameof(ScreenPrefix), true);
        Patch(harmony, typeof(LevelUpScreen), nameof(LevelUpScreen.OnItemSelected), nameof(SelectedPrefix), true);
        Patch(harmony, typeof(LevelUpScreen), nameof(LevelUpScreen.Update), nameof(UpdatePrefix), true);
        Patch(harmony, typeof(LevelUpScreen), nameof(LevelUpScreen.OnTargetChanged), nameof(TargetPrefix), true);
        Patch(harmony, typeof(LevelUpSceneController), nameof(LevelUpSceneController.EndLevelUp), nameof(EndPostfix), false);
        // The barrier needs both: a hold that nothing would end must never start.
        if (Patch(harmony, typeof(LevelUpSceneController), nameof(LevelUpSceneController.DoLevelUp), nameof(StartPrefix), true))
            Patch(harmony, typeof(LevelUpSequence), nameof(LevelUpSequence.OnTransitionInDone), nameof(FadedPrefix), true);
    }

    private static bool Patch(Harmony harmony, Type type, string method, string patch, bool prefix)
    {
        var target = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly).FirstOrDefault(m => m.Name == method);
        if (target == null) { Plugin.Log.LogWarning($"[levelup] {type.Name}.{method} not found"); return false; }
        if (NativeCode.IsFolded(target)) { Plugin.Log.LogWarning($"[levelup] {type.Name}.{method} is folded with other methods; not patched"); return false; }
        var hm = new HarmonyMethod(typeof(LevelUps).GetMethod(patch, BindingFlags.Static | BindingFlags.NonPublic));
        if (prefix) harmony.Patch(target, prefix: hm); else harmony.Patch(target, postfix: hm);
        return true;
    }

    private static bool Running => S.Engine.Running;

    private static string IdOf(PlayerPartyCharacter c)
    {
        try { return c == null ? "" : new ISinglePlayerPlusCharacter(c.Pointer).CharacterDefinitionId?.characterId ?? ""; }
        catch { return ""; }
    }

    private static string UiSlotText()
    {
        var p = GameBridge.Coop?.GetUIInputsPlayer();
        int slot = GameBridge.SlotOf(p);
        return slot >= 0 ? $"P{slot + 1}" : p == null ? "none" : $"player {p.id}";
    }

    /// <summary>Gives the UI player to the owner of <paramref name="id"/> (or leaves the game's) and logs it.</summary>
    private static void HandTo(string id, string where)
    {
        var coop = GameBridge.Coop;
        if (coop == null) return;
        int owner = BattleTurns.OwnTurns && S.Engine.Occupied.Count(o => o) > 1 ? BattleTurns.OwnerSlot(id) : -1;
        _ownerSlot = owner;
        var current = coop.GetUIInputsPlayer();
        int currentSlot = GameBridge.SlotOf(current);
        if (owner < 0 || owner == currentSlot)
        {
            ChoosingSlot = currentSlot;
            Plugin.Log.LogInfo($"[levelup] f{Frame} {where} {id}: P{currentSlot + 1} chooses ({(owner >= 0 ? "owner, already the UI player" : !BattleTurns.OwnTurns ? "the game's UI player; Battle Turns: Original" : "the game's UI player; nobody playing owns it")})");
            return;
        }
        var player = GameBridge.PlayerForSlot(owner);
        if (player == null) return;
        _gameUiPlayer ??= current;
        coop.SetUIInputsPlayer(player);
        ChoosingSlot = owner;
        Plugin.Log.LogInfo($"[levelup] f{Frame} {where} {id}: P{owner + 1} chooses (owner; the game's UI player was {(currentSlot >= 0 ? $"P{currentSlot + 1}" : "none")})");
    }

    // ---------------------------------------------------------------- patches

    /// <summary>The fade to the level-up is done (same frame on every PC): game time stands still until the release.</summary>
    private static void FadedPrefix(LevelUpSequence __instance)
    {
        if (!Running || _holdFrom >= 0 || !NativeCode.Is<LevelUpSequence>(__instance)) return;
        _holdFrom = Frame;
        GameClock.HoldForLoad();
        Plugin.Log.LogInfo($"[levelup] f{Frame} fade done: game time held until every PC has the level-up scene");
    }

    /// <summary>The scene has loaded on this PC: wait for the release frame (<see cref="Session.HoldLevelUp"/>).</summary>
    private static bool StartPrefix(LevelUpSceneController __instance)
    {
        if (_releasing || !Running || _holdFrom < 0 || _held != null || !NativeCode.Is<LevelUpSceneController>(__instance)) return true;
        _held = __instance;
        S.HoldLevelUp(Frame);
        return false;
    }

    private static void CharacterPrefix(LevelUpSceneController __instance, PlayerPartyCharacter __0)
    {
        if (!Running || !NativeCode.Is<LevelUpSceneController>(__instance)) return;
        try { HandTo(IdOf(__0), "level up of"); }
        catch (Exception e) { Plugin.Log.LogWarning($"[levelup] level up of a character: {e.Message}"); }
    }

    /// <summary>The bonus choice opens: the owner again, in case the game moved the UI player in between.</summary>
    private static void ScreenPrefix(LevelUpScreen __instance, IViewParams __0)
    {
        if (!Running || !NativeCode.Is<LevelUpScreen>(__instance)) return;
        try
        {
            _screen = __instance;
            ScreenCount++;
            var p = __0?.TryCast<LevelUpScreenParams>();
            string id = p?.levelingCharacter?.characterId ?? "";
            HandTo(id, $"bonus choice ({p?.levelUpCount ?? 0}) for");
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[levelup] bonus choice: {e.Message}"); }
    }

    /// <summary>
    /// The game's check, on the owner's input instead of the first player's: confirm picks the star under the cursor.
    /// Runs on the applied frame like every other input read, so every PC picks on the same frame.
    /// </summary>
    private static bool UpdatePrefix(LevelUpScreen __instance)
    {
        if (!Running || _ownerSlot < 0 || !NativeCode.Is<LevelUpScreen>(__instance)) return true;
        try
        {
            var owner = GameBridge.PlayerForSlot(_ownerSlot);
            var first = Manager<global::InputManager>.instance?.FirstPlayer;
            if (owner == null || first == null || owner.id == first.id) return true;
            if (__instance.canSelectUpgrade && !__instance.closing && owner.GetButtonDown(UIInputs.CONFIRM))
            {
                var star = __instance.navigation?.currentTarget;
                if (star != null) __instance.OnItemSelected(star);
            }
            return false;
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[levelup] update: {e.Message}"); return true; }
    }

    private static void TargetPrefix(LevelUpScreen __instance, LevelUpStar __0)
    {
        if (!Running || !NativeCode.Is<LevelUpScreen>(__instance)) return;
        try { Plugin.Log.LogInfo($"[levelup] f{Frame} {__instance.levelingCharacterID?.characterId}: cursor on {__0?.upgradeType} (UI player {UiSlotText()})"); }
        catch { }
    }

    private static void SelectedPrefix(LevelUpScreen __instance, LevelUpStar __0)
    {
        if (!Running || !NativeCode.Is<LevelUpScreen>(__instance)) return;
        try
        {
            string id = __instance.levelingCharacterID?.characterId ?? "";
            string by = _ownerSlot >= 0 ? $"P{_ownerSlot + 1}" : $"P{GameBridge.SlotOf(Manager<global::InputManager>.instance?.FirstPlayer) + 1} (the game's first player; UI player {UiSlotText()})";
            Plugin.Log.LogInfo($"[levelup] f{Frame} {id}: {__0?.upgradeType} chosen by {by}");
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[levelup] chosen: {e.Message}"); }
    }

    private static void EndPostfix(LevelUpSceneController __instance)
    {
        if (!Running || !NativeCode.Is<LevelUpSceneController>(__instance)) return;
        try
        {
            var back = _gameUiPlayer;
            _gameUiPlayer = null;
            ChoosingSlot = -1;
            _ownerSlot = -1;
            _screen = null;
            if (back == null) { Plugin.Log.LogInfo($"[levelup] f{Frame} done (UI player {UiSlotText()})"); return; }
            // The game's UI player again, unless that player left meanwhile: then the game's own replacement stays.
            int slot = GameBridge.SlotOf(back);
            if (slot >= 0 && !GameBridge.IsPlaying(slot) && slot != 0) { Plugin.Log.LogInfo($"[levelup] f{Frame} done; P{slot + 1} left, UI player stays {UiSlotText()}"); return; }
            GameBridge.Coop?.SetUIInputsPlayer(back);
            Plugin.Log.LogInfo($"[levelup] f{Frame} done; UI player back to {UiSlotText()}");
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[levelup] end: {e.Message}"); }
    }
}

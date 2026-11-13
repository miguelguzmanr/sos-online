using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using SosOnline.Net;
using UnityEngine;

namespace SosOnline;

/// <summary>
/// --sosonline-trace: logs every call into the combat timing machinery with the lockstep frame and game time,
/// to see how timed hits / blocks / QTEs are evaluated.
/// </summary>
internal static class Diagnostics
{
    public static void InstallTrace(Harmony harmony)
    {
        var targets = new (Type type, string[] methods)[]
        {
            (typeof(TimedInputHandler), new[] { "BeginInputPhase", "EndInputPhase", "OnInputPressed", "ResetInput", "SetInputEnabled" }),
            (typeof(AbstractTimedAttackHandler), new[] { "BeginInputPhase", "EndInputPhase", "OnInputPressed", "ResetInput", "GetResult", "EndInputPress" }),
            (typeof(TimedAttackHandler), new[] { "GetResult", "SendAttackResult" }),
            (typeof(TimedBlockHandler), new[] { "BeginInputPhase", "EndInputPhase", "OnInputPressed", "ResetInput", "DoBlock", "EndBlock", "GetResult", "SetInputEnabled" }),
            (typeof(PlayerCombatActor), new[] { "GetAttackResult", "OnBlockInput" }),
            (typeof(TimeQTEHandler), new[] { "Init", "Reset" }),
            (typeof(AssignControllersScreen), new[] { "Init", "AddController", "AssignControllers", "ValidateSetup", "EnableJoystickAutoAssignment", "UpdateActivePanels", "RemoveController" }),
            (typeof(AssignControllersScreenController), new[] { "Init", "SetReady", "SetNotReady", "SetCurrentPanelIndex", "GetRawMoveVector" }),
            (typeof(InputManager), new[] { "AssignPlayer", "UnassignPlayer", "SetControllerAssignmentMode", "AutoAssignUnassignedControllers" }),
            (typeof(SinglePlayerPlusPlayer), new[] { "Join", "Leave", "BeginPlaying", "StopPlaying", "AssignCharacter" }),
        };
        int count = 0;
        foreach (var (type, names) in targets)
            foreach (var m in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly).Where(m => names.Contains(m.Name)))
            {
                var prefixName = type == typeof(TimedInputHandler) ? nameof(PInput)
                    : type == typeof(AbstractTimedAttackHandler) ? nameof(PAbstractAttack)
                    : type == typeof(TimedAttackHandler) ? nameof(PAttack)
                    : type == typeof(TimedBlockHandler) ? nameof(PBlock)
                    : type == typeof(PlayerCombatActor) ? nameof(PActor)
                    : type == typeof(TimeQTEHandler) ? nameof(PQte)
                    : type == typeof(AssignControllersScreen) ? nameof(PAssign)
                    : type == typeof(AssignControllersScreenController) ? nameof(PAssignItem)
                    : type == typeof(InputManager) ? nameof(PInputManager) : nameof(PSpp);
                var prefix = new HarmonyMethod(typeof(Diagnostics).GetMethod(prefixName, BindingFlags.Static | BindingFlags.NonPublic));
                if (NativeCode.IsFolded(m)) { Plugin.Log.LogInfo($"[trace] skip folded {type.Name}.{m.Name}"); continue; }
                try { harmony.Patch(m, prefix: prefix); count++; }
                catch (Exception e) { Plugin.Log.LogWarning($"[trace] can't patch {type.Name}.{m.Name}: {e.Message}"); }
            }
        void Typed(Type t, string name, string patchName, bool post = true)
        {
            var m = t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly).FirstOrDefault(x => x.Name == name);
            if (m == null || NativeCode.IsFolded(m)) { Plugin.Log.LogInfo($"[trace] skip folded/missing {t.Name}.{name}"); return; }
            var hm = new HarmonyMethod(typeof(Diagnostics).GetMethod(patchName, BindingFlags.Static | BindingFlags.NonPublic));
            try { if (post) harmony.Patch(m, postfix: hm); else harmony.Patch(m, prefix: hm); count++; }
            catch (Exception e) { Plugin.Log.LogWarning($"[trace] can't patch {t.Name}.{name}: {e.Message}"); }
        }
        Typed(typeof(TimedAttackHandler), "OnAttackResultReady", nameof(AttackResult), false);
        Typed(typeof(TimedAttackHandler), "OnSelfAttackResultReady", nameof(SelfAttackResult), false);
        Typed(typeof(PlayerCombatActor), "GetBlockResult", nameof(BlockResult));
        Typed(typeof(TimeQTEHandler), "GetQTEResult", nameof(QteResult));
        Plugin.Log.LogInfo($"[trace] {count} combat timing methods traced");
    }

    /// <summary>--sosonline-assignprobe: what the Assign Players screen is built from, and whether virtual pads exist.</summary>
    public static void AssignProbe(int frame)
    {
        try
        {
            if (frame == 900)
            {
                Plugin.Log.LogInfo($"[probe] custom controllers: {Rewired.ReInput.controllers.customControllerCount}, layouts {Rewired.ReInput.mapping.CustomControllerLayouts.ToList().Count}");
                for (int id = 0; id < 4; id++)
                {
                    var cc = Rewired.ReInput.controllers.CreateCustomController(id);
                    Plugin.Log.LogInfo($"[probe] CreateCustomController({id}) -> {(cc == null ? "null" : $"{cc.name}")}");
                    if (cc != null) Rewired.ReInput.controllers.DestroyCustomController(cc);
                }
                var all = Rewired.ReInput.controllers.Controllers.ToList();
                Plugin.Log.LogInfo($"[probe] controllers: {string.Join(", ", all.Select(c => $"{c.type}#{c.id} {c.name}"))}");
                foreach (var p in Rewired.ReInput.players.AllPlayers.ToList())
                    Plugin.Log.LogInfo($"[probe] player {p.id} {p.name}: kb={p.controllers.hasKeyboard} joys={p.controllers.joystickCount}");
            }
            if (frame == 1150 || frame == 1175 || frame == 1205 || frame == 1245)
            {
                var scr = UnityEngine.Object.FindObjectsOfType<AssignControllersScreen>().FirstOrDefault();
                if (scr == null) { Plugin.Log.LogInfo("[probe] assign screen not found"); return; }
                var ctrls = scr.Controllers;
                var list = new System.Collections.Generic.List<string>();
                for (int i = 0; i < ctrls.Count; i++) list.Add($"{ctrls[i].type}#{ctrls[i].id}");
                Plugin.Log.LogInfo($"[probe] assign screen controllers: {string.Join(", ", list)}; panels {scr.panels.Count}; items {scr.controllerUIItems.Count}");
                for (int i = 0; i < scr.controllerUIItems.Count; i++)
                {
                    var it = scr.controllerUIItems[i];
                    Plugin.Log.LogInfo($"[probe]  item {i}: controller {it.controller?.type}#{it.controller?.id} spp={(it.player != null ? it.player.Index.ToString() : "null")} rewired={it.player?.Player?.id} uiInputsPlayer={it.uiInputs?.player?.id} panel={it.currentPanelIndex} ready={it.Ready}");
                }
            }
        }
        catch (Exception e) { Plugin.Log.LogError($"[probe] {e}"); }
    }

    private static string F => $"f{Session.Instance.Engine.Frame} t={Time.time:F3}";

    private static void AttackResult(Rewired.Player __0, EQTEResult __1) =>
        Plugin.Log.LogInfo($"[trace] {F} TimedAttackHandler.OnAttackResultReady player={__0?.id} result={__1}");

    private static void SelfAttackResult(EQTEResult __0) =>
        Plugin.Log.LogInfo($"[trace] {F} TimedAttackHandler.OnSelfAttackResultReady result={__0}");

    private static void BlockResult(PlayerCombatActor __instance, TeamQTEResult __result)
    {
        try
        {
            var best = __result?.GetBestResult();
            Plugin.Log.LogInfo($"[trace] {F} GetBlockResult actor={__instance.gameObject.name} best={(best != null ? best.result.ToString() : "null")} successes={__result?.GetSuccessCount()}");
        }
        catch (Exception e) { Plugin.Log.LogInfo($"[trace] GetBlockResult: {e.Message}"); }
    }

    private static void QteResult(float __0, QTEResult __result) =>
        Plugin.Log.LogInfo($"[trace] {F} TimeQTEHandler.GetQTEResult toQTE={__0:F3} result={__result?.result}");

    private static void PInput(TimedInputHandler __instance, MethodBase __originalMethod) => Prefix(__instance, __originalMethod);
    private static void PAbstractAttack(AbstractTimedAttackHandler __instance, MethodBase __originalMethod) => Prefix(__instance, __originalMethod);
    private static void PAttack(TimedAttackHandler __instance, MethodBase __originalMethod) => Prefix(__instance, __originalMethod);
    private static void PBlock(TimedBlockHandler __instance, MethodBase __originalMethod) => Prefix(__instance, __originalMethod);
    private static void PActor(PlayerCombatActor __instance, MethodBase __originalMethod) => Prefix(__instance, __originalMethod);
    private static void PQte(TimeQTEHandler __instance, MethodBase __originalMethod) => Prefix(__instance, __originalMethod);
    private static void PAssign(AssignControllersScreen __instance, MethodBase __originalMethod) => Prefix(__instance, __originalMethod);
    private static void PAssignItem(AssignControllersScreenController __instance, MethodBase __originalMethod) => Prefix(__instance, __originalMethod);
    private static void PInputManager(InputManager __instance, MethodBase __originalMethod) => Prefix(__instance, __originalMethod);
    private static void PSpp(SinglePlayerPlusPlayer __instance, MethodBase __originalMethod) =>
        Plugin.Log.LogInfo($"[trace] {F} SinglePlayerPlusPlayer.{__originalMethod.Name} index={__instance.Index} rewired={__instance.Player?.id} playing={__instance.Playing}");

    private static void Prefix(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase __instance, MethodBase __originalMethod)
    {
        try
        {
            string extra = "";
            var obj = __instance as Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase;
            object typed = __instance;
            if (obj != null && obj.Pointer != IntPtr.Zero)
            {
                // Wrap by the object's real IL2CPP class (TryCast can fault on classes not initialized yet).
                var klass = Il2CppInterop.Runtime.IL2CPP.il2cpp_object_get_class(obj.Pointer);
                string cls = System.Runtime.InteropServices.Marshal.PtrToStringAnsi(Il2CppInterop.Runtime.IL2CPP.il2cpp_class_get_name(klass));
                typed = cls switch
                {
                    "TimedAttackHandler" => new TimedAttackHandler(obj.Pointer),
                    "TimedBlockHandler" => new TimedBlockHandler(obj.Pointer),
                    "TimeQTEHandler" => new TimeQTEHandler(obj.Pointer),
                    "PlayerCombatActor" => new PlayerCombatActor(obj.Pointer),
                    _ => __instance,
                };
                if (typed == __instance) extra = $" [{cls}]";
            }
            switch (typed)
            {
                case TimedAttackHandler a:
                    extra = $" beforeEnd={a.beforeHitWindowEndTime:F3} afterEnd={a.afterHitWindowEndTime:F3} beforeDur={a.GetBeforeHitWindowDuration():F3} afterDur={a.GetAfterHitWindowDuration():F3} pressed={a.inputPressed} player={a.combatActor?.Player?.id}";
                    break;
                case TimedBlockHandler b:
                    extra = $" blockEnd={b.blockEndTime:F3} blocking={b.blocking} dur={b.GetBlockDuration():F3} player={b.combatActor?.Player?.id}";
                    break;
                case TimeQTEHandler q:
                    extra = $" toQTE={(q.qteObject != null ? q.qteObject.TimeToQTE() : float.NaN):F3} early={q.hitTooEarly}";
                    break;
                case PlayerCombatActor pa:
                    extra = $" actor={pa.gameObject.name} player={pa.Player?.id}";
                    break;
            }
            Plugin.Log.LogInfo($"[trace] f{Session.Instance.Engine.Frame} t={Time.time:F3} {__originalMethod.DeclaringType?.Name}.{__originalMethod.Name}{extra}");
        }
        catch (Exception e)
        {
            Plugin.Log.LogInfo($"[trace] {__originalMethod?.Name}: {e.Message}");
        }
    }
}

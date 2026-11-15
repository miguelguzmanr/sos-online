using System;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using SosOnline.Lockstep;
using SosOnline.Net;
using SosOnline.Steam;
using SosOnline.UI;
using UnityEngine;

namespace SosOnline;

[BepInPlugin(Guid, "Sea of Stars Online Co-op", Version)]
public sealed class Plugin : BasePlugin
{
    public const string Guid = "sosonline.coop";
    public const string Version = "0.1.0";

    internal static ManualLogSource Log;

    public override void Load()
    {
        Log = base.Log;
        try
        {
            var harmony = new Harmony(Guid);
            harmony.PatchAll(typeof(Plugin).Assembly);
            ClassInjector.RegisterTypeInIl2Cpp<OnlineBehaviour>();
            AddComponent<OnlineBehaviour>();
            TestHarness.Init();
            GameClock.Install();
            if (TestHarness.TraceEnabled) Diagnostics.InstallTrace(harmony);
            if (TestHarness.TryArg("tracecalls", out var traceClasses)) Diagnostics.InstallCallTrace(harmony, traceClasses);
            if (TestHarness.TryArg("dashprobe", out _)) Diagnostics.InstallDashProbe(harmony);
            TimingCompensation.Install(harmony);
            NativeCode.AuditPatches(harmony, Guid);
            SteamInvites.ParseCommandLine();
            Log.LogInfo($"Sea of Stars Online Co-op {Version} loaded. Press F8 in game.");
        }
        catch (Exception e)
        {
            Log.LogError($"Failed to initialize: {e}");
        }
    }
}

/// <summary>Per-frame driver living on a hidden GameObject.</summary>
public sealed class OnlineBehaviour : MonoBehaviour
{
    public OnlineBehaviour(IntPtr ptr) : base(ptr) { }

    /// <summary>Physics steps Unity has run (diagnostics: --sosonline-fixedlog).</summary>
    internal static int FixedSteps;

    private void FixedUpdate() => FixedSteps++;

    private void Update()
    {
        try
        {
            if (!ActionTable.Ready) ActionTable.TryBuild();
            Session.Instance.Update();
            Overlay.Update();
            NativeMenus.Update();
            SteamInvites.Update();
            TestHarness.Update();
        }
        catch (Exception e)
        {
            Plugin.Log.LogError(e);
        }
    }

    private void OnGUI()
    {
        try { Overlay.OnGUI(); Toast.OnGUI(); }
        catch (Exception e) { Plugin.Log.LogError(e); }
    }
}

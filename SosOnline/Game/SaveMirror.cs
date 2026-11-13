using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace SosOnline.Game;

/// <summary>
/// Keeps every peer on the host's save data.
///
/// Host: packs its save folder (index, slots, autosaves, options).
/// Guest: writes those files into &lt;saves&gt;/SosOnlineSession and redirects the game's file I/O there, then
/// reloads SaveManager. The guest's own saves are never read or written while the mirror is active, and the
/// in-game saves made during co-op land in the mirror folder (the host keeps the canonical copy).
/// </summary>
internal static class SaveMirror
{
    /// <summary>Guests' copy of the host's saves (per instance in test mode, so local test instances don't collide).</summary>
    public static string MirrorFolder = "SosOnlineSession";

    /// <summary>
    /// Test mode: this instance's own saves live in a sandbox copy, so test runs never write the player's real saves.
    /// </summary>
    public static string SandboxFolder;

    /// <summary>Copies the real saves into a fresh sandbox and plays from it (call before the game loads its saves).</summary>
    public static void UseSandbox(string folder)
    {
        var dir = Path.Combine(SaveDir, folder);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        foreach (var f in Directory.GetFiles(SaveDir, "*.sos")) File.Copy(f, Path.Combine(dir, Path.GetFileName(f)));
        SandboxFolder = folder;
        Plugin.Log.LogInfo($"[saves] sandboxed in {dir}");
    }

    /// <summary>Where this instance's own saves are (the real folder, or the test sandbox).</summary>
    public static string OwnDir => SandboxFolder != null ? Path.Combine(SaveDir, SandboxFolder) : SaveDir;

    public static bool Active { get; private set; }

    public static string SaveDir => Application.persistentDataPath;
    public static string MirrorDir => Path.Combine(SaveDir, MirrorFolder);

    public static List<(string name, byte[] data)> PackHostFiles()
    {
        var files = new List<(string, byte[])>();
        // Slots, autosaves (Autosave_SOS_SaveSlotN.sos), index and options.
        foreach (var path in Directory.GetFiles(OwnDir, "*.sos"))
            files.Add((Path.GetFileName(path), File.ReadAllBytes(path)));
        return files;
    }

    public static void BeginReceive()
    {
        Directory.CreateDirectory(MirrorDir);
        foreach (var f in Directory.GetFiles(MirrorDir)) File.Delete(f);
    }

    public static void WriteFile(string name, byte[] data)
    {
        // Never let a peer write outside the mirror folder.
        var safe = Path.GetFileName(name);
        if (string.IsNullOrEmpty(safe) || !safe.EndsWith(".sos", StringComparison.OrdinalIgnoreCase)
            || !(safe.StartsWith("SOS_", StringComparison.Ordinal) || safe.StartsWith("Autosave_SOS_", StringComparison.Ordinal))) return;
        File.WriteAllBytes(Path.Combine(MirrorDir, safe), data);
    }

    private static Il2CppSystem.Threading.Tasks.Task _reload;

    /// <summary>Activates/deactivates redirection and asks SaveManager to reload everything from disk.</summary>
    public static void SetActiveAndReload(bool active)
    {
        Active = active;
        var saves = GameBridge.Saves;
        if (saves == null) return;
        try
        {
            var standalone = saves.saveLoad?.TryCast<SaveLoadStandalone>();
            // The registry backup would otherwise copy the host's data over the guest's own backup.
            if (standalone != null) standalone.registryBackup = !active && _savedRegistryBackup;
        }
        catch { }
        _reload = saves.LoadSaveData();
    }

    private static bool _savedRegistryBackup = true;

    public static void RememberRegistryBackup()
    {
        try
        {
            var standalone = GameBridge.Saves?.saveLoad?.TryCast<SaveLoadStandalone>();
            if (standalone != null) _savedRegistryBackup = standalone.registryBackup;
        }
        catch { }
    }

    public static bool ReloadDone => _reload == null || _reload.IsCompleted;

    private static string Redirect(string fileName)
    {
        string folder = Active ? MirrorFolder : SandboxFolder;
        if (folder == null || string.IsNullOrEmpty(fileName)) return fileName;
        if (fileName.Contains(folder)) return fileName;
        if (Path.IsPathRooted(fileName))
            return Path.Combine(Path.GetDirectoryName(fileName) ?? SaveDir, folder, Path.GetFileName(fileName));
        return Path.Combine(folder, fileName);
    }

    [HarmonyPatch(typeof(SaveLoadStandalone), nameof(SaveLoadStandalone.SaveFile))]
    private static class P_Save { private static void Prefix(SaveLoadStandalone __instance, ref string __1) { if (NativeCode.Is<SaveLoadStandalone>(__instance)) __1 = Redirect(__1); } }

    [HarmonyPatch(typeof(SaveLoadStandalone), nameof(SaveLoadStandalone.LoadFile))]
    private static class P_Load { private static void Prefix(SaveLoadStandalone __instance, ref string __0) { if (NativeCode.Is<SaveLoadStandalone>(__instance)) __0 = Redirect(__0); } }

    [HarmonyPatch(typeof(SaveLoadStandalone), nameof(SaveLoadStandalone.DeleteFile))]
    private static class P_Delete { private static void Prefix(SaveLoadStandalone __instance, ref string __0) { if (NativeCode.Is<SaveLoadStandalone>(__instance)) __0 = Redirect(__0); } }
}

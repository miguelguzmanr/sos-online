using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace SosOnline.Game;

/// <summary>
/// Host: the character each friend (by Steam id) last played, per save slot, kept across game starts in
/// <c>SosOnlineCharacters.json</c> next to the saves the host plays from (a test run's sandbox in test runs). A friend
/// who joins that save again gets their character back when it's free (<see cref="Net.Session"/>, UpdateCharacters).
/// Only the host reads it, and its answers reach the other PCs as a scheduled message, so it can't split them.
/// </summary>
internal static class CharacterMemory
{
    public const string FileName = "SosOnlineCharacters.json";

    /// <summary>Slot -1: a save the game didn't name (kept for this run only).</summary>
    private static readonly Dictionary<int, Dictionary<ulong, string>> _bySlot = new();
    private static string _loadedFrom;

    private static string FilePath => Path.Combine(SaveMirror.Active ? SaveMirror.MirrorDir : SaveMirror.OwnDir, FileName);

    public static string Get(int slot, ulong player)
    {
        Load();
        return _bySlot.TryGetValue(slot, out var map) && map.TryGetValue(player, out var c) ? c : null;
    }

    public static void Set(int slot, ulong player, string character)
    {
        if (string.IsNullOrEmpty(character)) return;
        Load();
        if (!_bySlot.TryGetValue(slot, out var map)) _bySlot[slot] = map = new();
        if (map.TryGetValue(player, out var old) && old == character) return;
        map[player] = character;
        if (slot >= 0) Save();
    }

    /// <summary>(Re)reads the file when the folder it belongs to changed (a guest's mirror, a sandbox), or once.</summary>
    private static void Load()
    {
        string path;
        try { path = FilePath; } catch { return; }
        if (path == _loadedFrom) return;
        _loadedFrom = path;
        _bySlot.Clear();
        try
        {
            if (!File.Exists(path)) return;
            var data = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(File.ReadAllText(path));
            if (data == null) return;
            foreach (var (slotKey, players) in data)
            {
                if (!int.TryParse(slotKey, out int slot) || players == null) continue;
                var map = _bySlot[slot] = new();
                foreach (var (id, character) in players)
                    if (ulong.TryParse(id, out var player) && !string.IsNullOrEmpty(character)) map[player] = character;
            }
            Plugin.Log.LogInfo($"[characters] remembered characters for {_bySlot.Count} save slot(s) from {FileName}");
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[characters] couldn't read {path}: {e.Message}"); }
    }

    private static void Save()
    {
        var data = new SortedDictionary<string, SortedDictionary<string, string>>();
        foreach (var (slot, map) in _bySlot)
        {
            if (slot < 0 || map.Count == 0) continue;
            var players = data[slot.ToString()] = new();
            foreach (var (player, character) in map) players[player.ToString()] = character;
        }
        try
        {
            string path = _loadedFrom ?? FilePath, tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, path, true);
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[characters] couldn't write {FileName}: {e.Message}"); }
    }
}

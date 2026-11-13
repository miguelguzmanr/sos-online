using System;
using System.Runtime.InteropServices;
using System.Text;
using HarmonyLib;
using SosOnline.Game;
using SosOnline.Net;
using Steamworks;

namespace SosOnline.Steam;

/// <summary>
/// Joining from Steam itself:
///  - Cold start: Steam launches the game with "+connect_lobby &lt;id&gt;" (invite accepted, or "Join Game" while the
///    game isn't running).
///  - Game running: Steam posts GameLobbyJoinRequested_t (overlay invite) or GameRichPresenceJoinRequested_t
///    ("Join Game" in the friends list). Facepunch runs Steam in manual-dispatch mode and drops callbacks nobody
///    registered for, so we observe them on their way through Facepunch's dispatcher.
/// The actual join waits until the game reaches the title screen.
/// </summary>
internal static class SteamInvites
{
    private const int GameLobbyJoinRequested = 333; // { CSteamID lobby; CSteamID friend; }
    private const int GameRichPresenceJoinRequested = 337; // { CSteamID friend; char connect[256]; }

    private static ulong _pendingLobby;

    public static void ParseCommandLine()
    {
        var args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i].Equals("+connect_lobby", StringComparison.OrdinalIgnoreCase) && ulong.TryParse(args[i + 1], out var lobby))
            {
                _pendingLobby = lobby;
                Plugin.Log.LogInfo($"[invite] launched to join lobby {lobby}");
            }
    }

    /// <summary>Called every frame; joins a pending invite once it is safe to.</summary>
    public static void Update()
    {
        if (_pendingLobby == 0 || !GameBridge.AtTitleScreen) return;
        if (!SteamApi.TryInit()) return;
        ulong lobby = _pendingLobby;
        _pendingLobby = 0;
        Session.Instance.JoinLobby(lobby, "your friend's session");
    }

    private static void OnLobbyRequest(ulong lobby)
    {
        if (lobby == 0) return;
        _pendingLobby = lobby;
        if (!GameBridge.AtTitleScreen) UI.Toast.Show("Co-op invite accepted. Return to the title screen to join.");
    }

    private static ulong ParseConnect(string connect)
    {
        var parts = (connect ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length - 1; i++)
            if (parts[i] == "+connect_lobby" && ulong.TryParse(parts[i + 1], out var id)) return id;
        return 0;
    }

    [HarmonyPatch(typeof(Dispatch), nameof(Dispatch.ProcessCallback))]
    private static class P_ProcessCallback
    {
        private static unsafe void Prefix(Dispatch.CallbackMsg_t __0)
        {
            try
            {
                int type = (int)__0.Type;
                if (type != GameLobbyJoinRequested && type != GameRichPresenceJoinRequested) return;
                byte* data = (byte*)__0.Data;
                if (data == null) return;
                if (type == GameLobbyJoinRequested)
                {
                    ulong lobby = *(ulong*)data;
                    Plugin.Log.LogInfo($"[invite] lobby join requested: {lobby}");
                    OnLobbyRequest(lobby);
                }
                else
                {
                    string connect = Marshal.PtrToStringUTF8((IntPtr)(data + 8), Math.Max(0, Math.Min(256, __0.DataSize - 8)))?.TrimEnd('\0');
                    Plugin.Log.LogInfo($"[invite] rich presence join requested: {connect}");
                    OnLobbyRequest(ParseConnect(connect));
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[invite] {e}");
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace SosOnline.Steam;

/// <summary>
/// Minimal P/Invoke binding to the steam_api64.dll flat API the game already ships and initializes
/// (via Facepunch.Steamworks). We deliberately avoid the IL2CPP-compiled Facepunch wrapper: unused parts of it
/// are stripped, and its callback dispatch is not reachable from managed plugins. Everything here is
/// poll-based so it works with Facepunch's manual callback dispatch.
/// </summary>
internal static unsafe class SteamApi
{
    private const string Lib = "steam_api64";

    public const uint AppId = 1244090;

    // --- Accessors (versioned, must match the exports of the bundled steam_api64.dll) ---
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr SteamAPI_SteamUser_v021();
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr SteamAPI_SteamFriends_v017();
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr SteamAPI_SteamNetworkingMessages_SteamAPI_v002();

    // --- ISteamUser / ISteamFriends ---
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern ulong SteamAPI_ISteamUser_GetSteamID(IntPtr self);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr SteamAPI_ISteamFriends_GetPersonaName(IntPtr self);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern int SteamAPI_ISteamFriends_GetFriendCount(IntPtr self, int flags);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern ulong SteamAPI_ISteamFriends_GetFriendByIndex(IntPtr self, int index, int flags);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr SteamAPI_ISteamFriends_GetFriendPersonaName(IntPtr self, ulong steamId);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)][return: MarshalAs(UnmanagedType.I1)] private static extern bool SteamAPI_ISteamFriends_GetFriendGamePlayed(IntPtr self, ulong steamId, FriendGameInfo* info);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)][return: MarshalAs(UnmanagedType.I1)] private static extern bool SteamAPI_ISteamFriends_SetRichPresence(IntPtr self, byte[] key, byte[] value);

    // --- ISteamNetworkingMessages ---
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern int SteamAPI_ISteamNetworkingMessages_SendMessageToUser(IntPtr self, NetIdentity* identity, byte* data, uint size, int flags, int channel);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern int SteamAPI_ISteamNetworkingMessages_ReceiveMessagesOnChannel(IntPtr self, int channel, IntPtr* messages, int maxMessages);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)][return: MarshalAs(UnmanagedType.I1)] private static extern bool SteamAPI_ISteamNetworkingMessages_AcceptSessionWithUser(IntPtr self, NetIdentity* identity);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)][return: MarshalAs(UnmanagedType.I1)] private static extern bool SteamAPI_ISteamNetworkingMessages_CloseSessionWithUser(IntPtr self, NetIdentity* identity);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern void SteamAPI_SteamNetworkingMessage_t_Release(IntPtr message);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern int SteamAPI_ISteamNetworkingMessages_GetSessionConnectionInfo(IntPtr self, NetIdentity* identity, IntPtr connectionInfo, IntPtr quickStatus);

    // --- Lobbies / call results ---
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr SteamAPI_SteamMatchmaking_v009();
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr SteamAPI_SteamUtils_v010();
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern int SteamAPI_GetHSteamPipe();
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern ulong SteamAPI_ISteamMatchmaking_CreateLobby(IntPtr self, int lobbyType, int maxMembers);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern ulong SteamAPI_ISteamMatchmaking_JoinLobby(IntPtr self, ulong lobby);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern void SteamAPI_ISteamMatchmaking_LeaveLobby(IntPtr self, ulong lobby);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern int SteamAPI_ISteamMatchmaking_GetNumLobbyMembers(IntPtr self, ulong lobby);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern ulong SteamAPI_ISteamMatchmaking_GetLobbyMemberByIndex(IntPtr self, ulong lobby, int index);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern ulong SteamAPI_ISteamMatchmaking_GetLobbyOwner(IntPtr self, ulong lobby);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)][return: MarshalAs(UnmanagedType.I1)] private static extern bool SteamAPI_ISteamMatchmaking_SetLobbyData(IntPtr self, ulong lobby, byte[] key, byte[] value);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr SteamAPI_ISteamMatchmaking_GetLobbyData(IntPtr self, ulong lobby, byte[] key);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)][return: MarshalAs(UnmanagedType.I1)] private static extern bool SteamAPI_ISteamMatchmaking_SetLobbyJoinable(IntPtr self, ulong lobby, [MarshalAs(UnmanagedType.I1)] bool joinable);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern void SteamAPI_ISteamFriends_ActivateGameOverlayInviteDialog(IntPtr self, ulong lobby);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)][return: MarshalAs(UnmanagedType.I1)] private static extern bool SteamAPI_ISteamUtils_IsAPICallCompleted(IntPtr self, ulong call, [MarshalAs(UnmanagedType.I1)] out bool failed);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)][return: MarshalAs(UnmanagedType.I1)] private static extern bool SteamAPI_ISteamUtils_GetAPICallResult(IntPtr self, ulong call, void* result, int size, int expectedCallback, [MarshalAs(UnmanagedType.I1)] out bool failed);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)][return: MarshalAs(UnmanagedType.I1)] private static extern bool SteamAPI_ManualDispatch_GetAPICallResult(int pipe, ulong call, void* result, int size, int expectedCallback, [MarshalAs(UnmanagedType.I1)] out bool failed);

    private const int LobbyCreatedCallback = 513;
    private const int LobbyEnterCallback = 504;
    private const int LobbyTypeFriendsOnly = 1;

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct LobbyCreated { public int Result; public ulong LobbyId; }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct LobbyEnter { public ulong LobbyId; public uint Permissions; [MarshalAs(UnmanagedType.I1)] public bool Locked; public uint Response; }

    private static IntPtr _matchmaking, _utils;

    public static ulong CreateLobbyAsync(int maxMembers) => SteamAPI_ISteamMatchmaking_CreateLobby(_matchmaking, LobbyTypeFriendsOnly, maxMembers);
    public static ulong JoinLobbyAsync(ulong lobby) => SteamAPI_ISteamMatchmaking_JoinLobby(_matchmaking, lobby);
    public static void LeaveLobby(ulong lobby) { if (lobby != 0) SteamAPI_ISteamMatchmaking_LeaveLobby(_matchmaking, lobby); }
    public static ulong LobbyOwner(ulong lobby) => SteamAPI_ISteamMatchmaking_GetLobbyOwner(_matchmaking, lobby);
    public static void SetLobbyData(ulong lobby, string key, string value) => SteamAPI_ISteamMatchmaking_SetLobbyData(_matchmaking, lobby, Z(key), Z(value));
    public static string GetLobbyData(ulong lobby, string key) => Utf8(SteamAPI_ISteamMatchmaking_GetLobbyData(_matchmaking, lobby, Z(key)));
    public static void SetLobbyJoinable(ulong lobby, bool joinable) => SteamAPI_ISteamMatchmaking_SetLobbyJoinable(_matchmaking, lobby, joinable);
    public static void OpenInviteDialog(ulong lobby) => SteamAPI_ISteamFriends_ActivateGameOverlayInviteDialog(_friends, lobby);

    public static List<ulong> LobbyMembers(ulong lobby)
    {
        var list = new List<ulong>();
        int n = SteamAPI_ISteamMatchmaking_GetNumLobbyMembers(_matchmaking, lobby);
        for (int i = 0; i < n; i++) list.Add(SteamAPI_ISteamMatchmaking_GetLobbyMemberByIndex(_matchmaking, lobby, i));
        return list;
    }

    /// <summary>0 = pending, 1 = done (lobby id set), -1 = failed.</summary>
    public static int PollLobbyCreated(ulong call, out ulong lobby)
    {
        lobby = 0;
        LobbyCreated r;
        int state = PollCallResult(call, &r, sizeof(LobbyCreated), LobbyCreatedCallback);
        if (state != 1) return state;
        if (r.Result != 1) return -1;
        lobby = r.LobbyId;
        return 1;
    }

    /// <summary>0 = pending, 1 = entered, -1 = failed.</summary>
    public static int PollLobbyEntered(ulong call, out ulong lobby)
    {
        lobby = 0;
        LobbyEnter r;
        int state = PollCallResult(call, &r, sizeof(LobbyEnter), LobbyEnterCallback);
        if (state != 1) return state;
        if (r.Response != 1) return -1; // k_EChatRoomEnterResponseSuccess
        lobby = r.LobbyId;
        return 1;
    }

    private static int PollCallResult(ulong call, void* result, int size, int expected)
    {
        if (call == 0) return -1;
        bool failed;
        if (!SteamAPI_ISteamUtils_IsAPICallCompleted(_utils, call, out failed)) return 0;
        if (failed) return -1;
        if (SteamAPI_ISteamUtils_GetAPICallResult(_utils, call, result, size, expected, out failed) && !failed) return 1;
        // Facepunch runs Steam in manual-dispatch mode; fall back to the pipe-level accessor.
        if (SteamAPI_ManualDispatch_GetAPICallResult(SteamAPI_GetHSteamPipe(), call, result, size, expected, out failed) && !failed) return 1;
        return -1;
    }

    /// <summary>Lobby id a friend is currently in (0 if none / not in Sea of Stars).</summary>
    public static ulong FriendLobby(ulong friend)
    {
        FriendGameInfo info;
        if (SteamAPI_ISteamFriends_GetFriendGamePlayed(_friends, friend, &info) && (uint)(info.GameId & 0xFFFFFF) == AppId)
            return info.LobbyId;
        return 0;
    }

    public const int SendUnreliable = 0;
    public const int SendNoNagle = 1;
    public const int SendReliable = 8;
    public const int SendAutoRestartBrokenSession = 32;

    private const int FriendFlagImmediate = 0x04;
    private const int IdentityTypeSteamId = 16;

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct FriendGameInfo
    {
        public ulong GameId;
        public uint GameIp;
        public ushort GamePort;
        public ushort QueryPort;
        public ulong LobbyId;
    }

    /// <summary>SteamNetworkingIdentity: int type, int size, 128-byte union. 136 bytes total.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 136)]
    private struct NetIdentity
    {
        [FieldOffset(0)] public int Type;
        [FieldOffset(4)] public int Size;
        [FieldOffset(8)] public ulong SteamId;
    }

    // SteamNetworkingMessage_t field offsets (x64).
    private const int MsgDataOffset = 0;
    private const int MsgSizeOffset = 8;
    private const int MsgIdentityOffset = 16;

    private static IntPtr _user, _friends, _messages;
    public static bool Available { get; private set; }
    public static string InitError { get; private set; }

    public static bool TryInit()
    {
        if (Available) return true;
        try
        {
            _user = SteamAPI_SteamUser_v021();
            _friends = SteamAPI_SteamFriends_v017();
            _messages = SteamAPI_SteamNetworkingMessages_SteamAPI_v002();
            _matchmaking = SteamAPI_SteamMatchmaking_v009();
            _utils = SteamAPI_SteamUtils_v010();
            Available = _user != IntPtr.Zero && _friends != IntPtr.Zero && _messages != IntPtr.Zero
                        && _matchmaking != IntPtr.Zero && _utils != IntPtr.Zero && LocalSteamId != 0;
            if (!Available) InitError = "Steam interfaces not available (is Steam running?)";
        }
        catch (Exception e)
        {
            InitError = e.Message;
            Available = false;
        }
        return Available;
    }

    public static ulong LocalSteamId => _user == IntPtr.Zero ? 0 : SteamAPI_ISteamUser_GetSteamID(_user);

    public static string LocalName => Utf8(SteamAPI_ISteamFriends_GetPersonaName(_friends));

    public static string FriendName(ulong id) => Utf8(SteamAPI_ISteamFriends_GetFriendPersonaName(_friends, id));

    public static void SetRichPresence(string key, string value)
    {
        if (!Available) return;
        SteamAPI_ISteamFriends_SetRichPresence(_friends, Z(key), Z(value));
    }

    /// <summary>Friends currently running Sea of Stars, with the lobby they're in (0 if none).</summary>
    public static List<(ulong id, string name, ulong lobby)> FriendsInGame()
    {
        var result = new List<(ulong, string, ulong)>();
        if (!Available) return result;
        int count = SteamAPI_ISteamFriends_GetFriendCount(_friends, FriendFlagImmediate);
        for (int i = 0; i < count; i++)
        {
            ulong id = SteamAPI_ISteamFriends_GetFriendByIndex(_friends, i, FriendFlagImmediate);
            FriendGameInfo info;
            if (SteamAPI_ISteamFriends_GetFriendGamePlayed(_friends, id, &info) && (uint)(info.GameId & 0xFFFFFF) == AppId)
                result.Add((id, FriendName(id), info.LobbyId));
        }
        return result;
    }

    /// <summary>
    /// Whether Steam considers the messaging session with <paramref name="steamId"/> dead
    /// (ESteamNetworkingConnectionState ClosedByPeer = 4 or ProblemDetectedLocally = 5).
    /// </summary>
    public static bool SessionLost(ulong steamId)
    {
        var self = _messages;
        if (self == IntPtr.Zero) return false;
        var identity = new NetIdentity { Type = IdentityTypeSteamId, Size = 8, SteamId = steamId };
        int state;
        try { state = SteamAPI_ISteamNetworkingMessages_GetSessionConnectionInfo(self, &identity, IntPtr.Zero, IntPtr.Zero); }
        catch { return false; }
        return state == 4 || state == 5;
    }

    public static bool Send(ulong steamId, byte[] data, int length, bool reliable, int channel)
    {
        var identity = new NetIdentity { Type = IdentityTypeSteamId, Size = 8, SteamId = steamId };
        int flags = (reliable ? SendReliable : SendUnreliable) | SendNoNagle | SendAutoRestartBrokenSession;
        fixed (byte* p = data)
        {
            int result = SteamAPI_ISteamNetworkingMessages_SendMessageToUser(_messages, &identity, p, (uint)length, flags, channel);
            return result == 1; // k_EResultOK
        }
    }

    public static void Accept(ulong steamId)
    {
        var identity = new NetIdentity { Type = IdentityTypeSteamId, Size = 8, SteamId = steamId };
        SteamAPI_ISteamNetworkingMessages_AcceptSessionWithUser(_messages, &identity);
    }

    public static void Close(ulong steamId)
    {
        var identity = new NetIdentity { Type = IdentityTypeSteamId, Size = 8, SteamId = steamId };
        SteamAPI_ISteamNetworkingMessages_CloseSessionWithUser(_messages, &identity);
    }

    private static readonly IntPtr[] RecvBuffer = new IntPtr[64];

    /// <summary>Drains pending messages on a channel.</summary>
    public static void Receive(int channel, Action<ulong, byte[]> onMessage)
    {
        if (!Available) return;
        while (true)
        {
            int n;
            fixed (IntPtr* buf = RecvBuffer)
                n = SteamAPI_ISteamNetworkingMessages_ReceiveMessagesOnChannel(_messages, channel, buf, RecvBuffer.Length);
            if (n <= 0) return;
            for (int i = 0; i < n; i++)
            {
                byte* msg = (byte*)RecvBuffer[i];
                IntPtr dataPtr = *(IntPtr*)(msg + MsgDataOffset);
                int size = *(int*)(msg + MsgSizeOffset);
                var identity = (NetIdentity*)(msg + MsgIdentityOffset);
                ulong sender = identity->Type == IdentityTypeSteamId ? identity->SteamId : 0;
                var bytes = new byte[size];
                if (size > 0) Marshal.Copy(dataPtr, bytes, 0, size);
                SteamAPI_SteamNetworkingMessage_t_Release(RecvBuffer[i]);
                if (sender != 0) onMessage(sender, bytes);
            }
            if (n < RecvBuffer.Length) return;
        }
    }

    private static byte[] Z(string s)
    {
        var b = Encoding.UTF8.GetBytes(s ?? "");
        Array.Resize(ref b, b.Length + 1);
        return b;
    }

    private static string Utf8(IntPtr p) => p == IntPtr.Zero ? "" : Marshal.PtrToStringUTF8(p) ?? "";
}

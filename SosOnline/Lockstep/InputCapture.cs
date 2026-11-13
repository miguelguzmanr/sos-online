using System.Linq;
using Rewired;

namespace SosOnline.Lockstep;

/// <summary>Reads what the local hardware is doing, through Rewired's original (unpatched) getters.</summary>
internal static class InputCapture
{
    public static InputSnapshot Capture(Player player, InputSnapshot previous)
    {
        var s = new InputSnapshot();
        if (player == null || !ActionTable.Ready) return s;
        InputOverride.Bypass = true;
        try
        {
            var ids = ActionTable.Ids;
            for (int i = 0; i < ids.Length; i++)
            {
                int id = ids[i];
                ActionBits bits = 0;
                if (player.GetButton(id)) bits |= ActionBits.Button;
                if (player.GetButtonDown(id)) bits |= ActionBits.ButtonDown;
                if (player.GetButtonUp(id)) bits |= ActionBits.ButtonUp;
                if (player.GetNegativeButton(id)) bits |= ActionBits.NegButton;
                if (player.GetNegativeButtonDown(id)) bits |= ActionBits.NegButtonDown;
                float axis = player.GetAxis(id);
                float raw = player.GetAxisRaw(id);

                // Timed states (short/long press, repeat, double-press) only matter around activity.
                bool prevActive = previous != null && i < previous.Bits.Length && previous.Bits[i] != 0;
                if (bits != 0 || prevActive)
                {
                    if (player.GetButtonDoublePressHold(id)) bits |= ActionBits.DoublePressHold;
                    if (player.GetButtonShortPress(id)) bits |= ActionBits.ShortPress;
                    if (player.GetButtonLongPress(id)) bits |= ActionBits.LongPress;
                    if (player.GetButtonRepeating(id)) bits |= ActionBits.Repeating;
                    if (player.GetNegativeButtonDoublePressHold(id)) bits |= ActionBits.NegDoublePressHold;
                    if (player.GetNegativeButtonShortPress(id)) bits |= ActionBits.NegShortPress;
                    if (player.GetNegativeButtonLongPress(id)) bits |= ActionBits.NegLongPress;
                    if (player.GetNegativeButtonRepeating(id)) bits |= ActionBits.NegRepeating;
                }

                s.Bits[i] = bits;
                s.Axis[i] = InputSnapshot.Quantize(axis);
                s.AxisRaw[i] = InputSnapshot.Quantize(raw);
            }
        }
        finally
        {
            InputOverride.Bypass = false;
        }
        ApplyAliases(s);
        return s;
    }

    private static readonly (string to, string from)[] Aliases =
    {
        // The Assign Players screen uses its own actions, which the game only binds for gamepads (it is
        // pad-only offline). Online every player needs it, so fall back to the regular UI actions.
        ("UIAssignConfirm", "UI_Confirm"),
        ("UIAssignBack", "UI_Back"),
        ("UIAssignMoveHorizontal", "UIMoveHorizontal"),
    };

    private static int[] _aliasTo, _aliasFrom;

    private static void ApplyAliases(InputSnapshot s)
    {
        if (_aliasTo == null)
        {
            _aliasTo = Aliases.Select(a => ActionTable.IndexOf(a.to)).ToArray();
            _aliasFrom = Aliases.Select(a => ActionTable.IndexOf(a.from)).ToArray();
        }
        for (int i = 0; i < _aliasTo.Length; i++)
        {
            int to = _aliasTo[i], from = _aliasFrom[i];
            if (to < 0 || from < 0 || s.Bits[to] != 0 || s.Axis[to] != 0) continue;
            s.Bits[to] = s.Bits[from];
            s.Axis[to] = s.Axis[from];
            s.AxisRaw[to] = s.AxisRaw[from];
        }
    }
}

/// <summary>
/// During a session every peer owns exactly one slot. All local devices are routed to that slot's Rewired
/// player (and to the System player, which drives menus). The game's own auto-assignment is suspended so it
/// can't move devices around; see <see cref="Patches.InputManagerPatches"/>.
/// </summary>
internal static class ControllerRouting
{
    /// <summary>Set while the mod itself (re)assigns devices; the game's own reassignment is ignored in sessions.</summary>
    public static bool Routing { get; private set; }

    public static void RouteLocalDevicesTo(Player slotPlayer)
    {
        if (slotPlayer == null) return;
        Routing = true;
        try { RouteLocalDevicesToCore(slotPlayer); } finally { Routing = false; }
    }

    private static void RouteLocalDevicesToCore(Player slotPlayer)
    {
        var system = ReInput.players.GetSystemPlayer();
        var players = ReInput.players.AllPlayers.ToList();
        for (int i = 0; i < players.Count; i++)
        {
            var p = players[i];
            bool mine = p.id == slotPlayer.id || p.id == system.id;
            p.controllers.hasKeyboard = mine;
            p.controllers.hasMouse = mine;
        }

        var joysticks = ReInput.controllers.Joysticks.ToList();
        for (int j = 0; j < joysticks.Count; j++)
        {
            var joy = joysticks[j];
            slotPlayer.controllers.AddController(joy.Cast<Controller>(), true);
            system.controllers.AddController(joy.Cast<Controller>(), false);
        }
    }

    public static void RouteNewJoystick(Player slotPlayer, Joystick joy)
    {
        if (slotPlayer == null || joy == null) return;
        Routing = true;
        try { RouteNewJoystickCore(slotPlayer, joy); } finally { Routing = false; }
    }

    private static void RouteNewJoystickCore(Player slotPlayer, Joystick joy)
    {
        slotPlayer.controllers.AddController(joy.Cast<Controller>(), true);
        ReInput.players.GetSystemPlayer().controllers.AddController(joy.Cast<Controller>(), false);
    }
}

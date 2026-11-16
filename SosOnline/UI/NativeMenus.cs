using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Rewired;
using SosOnline.Game;
using SosOnline.Lockstep;
using SosOnline.Net;
using SosOnline.Steam;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace SosOnline.UI;

/// <summary>
/// The "Online" entries, built from the game's own widgets so they look, sound and navigate like the rest of the UI:
///  - Title screen: a copy of the Options button. Choosing it swaps the title's button list for our own entries
///    (copies of the same button) with a status line above them; "Back" restores the game's list.
///  - Pause menu: an item added through the game's own PauseMenu.AddPauseMenuItem, opening the game's
///    two-choice popup (the one used for "Return to title?").
///
/// Determinism: during a running session the pause menu is opened and navigated with synchronized input, so every
/// peer opens the same popup and receives the same answer on the same frame. Choices made there act as
/// deterministic commands ("End co-op session" stops lockstep on every peer at that frame).
/// </summary>
internal static class NativeMenus
{
    private const string TitleButtonName = "SosOnline_OnlineButton";
    private const string EntryName = "SosOnline_Entry";
    private const string StatusName = "SosOnline_Status";
    private const string PauseItemId = "sosonline";

    private static Sabotage.Localization.LocalizationId OnlineId => OnlineText.Static("online", "Online");

    // ================================================================== popups (two choices max)

    private static bool _popupOpen;
    private static int _popupToken;

    /// <summary>
    /// The game's generic popup: a message, up to two choices; onAnswer(index), -1 for cancel. False when it can't
    /// open now (another one is open, or the UI isn't up).
    /// </summary>
    public static bool Popup(string message, string yes, string no, Action<int> onAnswer)
    {
        var ui = Manager<UIManager>.instance;
        if (ui == null || _popupOpen) return false;
        try
        {
            var p = new GenericPopupScreenParams();
            p.Init(message, true, no != null ? 1 : 0);
            p.choices.Clear();
            p.AddChoice(OnlineText.Dynamic(yes));
            if (no != null) p.AddChoice(OnlineText.Dynamic(no));
            _popupOpen = true;
            int token = ++_popupToken;
            Action<View> done = view =>
            {
                var screen = view?.TryCast<GenericPopupScreen>();
                if (screen == null) { if (token == _popupToken) _popupOpen = false; return; }
                bool handled = false;
                Action<GenericPopupScreen, bool, int> answered = (_, success, index) =>
                {
                    // The game reuses its popup view and adds its own handler with +=, so ours stays on the view after
                    // this answer. Without these guards, the game's next question (e.g. "Overwrite this save?") also
                    // ran our old choice: a Yes there ended the co-op session.
                    if (handled || token != _popupToken || !_popupOpen) return;
                    handled = true;
                    _popupOpen = false;
                    try { screen.onPopupAnswered = null; } catch { }
                    try { onAnswer?.Invoke(success ? index : -1); }
                    catch (Exception e) { Plugin.Log.LogError($"[menu] {e}"); }
                };
                screen.onPopupAnswered = answered;
                FitButtons(screen);
            };
            ui.InstantiateViewAsync(Il2CppType.Of<GenericPopupScreen>(), EScreenLayers.MODAL, p.Cast<IViewParams>(), true,
                AnimatorUpdateMode.UnscaledTime, done);
            return true;
        }
        catch (Exception e)
        {
            _popupOpen = false;
            Plugin.Log.LogError($"[menu] popup failed: {e}");
            return false;
        }
    }

    /// <summary>Space left and right of a choice's label inside its highlight, in the popup's reference pixels.</summary>
    private const float ChoicePadX = 20f;

    /// <summary>
    /// Widest a popup choice may grow (padding included), about two thirds of the popup, so two choices still fit
    /// side by side. Keep labels within it (about 25 characters); a longer one is cut with an ellipsis and logged.
    /// </summary>
    private const float MaxChoiceWidth = 200f;

    private static bool _loggedPopupLayout;

    /// <summary>
    /// The game's popup buttons have a fixed width made for its own short choices ("Yes", "No"): a longer label of
    /// ours ran past the highlight. Widens each button, and every fixed-width part of it (the highlight), to its
    /// label; stretched parts follow the button. The buttons' container lays them out again.
    /// </summary>
    private static void FitButtons(GenericPopupScreen screen)
    {
        try
        {
            var buttons = screen.buttons;
            if (buttons == null) return;
            Canvas.ForceUpdateCanvases();
            for (int i = 0; i < buttons.Count; i++)
            {
                var b = buttons[i];
                var text = b?.textfield;
                if (text == null) continue;
                var rt = b.GetComponent<RectTransform>();
                float width = rt.rect.width;
                float need = Mathf.Ceil(text.GetPreferredValues(text.text, float.PositiveInfinity, float.PositiveInfinity).x + 2 * ChoicePadX);
                if (!_loggedPopupLayout)
                    Plugin.Log.LogInfo($"[menu] popup choice '{text.text}': button {width:F0}, label {need - 2 * ChoicePadX:F0}, text rect {text.rectTransform.rect.width:F0}");
                if (need > MaxChoiceWidth)
                {
                    Plugin.Log.LogWarning($"[menu] popup choice '{text.text}' is too long for a button ({need:F0} > {MaxChoiceWidth:F0}); shortened");
                    need = MaxChoiceWidth;
                    text.enableWordWrapping = false;
                    text.overflowMode = TextOverflowModes.Ellipsis;
                }
                if (need <= width) continue;

                // Fixed-width children as wide as the button (highlight, label box) grow with it.
                foreach (var child in b.GetComponentsInChildren<RectTransform>(true))
                    if (child != rt && Mathf.Approximately(child.anchorMin.x, child.anchorMax.x) && Mathf.Abs(child.rect.width - width) < 2f)
                        child.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, need);
                rt.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, need);
                var le = b.GetComponent<LayoutElement>() ?? b.gameObject.AddComponent<LayoutElement>();
                le.minWidth = le.preferredWidth = need;
            }
            _loggedPopupLayout = true;
            if (screen.buttonsContainer != null)
                LayoutRebuilder.ForceRebuildLayoutImmediate(screen.buttonsContainer.GetComponent<RectTransform>());
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[menu] popup buttons not resized: {e.Message}"); }
    }

    private static void Confirm(string message, string yes, Action onYes, string no = "Back", Action onNo = null) =>
        _ = Popup(message, yes, no, i => { if (i == 0) onYes?.Invoke(); else onNo?.Invoke(); });

    // ================================================================== title screen

    private static TitleScreen _title;
    private static readonly List<GameObject> _hiddenOriginals = new();
    private static readonly List<UIButton> _entries = new();
    private static TextMeshProUGUI _status;
    private static Func<IEnumerable<(string label, Action action)>> _currentPage;
    private static Phase _pagePhase;
    private static int _pagePeers;
    private static float _nextStatusRefresh;
    public static bool SubmenuOpen { get; private set; }

    [HarmonyPatch(typeof(TitleScreen), nameof(TitleScreen.ShowChoices))]
    private static class P_TitleShowChoices { private static void Postfix(TitleScreen __instance) { if (NativeCode.Is<TitleScreen>(__instance)) InjectTitleButton(__instance); } }

    [HarmonyPatch(typeof(TitleScreen), nameof(TitleScreen.RefreshButtons))]
    private static class P_TitleRefresh { private static void Postfix(TitleScreen __instance) { if (NativeCode.Is<TitleScreen>(__instance)) InjectTitleButton(__instance); } }

    private static void InjectTitleButton(TitleScreen title)
    {
        try
        {
            _title = title;
            var template = title.optionsButton;
            if (template == null) return;
            var parent = template.transform.parent;
            var existing = parent.Find(TitleButtonName);
            UIButton button;
            if (existing == null)
            {
                button = CloneButton(template, TitleButtonName, OnlineId, "Online", OpenTitleMenu);
                button.transform.SetSiblingIndex(template.transform.GetSiblingIndex() + 1);
                Plugin.Log.LogInfo("[menu] title screen Online button added");
            }
            else button = existing.GetComponent<UIButton>();
            if (!SubmenuOpen) SetActive(button.gameObject, template.gameObject.activeSelf);
            SpliceNavigation(template, button);
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"[menu] title injection failed: {e}");
        }
    }

    private static UIButton CloneButton(UIButton template, string name, Sabotage.Localization.LocalizationId id, string text, Action onClick)
    {
        var go = Object.Instantiate(template.gameObject, template.transform.parent);
        go.name = name;
        var button = go.GetComponent<UIButton>();
        // Persistent listeners (e.g. "open Options") are copied with the object, so replace the event itself.
        button.onClick = new UnityEvent();
        if (onClick != null) button.onClick.AddListener((UnityAction)onClick);
        SetLabel(go, id, text);
        return button;
    }

    /// <summary>
    /// Buttons can carry several labels (e.g. a separate one for the highlighted state), so relabel all of them.
    /// </summary>
    private static void SetLabel(GameObject go, Sabotage.Localization.LocalizationId id, string text)
    {
        var localizers = go.GetComponentsInChildren<Sabotage.Localization.TextLocalizer>(true);
        foreach (var l in localizers) l.LocalizeText(id);
        var tb = go.GetComponent<UITextButton>();
        if (tb != null) tb.SetText(text);
        foreach (var tmp in go.GetComponentsInChildren<TextMeshProUGUI>(true)) tmp.text = text;
    }

    private static void SetActive(GameObject go, bool active)
    {
        if (go != null && go.activeSelf != active) go.SetActive(active);
    }

    /// <summary>If the title list uses explicit navigation, insert <paramref name="ours"/> right below <paramref name="above"/>.</summary>
    private static void SpliceNavigation(Selectable above, Selectable ours)
    {
        var nav = above.navigation;
        if (nav.mode != Navigation.Mode.Explicit || nav.selectOnDown == ours) return;
        var below = nav.selectOnDown;
        nav.selectOnDown = ours;
        above.navigation = nav;
        var n = ours.navigation;
        n.mode = Navigation.Mode.Explicit;
        n.selectOnUp = above;
        n.selectOnDown = below;
        ours.navigation = n;
        if (below != null) { var b = below.navigation; b.selectOnUp = ours; below.navigation = b; }
    }

    // ---------------------------------------------------------------- submenu

    internal static void OpenTitleMenu() => ShowPage(MainPage);

    /// <summary>Test harness: press the visible entry with this label.</summary>
    internal static void ClickEntry(string label) => ClickEntry(_labels.FindIndex(l => l == label));

    /// <summary>Test harness: press the n-th visible entry.</summary>
    internal static void ClickEntry(int index)
    {
        if (index >= 0 && index < _entries.Count && _entries[index].gameObject.activeSelf) _entries[index].onClick.Invoke();
    }

    private static void ShowPage(Func<IEnumerable<(string label, Action action)>> page)
    {
        if (_title == null) return;
        try
        {
            var template = _title.optionsButton;
            var container = template.transform.parent;
            // The title screen is rebuilt when the game returns to it: buttons made for the old one are gone.
            _entries.RemoveAll(b => !Alive(b) || b.transform.parent != container);
            _hiddenOriginals.RemoveAll(go => !Alive(go));
            if (!SubmenuOpen)
            {
                _hiddenOriginals.Clear();
                for (int i = 0; i < container.childCount; i++)
                {
                    var child = container.GetChild(i).gameObject;
                    if (child.activeSelf && !child.name.StartsWith(EntryName, StringComparison.Ordinal))
                    {
                        _hiddenOriginals.Add(child);
                        child.SetActive(false);
                    }
                }
                SubmenuOpen = true;
            }

            // Rebuilding the page that is showing (a toggle, a player joining): keep the cursor on the same entry.
            string keep = page == _currentPage ? SelectedEntryKey() : null;
            _currentPage = page;
            _pagePhase = Session.Instance.Phase;
            _pagePeers = Session.Instance.Peers.Count();
            var items = page().ToList();

            // Reuse entry buttons; create more as needed.
            while (_entries.Count < items.Count)
            {
                var b = CloneButton(template, EntryName + _entries.Count, OnlineText.Dynamic(""), "", null);
                _entries.Add(b);
            }
            for (int i = 0; i < _entries.Count; i++)
            {
                var b = _entries[i];
                bool used = i < items.Count;
                SetActive(b.gameObject, used);
                if (!used) continue;
                b.transform.SetAsLastSibling();
                var (label, action) = items[i];
                b.onClick = new UnityEvent();
                var captured = action;
                b.onClick.AddListener((UnityAction)(Action)(() => captured?.Invoke()));
                SetLabel(b.gameObject, OnlineText.Dynamic(label), label);
            }

            // Vertical wrap-around navigation through the visible entries.
            for (int i = 0; i < items.Count; i++)
            {
                var n = _entries[i].navigation;
                n.mode = Navigation.Mode.Explicit;
                n.selectOnUp = _entries[(i - 1 + items.Count) % items.Count];
                n.selectOnDown = _entries[(i + 1) % items.Count];
                n.selectOnLeft = n.selectOnRight = null;
                _entries[i].navigation = n;
            }

            _labels.Clear();
            _labels.AddRange(items.Select(it => it.label));
            UpdateStatus(container);
            int at = keep == null ? -1 : _labels.FindIndex(l => EntryKey(l) == keep);
            Select(_entries[Math.Max(at, 0)]);
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"[menu] submenu failed: {e}");
            CloseSubmenu(true);
        }
    }

    private static readonly List<string> _labels = new();

    /// <summary>An entry's label without its current value ("Open to friends: on" → "Open to friends").</summary>
    private static string EntryKey(string label)
    {
        int colon = label.IndexOf(':');
        return colon >= 0 ? label[..colon] : label;
    }

    private static string SelectedEntryKey()
    {
        try
        {
            var selected = EventSystem.current?.currentSelectedGameObject;
            if (selected == null) return null;
            for (int i = 0; i < _labels.Count && i < _entries.Count; i++)
                if (_entries[i] != null && _entries[i].gameObject == selected) return EntryKey(_labels[i]);
        }
        catch { }
        return null;
    }

    /// <summary>The game's cancel (B / Esc) on any player, read straight from Rewired: no lockstep at the title.</summary>
    private static bool BackPressed()
    {
        try
        {
            if (!ReInput.isReady) return false;
            foreach (var p in ReInput.players.Players.ToList())
                if (p != null && p.GetButtonDown("UI_Back")) return true;
        }
        catch { }
        return false;
    }

    /// <summary>Not destroyed (a C# null check misses destroyed Unity objects).</summary>
    private static bool Alive(Object o)
    {
        try { return o != null && o.GetInstanceID() != 0 && o; }
        catch { return false; }
    }

    private static void Select(Selectable s)
    {
        if (s == null) return;
        try
        {
            EventSystem.current?.SetSelectedGameObject(s.gameObject);
            s.Select();
        }
        catch { }
    }

    private static void CloseSubmenu(bool reselect)
    {
        if (!SubmenuOpen) return;
        SubmenuOpen = false;
        _currentPage = null;
        foreach (var b in _entries) if (Alive(b)) SetActive(b.gameObject, false);
        foreach (var go in _hiddenOriginals) SetActive(go, true);
        _hiddenOriginals.Clear();
        if (_status != null) SetActive(_status.gameObject, false);
        if (reselect && _title != null)
        {
            var online = _title.optionsButton.transform.parent.Find(TitleButtonName);
            if (online != null) Select(online.GetComponent<Selectable>());
        }
    }

    /// <summary>A text line above the list, styled like the game's own button labels.</summary>
    private static void UpdateStatus(Transform container)
    {
        if (_status == null)
        {
            var templateText = _title.optionsButton.GetComponentInChildren<TextMeshProUGUI>(true);
            var go = new GameObject(StatusName);
            go.transform.SetParent(container.parent, false);
            _status = go.AddComponent<TextMeshProUGUI>();
            if (templateText != null)
            {
                _status.font = templateText.font;
                _status.fontSharedMaterial = templateText.fontSharedMaterial;
                _status.fontSize = templateText.fontSize;
                _status.color = new Color(1f, 0.88f, 0.55f);
            }
            _status.alignment = TextAlignmentOptions.Top;
            _status.enableWordWrapping = true;
            var rt = _status.rectTransform;
            var crt = container.GetComponent<RectTransform>();
            rt.anchorMin = crt.anchorMin;
            rt.anchorMax = crt.anchorMax;
            // Below the button list, above the copyright line.
            rt.pivot = new Vector2(0.5f, 1f);
            rt.sizeDelta = new Vector2(Mathf.Max(crt.rect.width, 760f), 140f);
            rt.anchoredPosition = crt.anchoredPosition - new Vector2(0f, crt.rect.height * crt.pivot.y + 12f);
        }
        SetActive(_status.gameObject, true);
        _status.text = StatusText(Session.Instance);
        PlaceStatus();
    }

    /// <summary>Just below the last visible entry, whatever the list length.</summary>
    private static void PlaceStatus()
    {
        if (_status == null) return;
        UIButton last = null;
        foreach (var b in _entries) if (b != null && b.gameObject.activeSelf) last = b;
        if (last == null) return;
        Canvas.ForceUpdateCanvases();
        var lastRt = last.GetComponent<RectTransform>();
        var corners = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Vector3>(4);
        lastRt.GetWorldCorners(corners);
        float bottom = Mathf.Min(corners[0].y, corners[3].y);
        float centerX = (corners[0].x + corners[3].x) * 0.5f;
        var p = _status.transform.position;
        _status.transform.position = new Vector3(centerX, bottom, p.z);
        _status.rectTransform.anchoredPosition -= new Vector2(0f, 6f);
    }

    /// <summary>Keeps the submenu current while it is open (players joining, connection progress…).</summary>
    public static void Update()
    {
        if (!SubmenuOpen || _title == null) return;
        if (!GameBridge.AtTitleScreen) { CloseSubmenu(false); return; }
        if (!_popupOpen && BackPressed())
        {
            // Same as the page's own Back entry: a sub-page goes back to the main page, the main page closes.
            if (_currentPage == MainPage) CloseSubmenu(true);
            else ShowPage(MainPage);
            return;
        }
        var s = Session.Instance;
        if (s.Phase != _pagePhase || s.Peers.Count() != _pagePeers)
        {
            // Session state changed under us: rebuild the main page (other pages may no longer apply).
            ShowPage(MainPage);
            return;
        }
        if (Time.unscaledTime >= _nextStatusRefresh)
        {
            _nextStatusRefresh = Time.unscaledTime + 0.5f;
            if (_status != null) { _status.text = StatusText(s); PlaceStatus(); }
        }
    }

    // ---------------------------------------------------------------- pages

    private static IEnumerable<(string, Action)> MainPage()
    {
        var s = Session.Instance;
        // Not a synced menu (the title screen runs no lockstep), so a per-PC entry is fine here.
        if (SosOnline.Update.AutoUpdate.RestartLabel is { } restart)
            yield return (restart, () =>
            {
                if (s.InSession) Confirm("Restart the game now? This PC leaves the co-op session.", "Restart", SosOnline.Update.AutoUpdate.Restart);
                else SosOnline.Update.AutoUpdate.Restart();
            });
        switch (s.Phase)
        {
            case Phase.Idle:
                yield return ("Host", () => s.Host());
                yield return ("Join", () => ShowPage(FriendsPage));
                break;
            case Phase.CreatingLobby:
            case Phase.JoiningLobby:
            case Phase.Connecting:
                yield return ("Cancel", () => s.Leave());
                break;
            case Phase.Lobby:
            case Phase.AwaitingReady:
                if (s.IsHost)
                {
                    if (s.Phase == Phase.Lobby) yield return ("Start", () => ShowPage(SavesPage));
                    if (!s.UsingUdp) yield return ("Invite friends", () => s.InviteFriends());
                    yield return ($"Input delay: {(s.ConfiguredDelay == 0 ? "auto" : s.ConfiguredDelay.ToString())}", () => ShowPage(DelayPage));
                    yield return ("Close session", () => s.Leave());
                }
                else yield return ("Leave session", () => s.Leave());
                break;
        }
        if (s.InSession && !s.IsHost) yield return ("Send logs to the host", () => s.SendLogsToHost());
        else if (s.IsHost && s.Peers.Any()) yield return ("Ask friends for their logs", () => s.AskForLogs());
        if (!s.InSession || s.IsHost)
            yield return ($"Open to friends: {(Settings.OpenToFriends ? "on" : "off")}", () => { Settings.OpenToFriends = !Settings.OpenToFriends; ShowPage(MainPage); });
        yield return ("Open logs folder", SessionLogs.OpenFolder);
        yield return ("Back", () => CloseSubmenu(true));
    }

    private static List<(ulong id, string name, ulong lobby)> _friendsCache = new();

    private static IEnumerable<(string, Action)> FriendsPage()
    {
        if (SteamApi.TryInit()) _friendsCache = SteamApi.FriendsInGame().Where(f => f.lobby != 0).Take(6).ToList();
        foreach (var f in _friendsCache)
        {
            var friend = f;
            yield return (friend.name, () => Session.Instance.JoinFriend(friend.id));
        }
        yield return ("Refresh", () => ShowPage(FriendsPage));
        yield return ("Back", () => ShowPage(MainPage));
    }

    private static IEnumerable<(string, Action)> SavesPage()
    {
        foreach (var slot in GameBridge.ListSaveSlots().Take(6))
        {
            var sl = slot;
            yield return (sl.label, () => Session.Instance.StartFromSave(sl.index, sl.auto));
        }
        yield return ("Back", () => ShowPage(MainPage));
    }

    private static IEnumerable<(string, Action)> DelayPage()
    {
        var s = Session.Instance;
        foreach (int d in new[] { 0, 3, 5, 8 })
        {
            int delay = d;
            yield return (delay == 0 ? "Automatic" : $"{delay} frames ({delay * 17} ms)", () => { s.ConfiguredDelay = delay; ShowPage(MainPage); });
        }
        yield return ("Back", () => ShowPage(MainPage));
    }

    // ================================================================== pause menu

    [HarmonyPatch(typeof(PauseMenu), nameof(PauseMenu.Init))]
    private static class P_PauseInit
    {
        private static void Postfix(PauseMenu __instance)
        {
            if (!NativeCode.Is<PauseMenu>(__instance)) return;
            try
            {
                if (__instance.GetItem(PauseItemId) != null) return;
                __instance.AddPauseMenuItem(PauseItemId, PickPauseAnchor(__instance), OnlineId, (UnityAction)(Action)OpenPauseMenu);
            }
            catch (Exception e) { Plugin.Log.LogError($"[menu] pause injection failed: {e}"); }
        }
    }

    /// <summary>
    /// Options from the shared pause (battles and cutscenes; elsewhere the pause is local, see <see cref="LocalPause"/>)
    /// only for the host. A guest's controls page is built from the controllers assigned to that player on each PC, so
    /// it is empty on every PC but the guest's and ignores the guest's Back there: the menus diverge (findings §7). The
    /// UI player is synced game state, so every PC skips it on the same frame.
    /// </summary>
    [HarmonyPatch(typeof(PauseMenu), nameof(PauseMenu.Options))]
    private static class P_PauseOptions
    {
        private static bool Prefix(PauseMenu __instance)
        {
            if (!NativeCode.Is<PauseMenu>(__instance) || Session.Instance.Phase != Phase.Running) return true;
            int slot = -1;
            try { slot = GameBridge.SlotOf(GameBridge.Coop?.GetUIInputsPlayer()); } catch { }
            if (slot <= 0) return true;
            Plugin.Log.LogInfo($"[menu] f{Session.Instance.Engine.AppliedFrame} Options from the shared pause blocked for P{slot + 1}");
            if (slot == Session.Instance.LocalSlot)
                Toast.Show("Open Options from your own pause screen.");
            return false;
        }
    }

    private static bool _loggedPauseIds;

    private static string PickPauseAnchor(PauseMenu menu)
    {
        var ids = new List<string>();
        var items = menu.pauseMenuItems;
        for (int i = 0; i < items.Count; i++) ids.Add(items[i].id);
        if (!_loggedPauseIds) { _loggedPauseIds = true; Plugin.Log.LogInfo("[menu] pause items: " + string.Join(", ", ids)); }
        var match = ids.FirstOrDefault(id => id.IndexOf("option", StringComparison.OrdinalIgnoreCase) >= 0);
        return match ?? (ids.Count > 0 ? ids[0] : "");
    }

    /// <summary>
    /// While lockstep runs this executes on every peer on the same frame (the pause menu is driven by synced input),
    /// so it only does deterministic things.
    /// </summary>
    /// <summary>Test harness: the pause menu's Online popup, as if chosen from the pause menu.</summary>
    internal static void OpenPauseMenuForTest() => OpenPauseMenu();

    private static void OpenPauseMenu()
    {
        var s = Session.Instance;
        if (s.Phase == Phase.Running)
        {
            // Runs on every peer on the same frame: the choices may only depend on synced game state (who opened the
            // menu), never on network state, so each answer means the same thing everywhere. Only the text may differ.
            int slot = -1;
            try { slot = GameBridge.SlotOf(GameBridge.Coop?.GetUIInputsPlayer()); } catch { }
            if (slot > 0)
            {
                Confirm($"{PlayersLine(s)}\nLeave the co-op session, P{slot + 1}? Your character goes back to the party AI; the others keep playing.",
                    "Leave session", () => s.LeaveSlotDeterministic(slot));
                return;
            }
            string waiting = s.HasPending ? $"\nJoining: {string.Join(", ", s.PendingNames)} (they drop in as soon as the game allows saving)." : "";
            Confirm($"{PlayersLine(s)}{waiting}", "End session for everyone",
                () => s.EndSessionDeterministic("The host ended the co-op session"));
            return;
        }
        if (!s.InSession && SosOnline.Update.AutoUpdate.Downloaded != null)
        {
            // Not synced (no lockstep runs here), so the choice may depend on this PC's download. Friends get the
            // update too, so playing together waits for the restart anyway.
            Confirm($"Update {SosOnline.Update.AutoUpdate.Current.ToString(3)} -> {SosOnline.Update.AutoUpdate.Downloaded.ToString(3)} is downloaded.\nFriends need the same version to play together.",
                "Restart to update", SosOnline.Update.AutoUpdate.Restart);
            return;
        }
        if (!s.InSession)
        {
            Confirm("Open your game to friends?\nA Steam lobby stays open while you play, and friends who join drop in right here.",
                "Open to friends", () => { Settings.OpenToFriends = true; s.Host(); });
            return;
        }
        if (s.IsHost && !s.UsingUdp)
            Popup(StatusText(s) + "\nFriends who join drop in right here.", "Invite friends", "Close session",
                i => { if (i == 0) s.InviteFriends(); else if (i == 1) { Settings.OpenToFriends = false; s.Leave(); } });
        else
            Confirm(StatusText(s), s.IsHost ? "Close session" : "Leave session", () => s.Leave());
    }

    // ================================================================== text

    private static string PlayersLine(Session s) => string.Join("    ",
        Enumerable.Range(0, InputOverride.SlotCount).Where(i => !string.IsNullOrEmpty(s.SlotName[i])).Select(i => $"P{i + 1} {s.SlotName[i]}"));

    private static string StatusText(Session s)
    {
        if (s.Phase == Phase.Idle)
            return string.IsNullOrEmpty(s.LastError)
                ? "Online co-op: play together with up to two friends.\nEach player controls a party member, like local co-op."
                : s.LastError;
        var lines = new List<string> { s.Status, PlayersLine(s) };
        if (s.Peers.Any()) lines.Add($"Ping {s.Peers.Max(p => p.rtt):F0} ms");
        return string.Join("\n", lines);
    }
}

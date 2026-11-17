// W6A.1: the public F8 multiplayer menu (IMGUI, drawn only while the player has it open in the original main menu) and the
// window plumbing shared with the developer menu (hotkeys, open / close, menu-input block, game font + click sound).
// Look: dark charcoal / graphite panel, one amber accent, the game's own main-menu font (MainMenu.currentProfileTxt.font)
// and click sound (MainMenu.audioClick) when present; scaled to the screen height (1080p reference), short fade-in.
// Contents: Host Game / Join Game / session status / join code / errors / version. No test campaign, story or debug items
// (those live in the developer menu, MultiplayerMenu.Dev.cs).
// While a window is open the original uGUI main menu takes no clicks (its EventSystem is disabled and restored on close; the
// v0.16.1 join loading screen takes over that state when it shows).
// Cost: nothing while closed (one key check per frame); while open: IMGUI layout of ~20 controls, cached strings, textures
// created once per process.
using System;
using HarmonyLib;
using TS2MP.Shared;
using UnityEngine;

namespace TS2MP.Plugin
{
    internal sealed partial class MultiplayerMenu
    {
        string _portText;
        int _tab;                 // 0 host, 1 join
        float _openedAt;
        Behaviour _blockedEs; bool _blockTried;
        Font _gameFont; AudioSource _click; bool _assetsTried;
        string _footer;

        // ---------------------------------------------------------------- open / close / hotkeys
        void Hotkey(MenuPanel pressed)
        {
            var ctx = Context();
            bool main = MenuRules.MenuOpens(ctx);
            var next = MenuRouting.Toggle(_panel, pressed, _devAllowed, GUIUtility.keyboardControl != 0, main);
            if (next == _panel)
            {
                double now = Time.realtimeSinceStartup;
                if (!main && now >= _nextIgnoredLog) { _nextIgnoredLog = now + 30; _log.Info("MENU " + (pressed == MenuPanel.Developer ? _settings.DevMenuKey : _settings.MenuKey) + " ignored outside the main menu (context " + ctx + "; multiplayer is prepared / joined in the main menu)"); }
                return;
            }
            if (next == MenuPanel.None) CloseMenu("key");
            else OpenPanel(next);
        }

        void OpenPanel(MenuPanel p)
        {
            _panel = p; _hint = null; _openedAt = Time.realtimeSinceStartup; _blockTried = false;
            GUIUtility.keyboardControl = 0;
            if (!_assetsTried) FindGameAssets();
            _log.Info("MENU " + (p == MenuPanel.Developer ? "developer menu (" + _settings.DevMenuKey + ")" : "multiplayer menu (" + _settings.MenuKey + ")") + " opened");
        }

        void CloseMenu(string why)
        {
            _panel = MenuPanel.None;
            GUIUtility.keyboardControl = 0;
            ReleaseInputBlock();
        }

        // The original main menu (uGUI) must not react to clicks through the window.
        void SyncInputBlock()
        {
            bool want = _open && !(_loading != null && _loading.Shown);
            if (!want) { if (_blockedEs != null) ReleaseInputBlock(); return; }
            if (_blockedEs != null || _blockTried) return;
            _blockTried = true;   // one lookup per opening
            var es = CurrentEventSystem();
            if (es != null && es.enabled) { es.enabled = false; _blockedEs = es; }
        }

        void ReleaseInputBlock()
        {
            if (_blockedEs != null && Alive(_blockedEs)) _blockedEs.enabled = true;
            _blockedEs = null; _blockTried = false;
        }

        static Func<Behaviour> _esCurrent; static bool _esResolved;
        static Behaviour CurrentEventSystem()
        {
            if (!_esResolved)
            {
                _esResolved = true;
                var t = AccessTools.TypeByName("UnityEngine.EventSystems.EventSystem");
                var pi = t != null ? AccessTools.Property(t, "current") : null;
                var g = pi != null ? pi.GetGetMethod() : null;
                if (g != null) _esCurrent = () => g.Invoke(null, null) as Behaviour;
            }
            return _esCurrent != null ? _esCurrent() : null;
        }

        // The game's own main-menu font and click sound (MainMenu.currentProfileTxt : UI.Text, MainMenu.audioClick).
        void FindGameAssets()
        {
            _assetsTried = true;
            try
            {
                var mm = _menuType != null ? UnityEngine.Object.FindObjectOfType(_menuType) : null;   // once per process
                if (mm == null) { _assetsTried = false; return; }
                var txt = Fast.Getter<object>(_menuType, "currentProfileTxt");
                var t = txt != null ? txt(mm) : null;
                var fp = t != null ? t.GetType().GetProperty("font") : null;
                _gameFont = fp != null ? fp.GetValue(t, null) as Font : null;
                var snd = Fast.Getter<object>(_menuType, "audioClick");
                _click = snd != null ? snd(mm) as AudioSource : null;
                _styles = null;   // rebuild with the font
                _log.Info("MENU look: game font " + (_gameFont != null ? "'" + _gameFont.name + "'" : "not found (Unity default)") + ", click sound " + (_click != null ? "original" : "none"));
            }
            catch (Exception ex) { _log.Warn("MENU game font / click sound not found: " + ex.Message); }
        }

        void Click() { if (_click != null && Alive(_click)) _click.Play(); }

        // ---------------------------------------------------------------- styles
        sealed class Styles
        {
            public GUIStyle Panel, Overline, Title, Body, Muted, Label, Field, Primary, Secondary, Close, Tab, TabOn, Code, Error, Info, Chip, ChipWarn, Footer;
            public Texture2D Backdrop, Accent, Line, Dot, DotOk, DotWarn;
        }
        Styles _styles; readonly System.Collections.Generic.List<Texture2D> _textures = new System.Collections.Generic.List<Texture2D>();

        static readonly Color CPanel = Hex(0x18191B), CInset = Hex(0x0F1012), CBorder = Hex(0x2C2E33), CText = Hex(0xE9E4DB), CMuted = Hex(0x8E9198),
            CAmber = Hex(0xE0922F), CAmberHi = Hex(0xF0A646), CAmberLo = Hex(0xBF7A22), CDark = Hex(0x141414), CSec = Hex(0x25272B), CSecHi = Hex(0x30333A),
            CErrBg = Hex(0x2B1816), CErr = Hex(0xF0A398), CInfoBg = Hex(0x1F2125), COk = Hex(0x7FB37A);

        static Color Hex(int rgb, float a = 1f) { return new Color(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, a); }

        Texture2D Tex(Color c)
        {
            var t = new Texture2D(1, 1, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            t.SetPixel(0, 0, c); t.Apply(); _textures.Add(t); return t;
        }

        GUIStyle Box(Color bg, Color fg, int size, FontStyle fs, TextAnchor a, int padH, int padV)
        {
            var s = new GUIStyle { fontSize = size, fontStyle = fs, alignment = a, wordWrap = true, richText = false, padding = new RectOffset(padH, padH, padV, padV) };
            if (_gameFont != null) s.font = _gameFont;
            s.normal.textColor = fg; if (bg.a > 0) s.normal.background = Tex(bg);
            return s;
        }

        GUIStyle Button(Color bg, Color hi, Color lo, Color fg, int size)
        {
            var s = Box(bg, fg, size, FontStyle.Bold, TextAnchor.MiddleCenter, 18, 10);
            s.wordWrap = false;
            s.hover.background = Tex(hi); s.hover.textColor = fg; s.active.background = Tex(lo); s.active.textColor = fg;
            s.focused.background = s.normal.background; s.focused.textColor = fg;
            return s;
        }

        Styles BuildStyles()
        {
            var st = new Styles();
            st.Backdrop = Tex(new Color(0, 0, 0, 0.55f)); st.Accent = Tex(CAmber); st.Line = Tex(CBorder);
            st.Dot = Tex(CMuted); st.DotOk = Tex(COk); st.DotWarn = Tex(CAmber);
            st.Panel = Box(CPanel, CText, 15, FontStyle.Normal, TextAnchor.UpperLeft, 0, 0);
            st.Overline = Box(Color.clear, CAmber, 12, FontStyle.Bold, TextAnchor.MiddleLeft, 0, 0);
            st.Title = Box(Color.clear, CText, 27, FontStyle.Bold, TextAnchor.MiddleLeft, 0, 0);
            st.Body = Box(Color.clear, CText, 15, FontStyle.Normal, TextAnchor.UpperLeft, 0, 2);
            st.Muted = Box(Color.clear, CMuted, 13, FontStyle.Normal, TextAnchor.UpperLeft, 0, 2);
            st.Label = Box(Color.clear, CMuted, 12, FontStyle.Bold, TextAnchor.MiddleLeft, 0, 2);
            st.Field = Box(CInset, CText, 16, FontStyle.Normal, TextAnchor.MiddleLeft, 12, 10);
            st.Field.wordWrap = false; st.Field.border = new RectOffset(1, 1, 1, 1);
            st.Field.focused.background = Tex(Hex(0x15161A)); st.Field.focused.textColor = CText; st.Field.hover.background = Tex(Hex(0x131417)); st.Field.hover.textColor = CText;
            st.Primary = Button(CAmber, CAmberHi, CAmberLo, CDark, 16);
            st.Secondary = Button(CSec, CSecHi, CInset, CText, 14);
            st.Close = Button(Color.clear, CSec, CInset, CMuted, 13); st.Close.padding = new RectOffset(8, 8, 0, 0);
            st.Tab = Box(Color.clear, CMuted, 15, FontStyle.Bold, TextAnchor.MiddleCenter, 8, 10); st.Tab.hover.textColor = CText;
            st.TabOn = Box(Color.clear, CText, 15, FontStyle.Bold, TextAnchor.MiddleCenter, 8, 10);
            st.Code = Box(CInset, CAmber, 32, FontStyle.Bold, TextAnchor.MiddleCenter, 12, 14); st.Code.wordWrap = false;
            st.Error = Box(CErrBg, CErr, 14, FontStyle.Normal, TextAnchor.MiddleLeft, 14, 10);
            st.Info = Box(CInfoBg, CText, 14, FontStyle.Normal, TextAnchor.MiddleLeft, 14, 10);
            st.Chip = Box(Hex(0x1E2A1E), COk, 12, FontStyle.Bold, TextAnchor.MiddleLeft, 10, 5); st.Chip.wordWrap = false;
            st.ChipWarn = Box(Hex(0x2D2416), CAmber, 12, FontStyle.Bold, TextAnchor.MiddleLeft, 10, 5); st.ChipWarn.wordWrap = false;
            st.Footer = Box(Color.clear, CMuted, 12, FontStyle.Normal, TextAnchor.MiddleLeft, 0, 0);
            return st;
        }

        // ---------------------------------------------------------------- drawing
        public void OnGUI()
        {
            if (!_open) return;
            if (_panel == MenuPanel.Developer) { DrawDeveloper(); return; }
            if (_styles == null) _styles = BuildStyles();
            var st = _styles;
            float scale = Mathf.Clamp(Screen.height / 1080f, 0.75f, 2.5f);
            float vw = Screen.width / scale, vh = Screen.height / scale;
            var old = GUI.matrix; var oldColor = GUI.color;
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));
            float t = Mathf.Clamp01((Time.realtimeSinceStartup - _openedAt) / 0.14f);
            GUI.color = new Color(1, 1, 1, t);
            GUI.DrawTexture(new Rect(0, 0, vw, vh), st.Backdrop);
            const float W = 600, H = 600;
            var r = new Rect(Mathf.Round((vw - W) / 2f), Mathf.Round((vh - H) / 2f + (1f - t) * 8f), W, H);
            GUI.Box(r, GUIContent.none, st.Panel);
            GUI.DrawTexture(new Rect(r.x, r.y, r.width, 3), st.Accent);
            DrawBorder(r, st.Line);
            GUILayout.BeginArea(new Rect(r.x + 32, r.y + 26, W - 64, H - 46));
            DrawPublic(st);
            GUILayout.EndArea();
            GUI.matrix = old; GUI.color = oldColor;
        }

        static void DrawBorder(Rect r, Texture2D line)
        {
            GUI.DrawTexture(new Rect(r.x, r.yMax - 1, r.width, 1), line);
            GUI.DrawTexture(new Rect(r.x, r.y, 1, r.height), line);
            GUI.DrawTexture(new Rect(r.xMax - 1, r.y, 1, r.height), line);
        }

        void DrawPublic(Styles st)
        {
            GUILayout.BeginHorizontal();
            GUILayout.BeginVertical();
            GUILayout.Label("TS2 MULTIPLAYER", st.Overline);
            GUILayout.Label("Koop-Sitzung", st.Title);
            GUILayout.EndVertical();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Schließen", st.Close, GUILayout.Width(110), GUILayout.Height(34))) { Click(); CloseMenu("button"); }
            GUILayout.EndHorizontal();
            GUILayout.Space(18);

            var ctx = Context();
            if (_legacy) GUILayout.Label(MenuRules.LegacyText, st.Body);
            else if (_hostCode != null && (_lc.IsHostState || _lc.State == MpState.Connected)) DrawHostingPublic(st);
            else if (_lc.Busy() && !_lc.IsHostState) DrawClientPublic(st);
            else
            {
                DrawTabs(st);
                GUILayout.Space(18);
                if (_tab == 0) DrawHostTab(st, ctx); else DrawJoinTab(st, ctx);
            }
            string err = _lc.State == MpState.Error && _lc.Error != null ? _lc.Error : null;
            if (err != null || _hint != null)
            {
                GUILayout.Space(14);
                if (err != null) GUILayout.Label(err, st.Error);
                if (_hint != null && _hint != err) { if (err != null) GUILayout.Space(6); GUILayout.Label(_hint, _hint == "Code kopiert." ? st.Info : st.Error); }
            }
            GUILayout.FlexibleSpace();
            DrawFooter(st);
        }

        void DrawTabs(Styles st)
        {
            GUILayout.BeginHorizontal();
            for (int i = 0; i < 2; i++)
            {
                bool on = _tab == i;
                if (GUILayout.Button(i == 0 ? "Spiel hosten" : "Spiel beitreten", on ? st.TabOn : st.Tab, GUILayout.Height(40)) && !on) { Click(); _tab = i; _hint = null; GUIUtility.keyboardControl = 0; }
                var lr = GUILayoutUtility.GetLastRect();
                if (Event.current.type == EventType.Repaint) GUI.DrawTexture(new Rect(lr.x, lr.yMax - (on ? 2 : 1), lr.width, on ? 2 : 1), on ? st.Accent : st.Line);
            }
            GUILayout.EndHorizontal();
        }

        void DrawHostTab(Styles st, GameContext ctx)
        {
            var a = MenuRouting.PublicHost(ctx, _lc.State, _legacy, _lc.ClientModeSticky, TestActive);
            GUILayout.Label("Du spielst als Spieler 1 mit deinem eigenen Spielstand. Bereite die Sitzung vor und lade danach wie gewohnt – dein Mitspieler kann beitreten, sobald deine Welt geladen ist.", st.Body);
            GUILayout.Space(16);
            GUILayout.Label("UDP-PORT", st.Label);
            GUILayout.BeginHorizontal();
            GUI.enabled = a.Enabled;
            _portText = Digits(GUILayout.TextField(_portText ?? "", 5, st.Field, GUILayout.Width(150), GUILayout.Height(42)));
            GUILayout.Space(8);
            if (GUILayout.Button("Standard", st.Secondary, GUILayout.Height(42))) { Click(); _portText = NetConfig.DefaultPort.ToString(); }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(16);
            if (GUILayout.Button("Sitzung vorbereiten", st.Primary, GUILayout.Height(46))) { Click(); StartHost(false); }
            GUI.enabled = true;
            GUILayout.Space(10);
            if (!a.Enabled) GUILayout.Label(a.Reason, st.Info);
            else GUILayout.Label("Leite UDP " + _portText + " im Router an diesen PC weiter. Der Spielverkehr ist nicht verschlüsselt.", st.Muted);
        }

        void DrawJoinTab(Styles st, GameContext ctx)
        {
            var a = MenuRouting.PublicJoin(ctx, _lc.State, _legacy, _isolationAvailable, TestActive);
            if (!a.Enabled) { GUILayout.Label(a.Reason, st.Info); return; }
            GUILayout.Label("ADRESSE DES HOSTS", st.Label);
            _joinHost = GUILayout.TextField(_joinHost ?? "", 253, st.Field, GUILayout.Height(42));
            GUILayout.Space(12);
            GUILayout.BeginHorizontal();
            GUILayout.BeginVertical(GUILayout.Width(150));
            GUILayout.Label("PORT", st.Label);
            _joinPort = Digits(GUILayout.TextField(_joinPort ?? "", 5, st.Field, GUILayout.Height(42)));
            GUILayout.EndVertical();
            GUILayout.Space(12);
            GUILayout.BeginVertical();
            GUILayout.Label("BEITRITTSCODE", st.Label);
            _joinCode = GUILayout.TextField(_joinCode ?? "", 40, st.Field, GUILayout.Height(42));
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();
            GUILayout.Space(16);
            if (GUILayout.Button("Verbinden", st.Primary, GUILayout.Height(46))) { Click(); StartJoin(JoinKind.Public); }
            GUILayout.Space(10);
            GUILayout.Label("Öffentliche IPv4-Adresse oder DNS-Name des Hosts. Deine Einzelspieler-Spielstände bleiben unverändert – Spieler 2 nutzt einen geschützten Bereich.", st.Muted);
        }

        void DrawHostingPublic(Styles st)
        {
            var node = _runner.Node;
            bool p2 = node != null && node.IsConnected;
            string chip = p2 ? "SPIELER 2 VERBUNDEN" : _hostGateReason == null ? "BEREIT – SPIELER 2 KANN BEITRETEN" : "WARTET: " + _hostGateReason.ToUpperInvariant();
            GUILayout.BeginHorizontal();
            GUILayout.Label(chip, p2 || _hostGateReason == null ? st.Chip : st.ChipWarn);
            if (_runner.TestCampaignActive) { GUILayout.Space(8); GUILayout.Label("ENTWICKLER-TESTKAMPAGNE", st.ChipWarn); }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(16);
            GUILayout.Label("BEITRITTSCODE", st.Label);
            GUILayout.BeginHorizontal();
            GUILayout.Label(_hostCode, st.Code, GUILayout.Height(64));
            GUILayout.Space(10);
            if (GUILayout.Button("Kopieren", st.Secondary, GUILayout.Width(120), GUILayout.Height(64))) { Click(); GUIUtility.systemCopyBuffer = _hostCode; _hint = "Code kopiert."; }
            GUILayout.EndHorizontal();
            GUILayout.Space(12);
            GUILayout.Label("Port: UDP " + _port + "   ·   Spieler 2: " + (p2 ? "verbunden" : "nicht verbunden"), st.Body);
            GUILayout.Space(8);
            GUILayout.Label("Gib deinem Mitspieler deine öffentliche IPv4-Adresse (oder DNS-Namen), den Port und den Code – er gilt nur für diese Sitzung. Schließe dann dieses Fenster und wähle „Fortsetzen“ (ohne Spielstand: ein Spiel beginnen). Zurück im Hauptmenü endet die Sitzung.", st.Muted);
            GUILayout.Space(14);
            if (GUILayout.Button("Hosten abbrechen", st.Secondary, GUILayout.Height(42))) { Click(); CancelHost("cancelled by the player in the main menu"); }
        }

        void DrawClientPublic(Styles st)
        {
            GUILayout.Label("SPIELER 2", st.Label);
            GUILayout.Label(_lc.Status, st.Body);
            if (_runner.TestCampaignActive) { GUILayout.Space(6); GUILayout.Label("ENTWICKLER-TESTKAMPAGNE", st.ChipWarn); }
            GUILayout.Space(16);
            if (GUILayout.Button(_lc.State == MpState.Connected ? "Verbindung trennen" : "Abbrechen", st.Secondary, GUILayout.Height(42))) { Click(); StopSession("disconnected by the player"); }
            if (_lc.ClientModeSticky) { GUILayout.Space(10); GUILayout.Label("Für Einzelspieler oder Hosten muss das Spiel danach neu gestartet werden.", st.Muted); }
        }

        void DrawFooter(Styles st)
        {
            var lr = GUILayoutUtility.GetRect(1, 1, GUILayout.ExpandWidth(true));
            if (Event.current.type == EventType.Repaint) GUI.DrawTexture(new Rect(lr.x, lr.y, lr.width, 1), st.Line);
            GUILayout.Space(10);
            if (_footer == null) _footer = "Version " + ProtocolInfo.ModVersion + "  ·  Protokoll " + ProtocolInfo.ProtocolVersion + "  ·  beide Spieler brauchen dieselbe Version";
            GUILayout.BeginHorizontal();
            GUILayout.Label(_footer, st.Footer);
            GUILayout.FlexibleSpace();
            var s = _lc.State;
            var dot = s == MpState.Error ? st.DotWarn : s == MpState.Offline ? st.Dot : st.DotOk;
            var dr = GUILayoutUtility.GetRect(8, 8, GUILayout.Width(8), GUILayout.Height(16));
            if (Event.current.type == EventType.Repaint) GUI.DrawTexture(new Rect(dr.x, dr.y + 4, 8, 8), dot);
            GUILayout.Space(6);
            GUILayout.Label(_legacy ? LegacyStatus() : s == MpState.Offline ? "Offline" : _lc.Status, st.Footer);
            GUILayout.EndHorizontal();
        }

        string LegacyStatus()
        {
            var n = _runner.Node;
            return n == null ? "kein Netzwerk" : (n.Role == Role.Host ? "Host" : "Spieler 2") + " – " + n.State;
        }

        static string Digits(string s)
        {
            if (s == null) return "";
            for (int i = 0; i < s.Length; i++) if (s[i] < '0' || s[i] > '9') { var sb = new System.Text.StringBuilder(s.Length); foreach (char c in s) if (c >= '0' && c <= '9') sb.Append(c); return sb.ToString(); }
            return s;
        }

        void DisposeUi()
        {
            foreach (var t in _textures) if (t != null) UnityEngine.Object.Destroy(t);
            _textures.Clear(); _styles = null;
        }
    }
}

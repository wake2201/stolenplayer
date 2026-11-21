// W6A.2 offline tests: developer test join without a join code (real LiteNetLib handshakes on 127.0.0.1), the menus' own
// pointer and cursor restore (source scan of the IMGUI glue), and the cleaned-up story-step list. In-game pointer look and
// hit accuracy are manual QA (docs/TESTING.md §51).
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using TS2MP.Shared;

static class W6A2Tests
{
    static int _passed, _failed, _port = 37650;
    static string _logDir;
    const string Game = "1.27c";

    static int Main()
    {
        _logDir = Path.Combine(Path.GetTempPath(), "ts2mp-w6a2-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(_logDir);
        Run("01 developer key: fixed per protocol, domain-separated, never equal to a join-code key; SessionKey routing", Keys);
        Run("02 developer join request: address + port only; the normal join still requires a valid code", Requests);
        Run("03 loopback: developer host + developer client connect without a code; mutual proof", DevConnect);
        Run("04 loopback: developer client at a normal F8 host -> AuthFailed, no retry; F8 client with a code at a developer host -> AuthFailed", NoBypass);
        Run("05 loopback: developer client reconnects after a client restart and after a host restart", Reconnect);
        Run("06 menu glue: developer key only for test host / developer join, normal path unchanged, AuthFailed message, no code field in F10", Glue);
        Run("07 pointer: own IMGUI pointer drawn last at Event.mousePosition, game cursor hidden + restored, lockState / visibility restored, no GUI.Window in F10", Pointer);
        Run("08 story list: one selectable row per step (no checkboxes), cached labels, selection only changes the chosen step", StoryList);
        Console.WriteLine(_failed == 0 ? "W6A2 TESTS PASSED (" + _passed + ")" : "W6A2 TESTS FAILED: " + _failed + " of " + (_passed + _failed));
        return _failed == 0 ? 0 : 1;
    }

    static void Run(string name, Action t)
    {
        try { t(); _passed++; Console.WriteLine("PASS  " + name); }
        catch (Exception ex) { _failed++; Console.WriteLine("FAIL  " + name + " :: " + ex.Message); }
    }
    static void Expect(bool c, string m) { if (!c) throw new Exception(m); }

    // ---------------------------------------------------------------- 01
    static void Keys()
    {
        var k = JoinAuth.DevTestKey(37);
        Expect(k.Length == 32 && Same(k, JoinAuth.DevTestKey(37)) && !Same(k, JoinAuth.DevTestKey(36)), "32 bytes, deterministic, per protocol");
        for (int i = 0; i < 2000; i++) Expect(!Same(k, JoinAuth.DeriveKey(JoinAuth.NewCode())), "never a join-code key");
        Expect(Same(MenuRouting.SessionKey(true, null), JoinAuth.DevTestKey(ProtocolInfo.ProtocolVersion)), "developer session key");
        string code = JoinAuth.NewCode();
        Expect(Same(MenuRouting.SessionKey(false, code), JoinAuth.DeriveKey(code)) && MenuRouting.SessionKey(false, null) == null, "normal session key = code key (unchanged); no code = refused");
        var cn = JoinAuth.NewNonce(); var sn = JoinAuth.NewNonce();
        var proof = JoinAuth.Proof(k, false, 37, Game, "h", "c", cn, sn);
        Expect(!Same(proof, JoinAuth.Proof(JoinAuth.DeriveKey(code), false, 37, Game, "h", "c", cn, sn)), "a developer proof never verifies against a normal host key");
    }

    // ---------------------------------------------------------------- 02
    static void Requests()
    {
        var lc = new Lifecycle(); JoinRequest req;
        Expect(lc.RequestDevJoin(GameContext.MainMenu, true, "192.168.178.20", "27960", out req, 1) == null && req.Code == null && req.Port == 27960 && lc.State == MpState.ClientConnecting, "address + port only");
        lc = new Lifecycle();
        Expect(lc.RequestDevJoin(GameContext.MainMenu, true, "", "27960", out req, 1) != null && lc.RequestDevJoin(GameContext.MainMenu, true, "host:1", "27960", out req, 1) != null
            && lc.RequestDevJoin(GameContext.MainMenu, true, "10.0.0.1", "80", out req, 1) != null && lc.State == MpState.Offline, "bad address / port refused");
        Expect(lc.RequestDevJoin(GameContext.InWorld, true, "10.0.0.1", "27960", out req, 1) != null && lc.RequestDevJoin(GameContext.MainMenu, false, "10.0.0.1", "27960", out req, 1) != null, "main menu + save isolation still required");
        Expect(lc.RequestJoin(GameContext.MainMenu, true, "10.0.0.1", "27960", "", out req, 1) != null && lc.State == MpState.Offline, "normal join without a code still refused");
        string code = JoinAuth.Format(JoinAuth.NewCode());
        Expect(lc.RequestJoin(GameContext.MainMenu, true, "10.0.0.1", "27960", code, out req, 1) == null && req.Code == JoinAuth.Normalize(code), "normal join with a code unchanged");
    }

    // ---------------------------------------------------------------- 03..05 (real UDP on 127.0.0.1)
    static InstanceLog Log(string name, string role, string inst) { return new InstanceLog(Path.Combine(_logDir, name + ".log"), role, inst, "s1"); }
    static SessionNode Host(string tag, int port, byte[] key)
    {
        var n = new SessionNode(Role.Host, "hs" + tag, "host01", Game, port, Log(tag + "-host", "HOST", "host01"));
        n.BindAddress = "127.0.0.1"; n.JoinKey = key; n.AuthTimeoutMs = 1500; n.TestCampaign = Same(key, JoinAuth.DevTestKey(ProtocolInfo.ProtocolVersion));
        return n;
    }
    static SessionNode Client(string tag, int port, string inst, byte[] key)
    {
        var n = new SessionNode(Role.Client, "join", inst, Game, port, Log(tag + "-" + inst, "CLIENT", inst));
        n.ConnectAddress = "127.0.0.1"; n.JoinKey = key; n.ReconnectDelayMs = 200; n.DisconnectTimeoutMs = 2000;
        return n;
    }
    static void Pump(Func<bool> until, int ms, string what, params SessionNode[] nodes)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < end) { foreach (var n in nodes) n.Tick(); if (until()) return; Thread.Sleep(5); }
        throw new Exception("timeout: " + what + " | " + string.Join(", ", Array.ConvertAll(nodes, n => n.Role + "=" + n.State + (n.LastReject.HasValue ? "/" + n.LastReject : ""))));
    }
    static void PumpFor(int ms, params SessionNode[] nodes) { var end = DateTime.UtcNow.AddMilliseconds(ms); while (DateTime.UtcNow < end) { foreach (var n in nodes) n.Tick(); Thread.Sleep(5); } }

    static void DevConnect()
    {
        int port = _port++; var dev = MenuRouting.SessionKey(true, null);
        using (var h = Host("dev", port, dev)) using (var c = Client("dev", port, "c1", dev))
        {
            Expect(h.Start() && c.Start(), "start");
            Pump(() => h.IsConnected && c.IsConnected, 6000, "developer handshake", h, c);
            Expect(c.HostTestCampaign && MenuRouting.JoinMismatch(JoinKind.Developer, c.HostTestCampaign) == null, "Welcome carries the test flag; developer routing accepts it");
            Expect(MenuRouting.JoinMismatch(JoinKind.Public, c.HostTestCampaign) != null, "an F8 join would still be refused by routing");
        }
    }

    static void NoBypass()
    {
        int port = _port++; string code = JoinAuth.NewCode();
        using (var h = Host("f8host", port, JoinAuth.DeriveKey(code))) using (var c = Client("f8host", port, "dev1", MenuRouting.SessionKey(true, null)))
        {
            Expect(h.Start() && c.Start(), "start");
            Pump(() => c.State == ConnState.Failed, 6000, "developer client refused", h, c);
            Expect(c.LastReject == RejectCode.AuthFailed && !h.IsConnected, "AuthFailed at a normal host, got " + c.LastReject);
            int attempts = c.ConnectAttempts; PumpFor(600, h, c);
            Expect(c.ConnectAttempts == attempts && c.State == ConnState.Failed && h.State == ConnState.Listening, "no retry; host keeps listening");
            using (var good = Client("f8host", port, "good1", JoinAuth.DeriveKey(code)))
            {
                good.Start(); Pump(() => h.IsConnected && good.IsConnected, 6000, "the real code still connects", h, good);
            }
        }
        port = _port++;
        using (var h = Host("devhost", port, MenuRouting.SessionKey(true, null))) using (var c = Client("devhost", port, "f8c", JoinAuth.DeriveKey(JoinAuth.NewCode())))
        {
            Expect(h.Start() && c.Start(), "start");
            Pump(() => c.State == ConnState.Failed, 6000, "F8 client refused by a developer host", h, c);
            Expect(c.LastReject == RejectCode.AuthFailed && !h.IsConnected, "AuthFailed, got " + c.LastReject);
        }
    }

    static void Reconnect()
    {
        int port = _port++; var dev = MenuRouting.SessionKey(true, null);
        using (var h = Host("re", port, dev))
        {
            Expect(h.Start(), "host");
            using (var c = Client("re", port, "c1", dev)) { c.Start(); Pump(() => h.IsConnected && c.IsConnected, 6000, "first", h, c); }
            Pump(() => !h.IsConnected, 6000, "host sees the client leave", h);
            using (var c2 = Client("re", port, "c2", dev)) { c2.Start(); Pump(() => h.IsConnected && c2.IsConnected, 6000, "client restart re-authenticates", h, c2); }
        }
        port = _port++;
        using (var h2 = Host("re2", port, dev)) using (var c3 = Client("re2", port, "c3", dev))
        {
            Expect(h2.Start() && c3.Start(), "restarted host");
            Pump(() => h2.IsConnected && c3.IsConnected, 6000, "new developer host session accepts the same developer key", h2, c3);
        }
    }

    // ---------------------------------------------------------------- 06..08 (source)
    static void Glue()
    {
        var code = Strip(Src("Plugin", "MultiplayerMenu.cs"));
        Expect(code.Contains("if (test) node.JoinKey = MenuRouting.SessionKey(true, null);") && code.Contains("else node.JoinKey = JoinAuth.DeriveKey(code);"), "host: developer key only for the test host, code key otherwise");
        Expect(code.Contains("_hostCode = test ? DevHostLabel : JoinAuth.Format(code); _devHosting = test;"), "no code shown for a developer host");
        Expect(code.Contains("kind == JoinKind.Developer ? _lc.RequestDevJoin(") && code.Contains("node.JoinKey = kind == JoinKind.Developer ? MenuRouting.SessionKey(true, null) : JoinAuth.DeriveKey(req.Code);"), "join: developer key only for JoinKind.Developer");
        Expect(code.Contains("if (kind == JoinKind.Developer && !_devAllowed)") && code.Contains("if (test && !_devAllowed)"), "developer key unreachable without the developer menu");
        Expect(code.Contains("if (_joinKind == JoinKind.Developer && node.LastReject == RejectCode.AuthFailed) { _hint = MenuRouting.DevAuthFailedText;"), "clear message when a developer join reaches a normal host");
        Expect(code.Contains("string kindErr = MenuRouting.JoinMismatch(_joinKind, test);"), "routing check after Welcome kept");
        var dev = Strip(Src("Plugin", "MultiplayerMenu.Dev.cs")); var ui = Strip(Src("Plugin", "MultiplayerMenu.Ui.cs"));
        Expect(dev.IndexOf("_joinCode", StringComparison.Ordinal) < 0 && dev.IndexOf("\"Code:\"", StringComparison.Ordinal) < 0, "F10 has no code field");
        Expect(ui.Contains("_joinCode = GUILayout.TextField(_joinCode ?? \"\", 40, st.Field") && ui.Contains("StartJoin(JoinKind.Public);"), "F8 join keeps its code field");
        Expect(ui.IndexOf("SessionKey(true", StringComparison.Ordinal) < 0 && ui.IndexOf("DevTestKey", StringComparison.Ordinal) < 0, "F8 never uses the developer key");
    }

    static void Pointer()
    {
        var ui = Strip(Src("Plugin", "MultiplayerMenu.Ui.cs")); var dev = Strip(Src("Plugin", "MultiplayerMenu.Dev.cs"));
        int og = ui.IndexOf("public void OnGUI()", StringComparison.Ordinal);
        string body = ui.Substring(og, ui.IndexOf("static void DrawBorder", og, StringComparison.Ordinal) - og);
        Expect(body.Contains("{ DrawDeveloper(); DrawPointer(); return; }"), "F10: pointer after the developer panel");
        int mr = body.IndexOf("GUI.matrix = old; GUI.color = oldColor;", StringComparison.Ordinal), dp = body.LastIndexOf("DrawPointer();", StringComparison.Ordinal);
        Expect(mr > 0 && dp > mr, "F8: pointer drawn last, after the scale matrix is restored (screen pixels)");
        Expect(ui.Contains("var m = Event.current.mousePosition;") && ui.Contains("if (Event.current.type != EventType.Repaint) return;"), "pointer at the IMGUI hit-test position, repaint only");
        Expect(dev.IndexOf("GUI.Window(", StringComparison.Ordinal) < 0 && dev.IndexOf("GUI.DragWindow", StringComparison.Ordinal) < 0, "no GUI.Window (it would draw above the pointer)");
        Expect(ui.IndexOf("GUI.Window(", StringComparison.Ordinal) < 0, "F8 no GUI.Window either");
        Expect(ui.Contains("_uiCursorField = Fast.Getter<object>(_uiModuleT, \"m_cursor\")") && ui.Contains("if (_gameCursorWasActive) _gameCursor.SetActive(false);")
            && ui.Contains("if (_gameCursor != null && Alive(_gameCursor) && _gameCursorWasActive) _gameCursor.SetActive(true);"), "game software cursor hidden while open, re-shown on close (never reused)");
        Expect(ui.Contains("_prevVisible = Cursor.visible; _prevLock = Cursor.lockState;") && ui.Contains("Cursor.lockState = _prevLock; Cursor.visible = _prevVisible;"), "lockState + visibility restored");
        Expect(ui.Contains("if (Cursor.lockState != CursorLockMode.None) Cursor.lockState = CursorLockMode.None;") && ui.Contains("if (Cursor.visible) Cursor.visible = false;"), "free pointer, no second hardware pointer");
        int cm = ui.IndexOf("void CloseMenu(string why)", StringComparison.Ordinal);
        Expect(cm > 0 && ui.IndexOf("SyncCursor();", cm, StringComparison.Ordinal) > cm && ui.IndexOf("SyncCursor();", cm, StringComparison.Ordinal) < ui.IndexOf("void SyncInputBlock()", cm, StringComparison.Ordinal), "close restores the cursor");
        Expect(ui.Contains("_uiSetPos.Invoke(_cursorModule, new object[] { (Vector2)Input.mousePosition });"), "game cursor continues at the pointer position");
        Expect(ui.Contains("filterMode = FilterMode.Point") && ui.Contains("if (_pointer != null) return _pointer;"), "pointer texture built once");
        foreach (var bad in new[] { "Cursor.SetCursor", "Resources.Load", "FindObjectsOfType" }) Expect(ui.IndexOf(bad, StringComparison.Ordinal) < 0, "no " + bad);
    }

    static void StoryList()
    {
        var dev = Strip(Src("Plugin", "MultiplayerMenu.Dev.cs"));
        Expect(dev.IndexOf("GUILayout.Toggle(", StringComparison.Ordinal) < 0 && dev.IndexOf("GUI.Toggle(", StringComparison.Ordinal) < 0, "no checkboxes in the developer menu");
        Expect(dev.Contains("if (GUILayout.Button(_stepLabels[i], _devStory == i ? SelectedRow() : RowStyle())) _devStory = i;"), "one selectable row per step; selecting only sets the chosen step");
        Expect(dev.Contains("if (_stepLabels == null)"), "row labels built once");
        int a = dev.IndexOf("void DevStorySelector()", StringComparison.Ordinal);
        string sel = dev.Substring(a, dev.IndexOf("void DevJoinSection(", a, StringComparison.Ordinal) - a);
        foreach (var bad in new[] { "_spSet(", "SaveGuard.", "Learn_Skill", "StartHost(", "moneyAm" }) Expect(sel.IndexOf(bad, StringComparison.Ordinal) < 0, "the selector itself writes nothing: " + bad);
        Expect(StoryCatalog.Steps.Length == 81 && ProtocolInfo.ProtocolVersion == 41 && ProtocolInfo.ModVersion == "0.31.0", "81 steps; protocol 37 / mod 0.26.2");
    }

    // ---------------------------------------------------------------- helpers
    static bool Same(byte[] a, byte[] b) { if (a == null || b == null || a.Length != b.Length) return false; for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false; return true; }
    static string Root()
    {
        string root = AppDomain.CurrentDomain.BaseDirectory;
        for (int i = 0; i < 6 && root != null; i++) { if (Directory.Exists(Path.Combine(root, "src", "Plugin"))) return Path.Combine(root, "src"); root = Path.GetDirectoryName(root.TrimEnd(Path.DirectorySeparatorChar)); }
        throw new Exception("src not found");
    }
    static string Src(string dir, string f) { return File.ReadAllText(Path.Combine(Root(), dir, f)).Replace("\r\n", "\n"); }
    static string Strip(string s)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var line in s.Split('\n')) { int i = line.IndexOf("//", StringComparison.Ordinal); sb.Append(i >= 0 ? line.Substring(0, i) : line).Append('\n'); }
        return sb.ToString();
    }
}

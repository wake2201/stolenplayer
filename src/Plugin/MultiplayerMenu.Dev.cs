// W6A.1: developer menu (F10, only with Developer.EnableDeveloperMenu = true; plain IMGUI, function first). Everything the
// former F8 "multiplayer test campaign" offered, plus a story-step selector and a scenario reset:
//   - Dev Host: prepares the isolated test campaign (W4.2B.2: SaveGuard.PrepareTestHostSave = redirect + guards onto
//     <data>\testcampaign\host-save, a COPY of the real save folder made once), optionally sets the selected story step in that
//     copy, then prepares the host session exactly like F8 (same StartHost path, Welcome carries the test flag).
//   - Dev Join: the normal join path with JoinKind.Developer (MenuRouting.JoinMismatch: only a test-campaign host is accepted;
//     the client then uses the test sandbox, W4.2B.2).
//   - Story step (0..80, StoryCatalog): written as the copy's "Story" / "StoryBackup" keys only while the copy is armed and
//     verified (SaveGuard.Armed + DevHost + RuntimeCheck) and only for steps StoryCatalog does not refuse. The original
//     MainMenu.Continue then loads the copy: StoryManager.BeginStory reads the key and runs InitiateStory for that step - the
//     same path as loading any save at that step (its start effects once, earlier steps never replayed). Player 2 receives
//     the host's campaign (CampaignBootstrap) and the live index (W6A "story" state).
//   - Reset: the whole <data>\testcampaign folder is moved aside to testcampaign.reset-<time> (never deleted) while nothing
//     uses it; the next Dev Host copies the real save again = clean, repeatable starting point.
using System;
using System.IO;
using HarmonyLib;
using TS2MP.Shared;
using UnityEngine;

namespace TS2MP.Plugin
{
    internal sealed partial class MultiplayerMenu
    {
        int _devStory = 18, _appliedStory = -1;   // W6A.3: default index 18 = story14 (W6A note_103 test)
        Rect _devRect; Vector2 _devScroll, _stepScroll; string[] _stepLabels; string _devInfo; float _devInfoAt;
        static readonly string[] SupportMark = { "OK ", "~  ", "X  " };

        // ---------------------------------------------------------------- W6A.3 story-test scenario (src/Shared/DevScenario.cs)
        string _devError;           // visible in F10 until the next start attempt
        int _verifyStory = -1; bool _readyLogged; float _verifyAt;

        void DevError(string why)
        {
            _devError = "TESTKAMPAGNE NICHT GESTARTET – " + why + " Es ist keine Testkampagne aktiv: im Hauptmenü nichts laden, Ursache beheben und erneut „Testkampagne starten“.";
            _log.Error("TEST_CAMPAIGN start failed: " + why + " (no test campaign is active; the sandbox was released)");
        }

        // Plan, move the previous story folder aside, arm + verify the isolated sandbox, check the seed source. false = nothing
        // is active (sandbox released), _devError says why.
        bool DevPrepare(out DevPlan plan)
        {
            _devError = null; _verifyStory = -1; _readyLogged = false;
            string why, storyDir = DevScenario.StoryDataDir(_settings.DataDir);
            why = DevScenario.Plan(_devStory, _settings.DataDir, storyDir != null && Directory.Exists(storyDir), SaveGuard.RealDir, SaveGuard.GameDir, DateTime.Now.ToString("yyyyMMdd-HHmmss"), out plan);
            if (why != null) { DevError(why); return false; }
            if (!ResolvePrefs()) { DevError("Speicher-API des Spiels nicht gefunden (SPrefs / FBPP)."); plan = null; return false; }
            if (!_lc.TestPrepared)
            {
                if (plan.MoveAsideTo != null)
                {
                    if (SaveGuard.Armed) { DevError("Der Testordner ist in diesem Spiel schon in Benutzung – Spiel neu starten."); plan = null; return false; }
                    try { Directory.Move(plan.DataDir, plan.MoveAsideTo); _log.Info("TEST_CAMPAIGN previous story-test folder moved aside (not deleted): " + plan.DataDir + " -> " + plan.MoveAsideTo); }
                    catch (Exception ex) { DevError("Alter Story-Testordner konnte nicht beiseitegelegt werden: " + ex.Message); plan = null; return false; }
                }
                if (!PrepareTestCampaign(plan)) { DevError(_hint ?? "Isolierter Speicherbereich konnte nicht vorbereitet werden (siehe Log)."); plan = null; return false; }
            }
            if (!SaveGuard.Armed || !SaveGuard.DevHost || !SaveGuard.RuntimeCheck())
            { DevError("Speicherschutz des Test-Spielstands nicht bestätigt (siehe Log)."); ReleaseTestIfDue("sandbox not verified"); plan = null; return false; }
            if (plan.Mode == DevMode.StorySeed)
            {
                if (!ResolveSeed()) { DevError("MainMenu.StartNewGame / skipIntro des Spiels nicht gefunden."); ReleaseTestIfDue("seed API missing"); plan = null; return false; }
            }
            else
            {
                bool hasSave = false;
                try { hasSave = _fbppExists != null && (bool)_fbppExists.Invoke(null, null); } catch { }
                string r = MenuRouting.DevCopyRefusal(hasSave);
                if (r != null) { DevError(r); ReleaseTestIfDue("copy has no save"); plan = null; return false; }
            }
            _log.Info("TEST_CAMPAIGN prepared: " + (plan.Mode == DevMode.StorySeed ? "story seed " + DevScenario.Label(plan.Story) + " in " + SaveGuard.SandboxDir + " (fresh, never seeded from the real save)" : "copy of the real save in " + SaveGuard.SandboxDir) + "; host session next");
            return true;
        }

        bool PrepareTestCampaign(DevPlan plan)
        {
            var a = MenuRules.PrepareTest(Context(), _lc.State, _legacy, _lc.ClientModeSticky, _isolationAvailable, _lc.TestPrepared, _worldLoadedOnce);
            if (!a.Enabled) { _hint = a.Reason; return false; }
            string dir = plan.DataDir, why;
            try { Directory.CreateDirectory(dir); } catch (Exception ex) { _hint = "Testordner konnte nicht angelegt werden: " + ex.Message; return false; }
            bool wasArmed = SaveGuard.Armed;
            if (!SaveGuard.PrepareTestHostSave(dir, plan.SeedFromReal, out why))
            {
                _log.Error("TEST_CAMPAIGN save isolation could not be verified: " + why);
                _hint = "Testkampagne konnte nicht sicher vorbereitet werden (siehe Log).";
                if (SaveGuard.Armed || wasArmed) _runner.RequestQuit("SAVE_GUARD_FAILED after arming the test campaign: quitting to protect the real saves (fail closed).");
                return false;
            }
            _runner.EnableTestCampaign(dir, "developer menu host");
            _lc.TestCampaignPrepared(); _hint = null;
            RefreshContinueText();
            return true;
        }

        static Action<object, bool> _setSkipIntro; static Func<object, bool> _skipIntro; static System.Reflection.MethodInfo _startNewGame, _continue; static bool _seedResolved;
        bool ResolveSeed()
        {
            if (!_seedResolved)
            {
                _seedResolved = true;
                _skipIntro = Fast.Getter<bool>(_menuType, "skipIntro"); _setSkipIntro = Fast.Setter<bool>(_menuType, "skipIntro");
                _startNewGame = _menuType != null ? AccessTools.Method(_menuType, "StartNewGame", Type.EmptyTypes) : null;
            }
            return _skipIntro != null && _setSkipIntro != null && _startNewGame != null;
        }

        // The host session listens; now the original load runs by itself (no click in the original menu).
        void DevAutoLoad(DevPlan plan)
        {
            if (plan == null) return;
            try
            {
                var mm = _menuType != null ? UnityEngine.Object.FindObjectOfType(_menuType) : null;   // once
                if (mm == null) throw new InvalidOperationException("Hauptmenü des Spiels nicht gefunden");
                if (!SaveGuard.Armed || !SaveGuard.DevHost || !SaveGuard.RuntimeCheck()) throw new InvalidOperationException("Speicherschutz nicht bestätigt");
                CloseMenu("test campaign start");
                if (plan.Mode == DevMode.SaveCopy)
                {
                    if (_continue == null) _continue = AccessTools.Method(_menuType, "Continue");
                    _log.Info("TEST_CAMPAIGN loading the copy through the original MainMenu.Continue()");
                    _continue.Invoke(mm, null);
                    return;
                }
                // Original new game inside the verified sandbox (MainMenu.StartNewGame, skip-intro branch IL_001a..IL_002c).
                bool prev = _skipIntro(mm);
                _setSkipIntro(mm, true);
                try { _startNewGame.Invoke(mm, null); } finally { _setSkipIntro(mm, prev); }
                // Before the scene's StoryManager.BeginStory reads it (the scene loads asynchronously).
                _spSet("Story", plan.Story); _spSet("StoryBackup", plan.Story); _fbppSave();
                _appliedStory = plan.Story; _verifyStory = plan.Story; _verifyAt = Time.realtimeSinceStartup;
                string bad = DevScenario.VerifySeedPrefs(plan.Story, _spGet("Story"), _spGet("StoryBackup"), _spGet("FreeMode"), _spGet("CurrentLocation"));
                if (bad != null) { _devError = "Story-Testspielstand ungültig: " + bad; _log.Error("TEST_STORY seed invalid: " + bad); }
                else _log.Info("TEST_STORY seed: original MainMenu.StartNewGame (skip intro) in the sandbox, then Story = StoryBackup = " + plan.Story + " (" + DevScenario.Label(plan.Story) + "), FreeMode 0, CurrentLocation " + DevScenario.SeedLocation + "; loading MainScene");
            }
            catch (Exception ex)
            {
                string why = ex is System.Reflection.TargetInvocationException && ex.InnerException != null ? ex.InnerException.Message : ex.Message;
                _log.Error("TEST_CAMPAIGN automatic load failed: " + why);
                if (!_worldLoadedOnce && !_worldSceneLoaded) { CancelHost("automatic test load failed"); DevError("Automatisches Laden fehlgeschlagen: " + why + "."); }
                else _devError = "Automatisches Laden fehlgeschlagen: " + why + " (siehe Log).";
            }
        }

        // Host in the loaded world: verify the live story index once, then report "host ready" once.
        static Func<object, object> _gmStoryMgr, _smCurrent; static Func<object, int> _smStory; static Func<object, bool> _smFree; static Func<object, string> _scriptKey; static bool _liveResolved;
        void DevHostTick(GameContext ctx)
        {
            if (ctx != GameContext.InWorld) return;
            if (_verifyStory >= 0 && Time.realtimeSinceStartup - _verifyAt > 1f && _gm != null)
            {
                if (!_liveResolved)
                {
                    _liveResolved = true;
                    var sm = AccessTools.TypeByName("StoryManager"); var ss = AccessTools.TypeByName("StoryScript");
                    _gmStoryMgr = Fast.Getter<object>(_gmType, "story_manager"); _smCurrent = Fast.Getter<object>(sm, "currentStoryScript");
                    _smStory = Fast.Getter<int>(sm, "story"); _smFree = Fast.Getter<bool>(sm, "isFreeMode"); _scriptKey = Fast.Getter<string>(ss, "story_description");
                }
                var mgr = _gmStoryMgr != null ? _gmStoryMgr(_gm) : null; var cur = mgr != null && _smCurrent != null ? _smCurrent(mgr) : null;
                if (cur != null && _smStory != null && _smFree != null && _scriptKey != null)
                {
                    int expect = _verifyStory; _verifyStory = -1;
                    string bad = DevScenario.VerifyLoaded(expect, _smStory(mgr), _scriptKey(cur), _smFree(mgr));
                    if (bad != null) { _devError = "Story-Test ungültig: " + bad; _devCheck = "FEHLER – " + bad; _log.Error("TEST_STORY world check FAILED: " + bad); }
                    else
                    {
                        string note = "";
                        if (StoryCatalog.Get(expect).Key == "story14")
                        {
                            var go = GameObject.Find("story object 103 notka");   // once, after the load: active only while story14 is current
                            note = go != null && go.activeInHierarchy ? "; note_103 (House 103) present" : "; note_103 parent not active yet (Madison Street not loaded or StoryObject pending)";
                        }
                        _devCheck = DevScenario.Label(expect) + " – geprüft OK" + note;
                        _log.Info("TEST_STORY world check OK: live " + DevScenario.Label(expect) + ", free mode off" + note);
                    }
                }
            }
            if (!_readyLogged && _hostGateReason == null)
            {
                _readyLogged = true;
                _log.Info("TEST_CAMPAIGN HOST READY: Player 2 can join now with F10 → Testkampagne beitreten (address of this PC, UDP " + _port + ", no code)");
            }
        }

        // The main menu's save info was read from the real folder; the original LoadContinueTxt re-reads it (now redirected).
        void RefreshContinueText()
        {
            try
            {
                var mm = _menuType != null ? UnityEngine.Object.FindObjectOfType(_menuType) : null;   // once
                var m = _menuType != null ? AccessTools.Method(_menuType, "LoadContinueTxt") : null;
                if (mm != null && m != null && m.GetParameters().Length == 0) m.Invoke(mm, null);
            }
            catch (Exception ex) { _log.Warn("TEST_CAMPAIGN main-menu save info not refreshed: " + ex.Message); }
        }

        // ---------------------------------------------------------------- story step in the isolated copy
        static Func<string, int> _spGet; static Action<string, int> _spSet; static Action _fbppSave; static bool _spResolved;
        static bool ResolvePrefs()
        {
            if (_spResolved) return _spGet != null && _spSet != null && _fbppSave != null;
            _spResolved = true;
            var sp = AccessTools.TypeByName("SPrefs"); var fbpp = AccessTools.TypeByName("FBPP");
            var g = sp != null ? AccessTools.Method(sp, "GetInt", new[] { typeof(string) }) : null;
            var s = sp != null ? AccessTools.Method(sp, "SetInt", new[] { typeof(string), typeof(int) }) : null;
            var sv = fbpp != null ? AccessTools.Method(fbpp, "Save", Type.EmptyTypes) : null;
            if (g != null) _spGet = (Func<string, int>)Delegate.CreateDelegate(typeof(Func<string, int>), g);
            if (s != null) _spSet = (Action<string, int>)Delegate.CreateDelegate(typeof(Action<string, int>), s);
            if (sv != null) _fbppSave = (Action)Delegate.CreateDelegate(typeof(Action), sv);
            return _spGet != null && _spSet != null && _fbppSave != null;
        }

        // ---------------------------------------------------------------- reset
        void ResetScenario()
        {
            var a = MenuRouting.DevReset(Context(), _lc.State, _legacy, SaveGuard.Armed, _worldLoadedOnce);
            if (!a.Enabled) { _hint = a.Reason; return; }
            string dir = SaveIsolation.TestCampaignDataDir(_settings.DataDir), why;
            if (!Directory.Exists(dir)) { _hint = "Kein Testszenario vorhanden – der nächste Test-Host kopiert deinen Spielstand frisch."; return; }
            string dst = SaveIsolation.ResetDestination(dir, SaveGuard.RealDir, SaveGuard.GameDir, DateTime.Now.ToString("yyyyMMdd-HHmmss"), out why);
            if (dst == null) { _hint = "Zurücksetzen verweigert: " + why; _log.Error("TEST_RESET refused: " + why); return; }
            try
            {
                Directory.Move(dir, dst);
                _hint = "Testszenario zurückgesetzt. Der nächste Test-Host kopiert deinen echten Spielstand neu (alter Stand: " + Path.GetFileName(dst) + ").";
                _log.Info("TEST_RESET test campaign folder moved aside (not deleted): " + dir + " -> " + dst + "; the next developer host seeds a fresh copy of the real save");
            }
            catch (Exception ex) { _hint = "Zurücksetzen fehlgeschlagen: " + ex.Message; _log.Error("TEST_RESET move failed", ex); }
        }

        // ---------------------------------------------------------------- window (function first)
        // W6A.2: a plain area instead of GUI.Window - IMGUI draws windows above every later control, which would hide the
        // mod's pointer (DrawPointer is the last call of the frame). Fixed, centred, clamped to the screen.
        void DrawDeveloper()
        {
            float w = Mathf.Min(720, Screen.width - 20), h = Mathf.Min(780, Screen.height - 20);
            _devRect = new Rect(Mathf.Round((Screen.width - w) / 2f), Mathf.Round((Screen.height - h) / 2f), w, h);
            GUI.Box(_devRect, "TS2MP Entwicklermenü (" + _settings.DevMenuKey + ")");
            GUI.Box(_devRect, GUIContent.none);   // second pass: opaque enough over the main menu
            GUILayout.BeginArea(new Rect(_devRect.x + 10, _devRect.y + 24, _devRect.width - 20, _devRect.height - 34));
            DevWindow();
            GUILayout.EndArea();
        }

        void DevWindow()
        {
            var ctx = Context();
            _devScroll = GUILayout.BeginScrollView(_devScroll);
            if (_devError != null) { GUILayout.Label("!! " + _devError); GUILayout.Space(4); }
            GUILayout.Label(DevInfo());
            if (_lc.State == MpState.Error && _lc.Error != null) GUILayout.Label("Fehler: " + _lc.Error);
            if (_hint != null) GUILayout.Label(">> " + _hint);
            GUILayout.Space(6);

            if (_legacy) GUILayout.Label(MenuRules.LegacyText);
            else if (_hostCode != null && (_lc.IsHostState || _lc.State == MpState.Connected))
            {
                GUILayout.Label(_hostGateReason == null ? ">>> HOST BEREIT – Spieler 2 kann jetzt über F10 beitreten (UDP " + _port + ", kein Code) <<<" : "HOST LÄDT – Spieler 2 noch nicht beitreten: " + _hostGateReason);
                GUILayout.Label("UDP " + _port + (_runner.TestCampaignActive ? " – TESTKAMPAGNE" : " – normale Kampagne") + (_appliedStory >= 0 ? " – " + DevScenario.Label(_appliedStory) : ""));
                if (GUILayout.Button("Hosten abbrechen")) CancelHost("cancelled in the developer menu");
            }
            else if (_lc.Busy() && !_lc.IsHostState)
            {
                GUILayout.Label("CLIENT: " + _lc.Status);
                if (GUILayout.Button(_lc.State == MpState.Connected ? "Verbindung trennen" : "Abbrechen")) StopSession("disconnected in the developer menu");
            }
            else
            {
                DevHostSection(ctx);
                GUILayout.Space(10);
                DevJoinSection(ctx);
                GUILayout.Space(10);
                var ra = MenuRouting.DevReset(ctx, _lc.State, _legacy, SaveGuard.Armed, _worldLoadedOnce);
                GUILayout.Label("— Testszenario zurücksetzen —");
                GUILayout.Label("Verschiebt <TS2MP>\\testcampaign (Story-Test-Spielstände, Kopie, Test-Datensätze, Test-Sandbox) nach testcampaign.reset-<Zeit>. Nichts wird gelöscht; echte Spielstände bleiben unberührt. (Ein Story-Start legt seinen Test-Spielstand ohnehin jedes Mal frisch an.)");
                GUI.enabled = ra.Enabled;
                if (GUILayout.Button("Testszenario zurücksetzen")) ResetScenario();
                GUI.enabled = true;
                if (!ra.Enabled) GUILayout.Label(ra.Reason);
            }
            GUILayout.EndScrollView();
            if (GUILayout.Button("Schließen (" + _settings.DevMenuKey + ")")) CloseMenu("button");
        }

        void DevHostSection(GameContext ctx)
        {
            GUILayout.Label("— Testkampagne starten (Host) —");
            var a = MenuRules.PrepareTest(ctx, _lc.State, _legacy, _lc.ClientModeSticky, _isolationAvailable, _lc.TestPrepared, _worldLoadedOnce);
            GUILayout.BeginHorizontal();
            GUILayout.Label("UDP-Port:", GUILayout.Width(80));
            _portText = Digits(GUILayout.TextField(_portText ?? "", 5, GUILayout.Width(80)));
            GUILayout.EndHorizontal();
            DevStorySelector();
            GUI.enabled = a.Enabled || _lc.TestPrepared;
            if (GUILayout.Button(_devStory < 0 ? "Testkampagne starten (Kopie meines Spielstands)" : "Testkampagne starten – " + DevScenario.Label(_devStory), GUILayout.Height(34))) StartHost(true);
            GUI.enabled = true;
            if (!a.Enabled && !_lc.TestPrepared) GUILayout.Label(a.Reason);
            GUILayout.Label(_devStory < 0
                ? "Lädt eine isolierte KOPIE deines Spielstands automatisch (mit seinem eigenen Story-Stand, auch Freier Modus)."
                : "Startet automatisch: eigener, frischer Test-Spielstand (originales Neues Spiel ohne Intro, NICHT dein echter Spielstand), Story-Index " + _devStory + ", alle Fähigkeiten, Testgeld. Danach lädt die Welt von selbst – im Original-Menü nichts anklicken.");
            GUILayout.Label("Spieler 2: erst beitreten, wenn F10 / Log „HOST BEREIT“ meldet (der Beitritt wartet sonst automatisch).");
        }

        void DevStorySelector()
        {
            if (_stepLabels == null)
            {
                _stepLabels = new string[StoryCatalog.Count];
                foreach (var s in StoryCatalog.Steps) _stepLabels[s.Index] = SupportMark[(int)s.Support] + DevScenario.Label(s.Index);   // W6A.3: index and original objective together
            }
            GUILayout.BeginHorizontal();
            GUILayout.Label("Story-Index:", GUILayout.Width(90));
            if (GUILayout.Button("Kopie meines Spielstands", GUILayout.Width(170))) _devStory = -1;
            if (GUILayout.Button("−10", GUILayout.Width(40))) _devStory = Mathf.Clamp(_devStory - 10, 0, StoryCatalog.Count - 1);
            if (GUILayout.Button("−1", GUILayout.Width(34))) _devStory = Mathf.Clamp(_devStory - 1, 0, StoryCatalog.Count - 1);
            if (GUILayout.Button("+1", GUILayout.Width(34))) _devStory = Mathf.Clamp(_devStory + 1, 0, StoryCatalog.Count - 1);
            if (GUILayout.Button("+10", GUILayout.Width(40))) _devStory = Mathf.Clamp(_devStory + 10, 0, StoryCatalog.Count - 1);
            GUILayout.EndHorizontal();
            var sel = StoryCatalog.Get(_devStory);
            if (sel == null) GUILayout.Label("Gewählt: Kopie deines Spielstands (kein Story-Index, eigener Story-Stand der Kopie).");
            else
            {
                GUILayout.Label("Gewählt: " + _stepLabels[sel.Index]);
                GUILayout.Label(sel.Support == StepSupport.Supported ? "Unterstützt." : (sel.Support == StepSupport.Limited ? "Eingeschränkt: " : "NICHT unterstützt: ") + sel.Reason);
                GUILayout.Label("Ausgangslage = originales Neues Spiel auf diesem Index: frühere Schritte werden nicht nachgespielt (keine Gegenstände, Aufgaben, Schuldenabzüge oder Zähler früherer Schritte).");
            }
            GUILayout.Label("Legende: OK unterstützt · ~ eingeschränkt · X nicht unterstützt");
            _stepScroll = GUILayout.BeginScrollView(_stepScroll, GUILayout.Height(190));
            for (int i = 0; i < _stepLabels.Length; i++)
                if (GUILayout.Button(_stepLabels[i], _devStory == i ? SelectedRow() : RowStyle())) _devStory = i;   // W6A.2: one selectable row per step (no checkboxes)
            GUILayout.EndScrollView();
        }

        void DevJoinSection(GameContext ctx)
        {
            GUILayout.Label("— Testkampagne beitreten (zweiter PC) —");
            var a = MenuRules.Join(ctx, _lc.State, _legacy, _isolationAvailable, _lc.TestPrepared);
            if (!a.Enabled) { GUILayout.Label(a.Reason); return; }
            GUILayout.BeginHorizontal();
            GUILayout.Label("Host:", GUILayout.Width(40)); _joinHost = GUILayout.TextField(_joinHost ?? "", 253, GUILayout.Width(220));
            GUILayout.Label("Port:", GUILayout.Width(36)); _joinPort = Digits(GUILayout.TextField(_joinPort ?? "", 5, GUILayout.Width(60)));
            GUILayout.EndHorizontal();
            if (GUILayout.Button("Testkampagne beitreten")) StartJoin(JoinKind.Developer);
            GUILayout.Label("Kein Code nötig: Test-Host und Test-Beitritt verwenden einen festen Entwickler-Schlüssel. Normale F8-Hosts lehnen ihn ab.");
        }

        GUIStyle _row, _rowSel;
        GUIStyle RowStyle() { if (_row == null) { _row = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleLeft, wordWrap = false }; _row.hover.textColor = Color.white; _row.normal.textColor = new Color(0.82f, 0.82f, 0.82f); } return _row; }
        GUIStyle SelectedRow() { if (_rowSel == null) { _rowSel = new GUIStyle(GUI.skin.button) { alignment = TextAnchor.MiddleLeft, wordWrap = false, fontStyle = FontStyle.Bold }; } return _rowSel; }

        // W6A.3: read-only developer status in the world (F10 with the developer menu enabled): host readiness, story check.
        bool _devStatus; string _devCheck, _statusText; float _statusAt;
        void DrawDevStatus()
        {
            if (Context() != GameContext.InWorld) { _devStatus = false; return; }
            float now = Time.realtimeSinceStartup;
            if (_statusText == null || now >= _statusAt)
            {
                _statusAt = now + 0.5f;
                var n = _runner.Node;
                string host = !_lc.IsHostState && !_devHosting ? (n != null ? "Spieler 2: " + _lc.Status : "keine Sitzung")
                    : _hostGateReason == null ? "HOST BEREIT – Spieler 2 kann über F10 beitreten (UDP " + _port + ", kein Code)" : "HOST LÄDT – noch nicht beitreten: " + _hostGateReason;
                _statusText = "TS2MP Entwickler-Status (" + _settings.DevMenuKey + ")\n" + host
                    + "\nStory: " + (_devCheck ?? (_appliedStory >= 0 ? DevScenario.Label(_appliedStory) + " (Prüfung läuft)" : "–"))
                    + "\nSpieler 2: " + (n != null && n.IsConnected ? "verbunden" : "nicht verbunden") + (_devError != null ? "\n!! " + _devError : "");
            }
            GUI.Box(new Rect(10, 10, Mathf.Min(760, Screen.width - 20), _devError != null ? 120 : 80), _statusText);
        }

        // Technical status, rebuilt at most twice per second (no per-frame string building).
        string DevInfo()
        {
            float now = Time.realtimeSinceStartup;
            if (_devInfo != null && now < _devInfoAt) return _devInfo;
            _devInfoAt = now + 0.5f;
            var n = _runner.Node;
            _devInfo = "Mod " + ProtocolInfo.ModVersion + " / Protokoll " + ProtocolInfo.ProtocolVersion + "   Zustand: " + _lc.State + (n != null ? " (" + n.Role + " " + n.State + ")" : "")
                + "\nSpeicherschutz: " + (SaveGuard.Armed ? "AKTIV" + (SaveGuard.DevHost ? " (Test-Host-Kopie)" : SaveGuard.TestSandbox ? " (Test-Sandbox)" : " (Spieler-2-Sandbox)") : "aus") + (SaveGuard.Armed ? "  " + SaveGuard.SandboxDir : "")
                + "\nTestkampagne: " + (_runner.TestCampaignActive ? "aktiv" : "aus") + (_lc.TestPrepared ? ", vorbereitet" : "") + "   Welt in diesem Prozess geladen: " + (_worldLoadedOnce ? "ja" : "nein")
                + "\nStory gesetzt: " + (_appliedStory >= 0 ? DevScenario.Label(_appliedStory) : "–") + "   Datenordner: " + _settings.DataDir;
            return _devInfo;
        }
    }
}

// F8 multiplayer menu (Unity IMGUI, drawn only while the player has it open) and the session glue that feeds real game
// state into the Unity-free Lifecycle (src/Shared/MultiplayerLifecycle.cs) and executes its actions.
// W4.2B.3: the window exists ONLY in the original main menu (MainMenuNew). In a loaded world F8 does nothing: no window,
// no player motor / camera / cursor / Rewired change (the former in-world input lock is gone).
//  Host : prepared in the main menu (normal or isolated test campaign). Starts the SessionNode (UDP 0.0.0.0:<port>, new
//         join code) and the sync systems right away - the developer launcher host's proven order: the systems see the
//         save load from the start, no late attach into a running world. The player then loads through the original
//         Continue / New Game. Joins are refused (HostNotReady, retryable) until HostReadiness reports the loaded world
//         ready. Back in the main menu after the world was loaded = the session ends.
//  Join : only in the original main menu. Connect + authenticate; after Welcome the save isolation is installed, armed
//         and verified (SaveGuard.Arm), then the client systems are built and ClientReady is sent; once the host's world
//         snapshot arrived and (W4.2) the host's campaign was written into the sandbox (CampaignBootstrap), the original
//         MainMenu.Continue() loads it - never the client's own solo save. Back in the main menu after playing = the
//         session ends (rejoin with F8).
// The join code is shown only inside this window and never written to a log.
using System;
using System.IO;
using System.Reflection;
using HarmonyLib;
using TS2MP.Shared;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TS2MP.Plugin
{
    internal sealed class MultiplayerMenu : IDisposable
    {
        const string MenuScene = "MainMenuNew", WorldScene = "MainScene";
        readonly NetRunner _runner; readonly InstanceLog _log; readonly SessionSettings _settings; readonly Harmony _saveHarmony;
        readonly Lifecycle _lc = new Lifecycle();
        readonly bool _legacy;
        bool _isolationAvailable;
        SessionNode _pendingClient;   // client node until it is attached to the runner (ticked here)
        string _hostCode;             // formatted join code of the running host session (memory only)
        int _port;
        string _joinHost = "", _joinPort, _joinCode = "";
        string _hostGateReason = "Host startet";
        string _hint;
        // Developer test hook (not used by players): TS2MP_DEV_AUTOJOIN="host|port|code" in this process's environment
        // performs one Join from the main menu without UI input. Read and cleared at startup; the code is never logged.
        string _devJoin; float _devJoinAt;

        // game context
        bool _menuSceneLoaded, _worldSceneLoaded;
        float _lastSceneLoad;
        object _gm; float _nextGmProbe;
        Type _gmType, _menuType;
        Func<object, bool> _gmLoaded; Func<object, object> _gmPs;
        FieldInfo _fbppConfig; MethodInfo _fbppExists;

        // window (main menu only)
        bool _open; Rect _rect; GUIStyle _title, _text, _big, _err, _btn, _field; Vector2 _scroll;
        double _nextIgnoredLog;

        public MultiplayerMenu(NetRunner runner, InstanceLog log, SessionSettings settings, Harmony saveHarmony, bool legacy)
        {
            _runner = runner; _log = log; _settings = settings; _saveHarmony = saveHarmony; _legacy = legacy;
            _lc.Legacy = legacy;
            _port = settings.HostPort; _joinPort = NetConfig.DefaultPort.ToString();
            ResolveTypes();
            SceneManager.sceneLoaded += OnSceneLoaded;
            SceneManager.sceneUnloaded += OnSceneUnloaded;
            for (int i = 0; i < SceneManager.sceneCount; i++) Mark(SceneManager.GetSceneAt(i).name, true);
            if (!legacy) _isolationAvailable = SaveGuard.Precheck(saveHarmony, settings.DataDir, log);
            if (!legacy)
            {
                _devJoin = Environment.GetEnvironmentVariable("TS2MP_DEV_AUTOJOIN");
                if (_devJoin != null) { try { Environment.SetEnvironmentVariable("TS2MP_DEV_AUTOJOIN", null); } catch { } log.Info("DEV_AUTOJOIN test hook armed (one join from the main menu)"); }
            }
            // W4.2B.1: the developer launcher's fixed-role host listens from startup. Without this gate a client was accepted
            // in the host's main menu before the host save / Player-2 record were bound (CAMPAIGN snapshot NOT sent, the
            // client waited forever). Same readiness rules as the F8 host; the client keeps retrying (HostNotReady).
            var n = runner.Node;
            if (legacy && n != null && n.Role == Role.Host) { n.HostGate = () => _hostGateReason; _legacyHostGate = true; log.Info("MENU launcher host: joins wait until the host is gameplay-ready (" + _hostGateReason + ")"); }
            log.Info("MENU ready key=" + settings.MenuKey + (legacy ? " (launcher mode: status only)" : " isolationAvailable=" + _isolationAvailable));
        }
        bool _legacyHostGate; float _nextGate; string _loggedGate;
        bool _testHost, _worldLoadedOnce, _mismatchChecked;   // W4.2B.2 multiplayer test campaign

        void LegacyHostGate() { if (_legacyHostGate) RefreshHostGate(Context()); }

        // Host readiness re-evaluated 4 times per second (it is only read at a join attempt; launcher host and F8 host use the
        // same rules); transitions are logged as HOST_READINESS.
        void RefreshHostGate(GameContext ctx)
        {
            if (Time.realtimeSinceStartup < _nextGate) return;
            _nextGate = Time.realtimeSinceStartup + 0.25f;
            _hostGateReason = HostReadiness.Check(Facts(ctx));
            if (!string.Equals(_loggedGate, _hostGateReason)) { _loggedGate = _hostGateReason; _log.Info("HOST_READINESS " + (_hostGateReason ?? "ready: Player 2 may join")); }
        }

        void ResolveTypes()
        {
            _gmType = AccessTools.TypeByName("GameManager"); _menuType = AccessTools.TypeByName("MainMenu");
            _gmLoaded = Fast.Getter<bool>(_gmType, "loadedMap"); _gmPs = Fast.Getter<object>(_gmType, "PS");
            var fbpp = AccessTools.TypeByName("FBPP"); _fbppConfig = fbpp != null ? AccessTools.Field(fbpp, "_config") : null; _fbppExists = fbpp != null ? AccessTools.Method(fbpp, "DoesSaveFileExist") : null;
        }

        // ---------------------------------------------------------------- game context
        void Mark(string name, bool loaded)
        {
            if (name == MenuScene) _menuSceneLoaded = loaded;
            else if (name == WorldScene) { _worldSceneLoaded = loaded; _gm = null; _nextGmProbe = 0; if (loaded) _worldLoadedOnce = true; }
        }

        void OnSceneLoaded(Scene s, LoadSceneMode mode)
        {
            if (mode == LoadSceneMode.Single) { _menuSceneLoaded = false; _worldSceneLoaded = false; _gm = null; }
            Mark(s.name, true);
            _lastSceneLoad = Time.realtimeSinceStartup;
            if (_open && mode == LoadSceneMode.Single) CloseMenu("scene change");   // e.g. the original Continue started loading
        }

        void OnSceneUnloaded(Scene s) { Mark(s.name, false); }

        GameContext Context()
        {
            if (_menuSceneLoaded && !_worldSceneLoaded) return GameContext.MainMenu;
            if (!_worldSceneLoaded || _gmType == null) return GameContext.Loading;
            if (_gm == null || (_gm is UnityEngine.Object && !(UnityEngine.Object)_gm))
            {
                _gm = null;
                if (Time.realtimeSinceStartup < _nextGmProbe) return GameContext.Loading;
                _nextGmProbe = Time.realtimeSinceStartup + 0.5f;   // at most twice per second, only while loading
                _gm = UnityEngine.Object.FindObjectOfType(_gmType);
                if (_gm == null) return GameContext.Loading;
            }
            return _gmLoaded != null && _gmPs != null && _gmLoaded(_gm) && Alive(_gmPs(_gm)) ? GameContext.InWorld : GameContext.Loading;
        }

        static bool Alive(object o) { var u = o as UnityEngine.Object; return u != null && u; }

        HostFacts Facts(GameContext ctx)
        {
            var f = new HostFacts { Context = ctx, SecondsSinceSceneLoad = Time.realtimeSinceStartup - _lastSceneLoad };
            if (ctx == GameContext.InWorld) { f.LoadedMap = true; f.PlayerPresent = true; }
            int profile = PlayerPrefs.GetInt("Profile", 0);
            f.ProfileValid = profile >= 0 && profile <= 4 && _fbppConfig != null && _fbppConfig.GetValue(null) != null;
            var w = _runner.World; var p2 = _runner.P2; var v = _runner.Vehicles;
            f.SystemsReady = w != null && p2 != null && p2.Ready;
            f.RegionKnown = w != null && w.RegionKnown;
            f.PersistenceReady = p2 != null && p2.HostBookReady;
            f.VehiclesPending = v != null && !v.HostFleetReady;   // W4.2B.3: the host's cars are bound and their holders assigned
            return f;
        }

        // ---------------------------------------------------------------- per frame
        public void Update()
        {
            if (Input.GetKeyDown(_settings.MenuKey)) { if (_open) CloseMenu("key"); else OpenMenu(); }
            if (_open && !MenuRules.MenuOpens(Context())) CloseMenu("left the main menu");

            double now = Time.realtimeSinceStartup;
            if (_legacy) { LegacyHostGate(); return; }
            if (_pendingClient != null) _pendingClient.Tick();
            if (_devJoin != null && !_lc.Busy() && Context() == GameContext.MainMenu)
            {
                if (_devJoinAt == 0) _devJoinAt = Time.realtimeSinceStartup + 5f;
                else if (Time.realtimeSinceStartup >= _devJoinAt)
                {
                    var parts = _devJoin.Split('|'); _devJoin = null;
                    if (parts.Length == 3)
                    {
                        _log.Info("DEV_AUTOJOIN joining " + parts[0] + ":" + parts[1]);
                        _joinHost = parts[0]; _joinPort = parts[1]; _joinCode = parts[2];
                        StartJoin();
                    }
                    else _log.Warn("DEV_AUTOJOIN malformed (expected host|port|code)");
                }
            }
            if (!_lc.Busy()) return;
            var ctx = Context();
            if (_lc.IsHostState || (_lc.State == MpState.Connected && _hostCode != null)) HostFrame(ctx, now);
            else ClientFrame(ctx, now);
        }

        void HostFrame(GameContext ctx, double now)
        {
            var node = _runner.Node;
            if (node == null) { _lc.Fail("Host-Sitzung nicht vorhanden", now); _hostCode = null; return; }
            bool entered = _lc.HostWorldEntered;
            RefreshHostGate(ctx);
            if (_lc.HostTick(ctx, _hostGateReason, node.IsConnected, now)) { StopSession("host returned to the main menu"); return; }
            if (!entered && _lc.HostWorldEntered) _log.Info("HOST world loading started: the multiplayer systems were attached in the main menu; joins open once HOST_READINESS reports ready");
        }

        void ClientFrame(GameContext ctx, double now)
        {
            var node = _pendingClient ?? _runner.Node;
            if (node == null) { _lc.Fail("Client-Sitzung nicht vorhanden", now); return; }
            // W4.2B.2: a re-join in an already isolated process skips the preparation - the campaign type must still match.
            if (_pendingClient == null && !_mismatchChecked && (node.State == ConnState.Connected || node.State == ConnState.Preparing))
            {
                _mismatchChecked = true;
                if (SaveGuard.Armed && (SaveGuard.DevHost || SaveGuard.TestSandbox != node.HostTestCampaign))
                {
                    _log.Error("JOIN refused: host test campaign=" + node.HostTestCampaign + " but this process is isolated for " + (SaveGuard.TestSandbox ? "the test campaign" : "a normal campaign"));
                    _lc.Fail("Andere Kampagnenart als zuvor in diesem Spiel – bitte das Spiel neu starten.", now);
                    EndClient("campaign type mismatch");
                    return;
                }
            }
            var w = _runner.World;
            var cb = _runner.Campaign;   // W4.2: the host's campaign must be written into the sandbox before the original Continue
            var action = _lc.ClientTick(ctx, node.State, node.LastReject, node.FatalReason, node.HostWaitReason, w != null && w.Synced && cb != null && cb.Applied, now);
            switch (action)
            {
                case ClientAction.ArmIsolation: Prepare(node, ctx, now); break;
                case ClientAction.ContinueGame: ContinueOriginal(); break;
                case ClientAction.Abort: node.AbortPreparation("aborted"); EndClient("aborted: " + _lc.Error); break;
                case ClientAction.GiveUp: EndClient("connection ended: " + _lc.Error); break;
                case ClientAction.EndSession: StopSession("player 2 returned to the main menu"); break;
            }
        }

        // Authenticated + welcomed by a gameplay-ready host, still in the main menu: arm the isolation BEFORE anything
        // client-side touches persistence or loads a scene, then build the client systems and confirm.
        void Prepare(SessionNode node, GameContext ctx, double now)
        {
            if (ctx != GameContext.MainMenu) { node.AbortPreparation("left the main menu"); _lc.Fail("Hauptmenü wurde verlassen", now); EndClient("left main menu"); return; }
            string why;
            bool wasArmed = SaveGuard.Armed;
            // W4.2B.2: a test-campaign host -> test sandbox + test records; a normal host -> the normal sandbox. Fixed per process.
            bool test = node.HostTestCampaign;
            string dataDir = test ? SaveIsolation.TestCampaignDataDir(_settings.DataDir) : _settings.DataDir;
            if (!SaveGuard.SelectClientSandbox(dataDir, test, out why))
            {
                _log.Error("JOIN refused: " + why);
                node.AbortPreparation("campaign type differs from this process");
                _lc.Fail(test ? "Der Host spielt die Testkampagne. Bitte das Spiel neu starten und erneut beitreten." : "Dieses Spiel lief bereits in der Testkampagne. Bitte das Spiel neu starten.", now);
                EndClient("campaign type mismatch");
                return;
            }
            if (!SaveGuard.Arm(out why))
            {
                _log.Error("JOIN save isolation could not be verified: " + why);
                node.AbortPreparation("save isolation not verified");
                _lc.IsolationFailed(why, now);
                EndClient("isolation failed");
                if (SaveGuard.Armed || wasArmed) _runner.RequestQuit("SAVE_GUARD_FAILED after arming: quitting to protect the real saves (fail closed).");
                return;
            }
            _lc.IsolationArmed(now);
            if (test) { try { Directory.CreateDirectory(dataDir); } catch { } _runner.EnableTestCampaign(dataDir, "Player 2"); }
            _log.Info("JOIN save isolation armed and verified; building client systems" + (test ? " (multiplayer test campaign)" : ""));
            _pendingClient = null;
            if (!_runner.AttachSession(node))
            {
                node.AbortPreparation("client systems failed");
                _lc.Fail("Mehrspieler-Systeme konnten nicht gestartet werden (siehe Log)", now);
                EndClient("systems failed");
                return;
            }
            node.ConfirmReady();
            _lc.SystemsConfirmed(now);
        }

        void ContinueOriginal()
        {
            bool hasSave = false;
            try { hasSave = _fbppExists != null && (bool)_fbppExists.Invoke(null, null); } catch { }
            if (!hasSave)
            {
                _hint = "Die Weltdaten des Hosts konnten nicht übernommen werden (siehe Log).";
                _log.Error("JOIN no sandbox save after the host campaign was written: not continuing (Player 2 never starts a world of its own)");
                return;
            }
            var mm = _menuType != null ? UnityEngine.Object.FindObjectOfType(_menuType) : null;   // once
            var cont = _menuType != null ? AccessTools.Method(_menuType, "Continue") : null;
            if (mm == null || cont == null) { _hint = "Bitte im Hauptmenü auf „Fortsetzen“ klicken."; _log.Warn("JOIN MainMenu.Continue not found; the player continues manually"); return; }
            CloseMenu("continue");
            _log.Info("JOIN host world synchronized: loading the save through the original MainMenu.Continue()");
            try { cont.Invoke(mm, null); }
            catch (Exception ex) { _log.Error("JOIN MainMenu.Continue failed", ex); _hint = "Bitte im Hauptmenü auf „Fortsetzen“ klicken."; }
        }

        void EndClient(string why)
        {
            if (_pendingClient != null) { _pendingClient.Dispose(); _pendingClient = null; }
            else _runner.DetachSession(why);
        }

        void StopSession(string why)
        {
            _lc.BeginStop(Time.realtimeSinceStartup);
            if (_pendingClient != null) { _pendingClient.Dispose(); _pendingClient = null; }
            _runner.DetachSession(why);
            _hostCode = null; _hint = null;
            _lc.Stopped(Time.realtimeSinceStartup);
        }

        // ---------------------------------------------------------------- actions
        // W4.2B.3: main menu only (MenuRules.Host / Lifecycle.RequestHost refuse any other context). test = prepare the
        // isolated multiplayer test campaign first: its save redirect and test records must exist before the systems are built.
        void StartHost(bool test)
        {
            double now = Time.realtimeSinceStartup;
            var ctx = Context();
            var a = MenuRules.Host(ctx, _lc.State, _legacy, _lc.ClientModeSticky);
            if (!a.Enabled) { _hint = a.Reason; return; }
            if (!NetConfig.ValidPort(_port)) { _hint = "Ungültiger Port (1024–65535)."; return; }
            if (test && !_testHost && !PrepareTestCampaign()) return;
            string deny = _lc.RequestHost(ctx, _port, now);
            if (deny != null) { _hint = deny; return; }
            string code = JoinAuth.NewCode();
            var node = new SessionNode(Role.Host, RandomHex(6), RandomHex(4), Application.version, _port, _log);
            node.BindAddress = "0.0.0.0";
            node.JoinKey = JoinAuth.DeriveKey(code);
            node.HostGate = () => _hostGateReason;
            node.TestCampaign = _testHost && SaveGuard.DevHost && _runner.TestCampaignActive;   // W4.2B.2: told to Player 2 in Welcome
            _hostGateReason = "Host startet";
            if (!node.Start())
            {
                node.Dispose();
                _lc.HostStarted(false, "UDP-Port " + _port + " ist belegt oder blockiert – anderen Port wählen", now);
                return;
            }
            if (!_runner.AttachSession(node))
            {
                _runner.DetachSession("host systems failed");
                _lc.HostStarted(false, "Mehrspieler-Systeme konnten nicht gestartet werden (siehe Log)", now);
                return;
            }
            _hostCode = JoinAuth.Format(code);
            _hint = null;
            _loggedGate = null; _nextGate = 0;
            _lc.HostStarted(true, null, now);
            _log.Info("HOST prepared in the main menu: UDP 0.0.0.0:" + _port + " listening, session systems attached before the save loads (join code shown in the menu only)"
                + (node.TestCampaign ? " - MULTIPLAYER TEST CAMPAIGN" : "") + "; joins wait for the loaded world (HOST_READINESS)");
        }

        void StartJoin()
        {
            double now = Time.realtimeSinceStartup;
            JoinRequest req;
            if (_testHost) { _hint = MenuRules.TestHostJoinText; return; }
            string deny = _lc.RequestJoin(Context(), _isolationAvailable, _joinHost, _joinPort, _joinCode, out req, now);
            if (deny != null) { _hint = deny; return; }
            var node = new SessionNode(Role.Client, "join", RandomHex(4), Application.version, req.Port, _log);
            node.ConnectAddress = req.Host;
            node.JoinKey = JoinAuth.DeriveKey(req.Code);
            node.AutoReady = SaveGuard.Armed;   // already isolated earlier in this process: no new preparation needed
            _mismatchChecked = false;
            if (!node.Start()) { node.Dispose(); _lc.Fail("Netzwerk konnte nicht gestartet werden", now); return; }
            _joinCode = "";   // the code is not kept in the field after use
            _hint = null;
            if (node.AutoReady)
            {
                // Isolation already active: attach the systems now so they see the connection from the start.
                if (!_runner.AttachSession(node)) { _runner.DetachSession("client systems failed"); _lc.Fail("Mehrspieler-Systeme konnten nicht gestartet werden", now); return; }
                _lc.IsolationArmed(now);
            }
            else _pendingClient = node;
            _log.Info("JOIN started from the F8 menu -> " + req.Host + ":" + req.Port + (node.AutoReady ? " (isolation already armed)" : ""));
        }

        // ---------------------------------------------------------------- window
        // W4.2B.3: only in the original main menu. Elsewhere the key press is ignored (logged at most every 30 s, BepInEx log
        // only - no in-game text); nothing in the player, camera or input state is touched.
        void OpenMenu()
        {
            var ctx = Context();
            if (!MenuRules.MenuOpens(ctx))
            {
                double now = Time.realtimeSinceStartup;
                if (now >= _nextIgnoredLog) { _nextIgnoredLog = now + 30; _log.Info("MENU " + _settings.MenuKey + " ignored outside the main menu (context " + ctx + "; multiplayer is prepared / joined in the main menu)"); }
                return;
            }
            _open = true;
            _hint = null;
            _rect = new Rect((Screen.width - 560) / 2f, (Screen.height - 470) / 2f, 560, 470);
        }

        void CloseMenu(string why)
        {
            _open = false;
            GUIUtility.keyboardControl = 0;
        }

        public void OnGUI()
        {
            if (!_open) return;
            if (_title == null)
            {
                _title = new GUIStyle(GUI.skin.label) { fontSize = 20, fontStyle = FontStyle.Bold, wordWrap = true };
                _text = new GUIStyle(GUI.skin.label) { fontSize = 15, wordWrap = true };
                _big = new GUIStyle(GUI.skin.label) { fontSize = 24, fontStyle = FontStyle.Bold };
                _err = new GUIStyle(_text); _err.normal.textColor = new Color(1f, 0.55f, 0.5f);
                _btn = new GUIStyle(GUI.skin.button) { fontSize = 15 };
                _field = new GUIStyle(GUI.skin.textField) { fontSize = 16 };
            }
            _rect = GUI.Window(0x7532, _rect, DrawWindow, "");
        }

        void DrawWindow(int id)
        {
            var ctx = Context();
            GUILayout.Label("Mehrspieler – TS2-Multiplayer " + ProtocolInfo.ModVersion, _title);
            _scroll = GUILayout.BeginScrollView(_scroll);
            GUILayout.Label("Status: " + (_legacy ? LegacyStatus() : _lc.Status), _text);
            if (_lc.State == MpState.Error && _lc.Error != null) GUILayout.Label(_lc.Error, _err);
            if (_runner.TestCampaignActive) GUILayout.Label("TESTKAMPAGNE AKTIV – isolierter Test-Spielstand, alle Fähigkeiten, Testgeld. Deine echten Spielstände werden nicht benutzt.", _err);
            GUILayout.Space(8);

            if (_legacy) GUILayout.Label(MenuRules.LegacyText, _text);
            else if (_hostCode != null && (_lc.IsHostState || _lc.State == MpState.Connected)) DrawHosting();
            else if (_lc.Busy() && !_lc.IsHostState) DrawClient();
            else
            {
                DrawHostSection(ctx);
                GUILayout.Space(10);
                DrawTestSection(ctx);
                GUILayout.Space(10);
                DrawJoinSection(ctx);
            }
            if (_hint != null) { GUILayout.Space(6); GUILayout.Label(_hint, _err); }
            GUILayout.EndScrollView();
            if (GUILayout.Button("Schließen (" + _settings.MenuKey + ")", _btn)) CloseMenu("button");
            GUI.DragWindow(new Rect(0, 0, 10000, 30));
        }

        string LegacyStatus()
        {
            var n = _runner.Node;
            return n == null ? "kein Netzwerk" : (n.Role == Role.Host ? "Host" : "Spieler 2") + " – " + n.State;
        }

        void DrawHostSection(GameContext ctx)
        {
            GUILayout.Label("Spiel hosten (du bist Spieler 1)", _title);
            var a = MenuRules.Host(ctx, _lc.State, _legacy, _lc.ClientModeSticky);
            GUILayout.BeginHorizontal();
            GUILayout.Label("UDP-Port: " + _port, _text, GUILayout.Width(170));
            GUI.enabled = a.Enabled;
            if (GUILayout.Button("−100", _btn)) _port = Clamp(_port - 100);
            if (GUILayout.Button("−1", _btn)) _port = Clamp(_port - 1);
            if (GUILayout.Button("+1", _btn)) _port = Clamp(_port + 1);
            if (GUILayout.Button("+100", _btn)) _port = Clamp(_port + 100);
            if (GUILayout.Button("Standard", _btn)) _port = NetConfig.DefaultPort;
            GUILayout.EndHorizontal();
            if (GUILayout.Button(_runner.TestCampaignActive ? "Testkampagne hosten" : "Hosten vorbereiten", _btn)) StartHost(_runner.TestCampaignActive);
            GUI.enabled = true;
            if (!a.Enabled) GUILayout.Label(a.Reason, _text);
            else GUILayout.Label("Danach lädst du deinen Spielstand wie gewohnt (Fortsetzen oder ein Spiel beginnen); der Mehrspieler startet automatisch, sobald deine Welt geladen ist. Der Router muss UDP " + _port + " an diesen PC weiterleiten. Der Spielverkehr ist nicht verschlüsselt.", _text);
        }

        // W4.2B.2 multiplayer test campaign (host). Explicit opt-in in the main menu; W4.2B.3: the same button prepares the host
        // session, then the original Continue / New Game loads the isolated copy. Player 2 needs no option: the host's Welcome
        // tells its game.
        void DrawTestSection(GameContext ctx)
        {
            if (_testHost || ctx != GameContext.MainMenu) return;   // offered only in the main menu, once
            var a = MenuRules.PrepareTest(ctx, _lc.State, _legacy, _lc.ClientModeSticky, _isolationAvailable, _testHost, _worldLoadedOnce);
            GUILayout.Label("Mehrspieler-Testkampagne hosten (nur zum Testen)", _title);
            GUILayout.Label("Isolierte KOPIE deines Spielstands mit allen Fähigkeiten und Testgeld für beide Spieler. Dein echter Spielstand und deine normalen Mehrspieler-Daten werden nicht verändert. Für Einzelspieler danach das Spiel neu starten.", _text);
            GUI.enabled = a.Enabled;
            if (GUILayout.Button("Testkampagne hosten", _btn)) StartHost(true);
            GUI.enabled = true;
            if (!a.Enabled) GUILayout.Label(a.Reason, _text);
        }

        bool PrepareTestCampaign()
        {
            var a = MenuRules.PrepareTest(Context(), _lc.State, _legacy, _lc.ClientModeSticky, _isolationAvailable, _testHost, _worldLoadedOnce);
            if (!a.Enabled) { _hint = a.Reason; return false; }
            string dir = SaveIsolation.TestCampaignDataDir(_settings.DataDir), why;
            try { Directory.CreateDirectory(dir); } catch (Exception ex) { _hint = "Testordner konnte nicht angelegt werden: " + ex.Message; return false; }
            bool wasArmed = SaveGuard.Armed;
            if (!SaveGuard.PrepareTestHostSave(dir, out why))
            {
                _log.Error("TEST_CAMPAIGN save isolation could not be verified: " + why);
                _hint = "Testkampagne konnte nicht sicher vorbereitet werden (siehe Log).";
                if (SaveGuard.Armed || wasArmed) _runner.RequestQuit("SAVE_GUARD_FAILED after arming the test campaign: quitting to protect the real saves (fail closed).");
                return false;
            }
            _runner.EnableTestCampaign(dir, "F8 host");
            _testHost = true; _hint = null;
            RefreshContinueText();
            _log.Info("TEST_CAMPAIGN prepared in the main menu: the original Continue / New Game now loads the isolated copy; the host session is prepared next");
            return true;
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

        void DrawJoinSection(GameContext ctx)
        {
            GUILayout.Label("Spiel beitreten (du bist Spieler 2)", _title);
            var a = MenuRules.Join(ctx, _lc.State, _legacy, _isolationAvailable, _testHost);
            if (!a.Enabled) { GUILayout.Label(a.Reason, _text); return; }
            GUILayout.Label("Adresse des Hosts (öffentliche IPv4 oder DNS-Name):", _text);
            _joinHost = GUILayout.TextField(_joinHost ?? "", 253, _field);
            GUILayout.Label("UDP-Port:", _text);
            _joinPort = GUILayout.TextField(_joinPort ?? "", 5, _field);
            GUILayout.Label("Beitrittscode:", _text);
            _joinCode = GUILayout.TextField(_joinCode ?? "", 40, _field);
            if (GUILayout.Button("Verbinden", _btn)) StartJoin();
            GUILayout.Label("Deine eigenen Einzelspieler-Spielstände bleiben unverändert (Spieler 2 nutzt einen geschützten Bereich).", _text);
        }

        void DrawHosting()
        {
            var node = _runner.Node;
            GUILayout.Label("Du hostest (Spieler 1) auf UDP " + _port + (_runner.TestCampaignActive ? " – Testkampagne" : ""), _text);
            GUILayout.Label("Beitrittscode:", _text);
            GUILayout.Label(_hostCode, _big);
            if (GUILayout.Button("Code in die Zwischenablage kopieren", _btn)) { GUIUtility.systemCopyBuffer = _hostCode; _hint = "Code kopiert."; }
            GUILayout.Label("Spieler 2: " + (node != null && node.IsConnected ? "verbunden" : "nicht verbunden"), _text);
            GUILayout.Label("Gib deinem Mitspieler deine öffentliche IPv4-Adresse (Router) oder deinen DynDNS-Namen, den Port und den Code. Der Code gilt nur für diese Sitzung.", _text);
            GUILayout.Label("Jetzt dieses Fenster schließen und im Hauptmenü „Fortsetzen“ wählen (ohne Spielstand: ein Spiel beginnen). Zurück im Hauptmenü endet die Sitzung.", _text);
            if (GUILayout.Button("Hosten abbrechen", _btn)) StopSession("cancelled by the player in the main menu");
        }

        void DrawClient()
        {
            GUILayout.Label("Rolle: Spieler 2", _text);
            if (GUILayout.Button(_lc.State == MpState.Connected ? "Verbindung trennen" : "Abbrechen", _btn)) StopSession("disconnected by the player");
            if (_lc.ClientModeSticky) GUILayout.Label("Hinweis: Für Einzelspieler oder Hosten muss das Spiel danach neu gestartet werden.", _text);
        }

        static int Clamp(int p) { return Math.Max(1024, Math.Min(65535, p)); }

        static string RandomHex(int bytes)
        {
            var b = new byte[bytes];
            using (var r = System.Security.Cryptography.RandomNumberGenerator.Create()) r.GetBytes(b);
            return BitConverter.ToString(b).Replace("-", "").ToLowerInvariant();
        }

        public void Dispose()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneUnloaded -= OnSceneUnloaded;
            if (_open) CloseMenu("shutdown");
            if (_pendingClient != null) { _pendingClient.Dispose(); _pendingClient = null; }
        }
    }
}

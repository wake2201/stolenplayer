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
//         session ends (rejoin with F8). v0.16.1: from the join request until that Continue the main menu shows its ORIGINAL
//         loading state and its buttons take no input (JoinLoadingScreen, MenuRules.JoinLoadingScreen); cancel, timeout or
//         a failed verification restores the menu. The host's prepared menu is never locked.
// The join code is shown only inside this window and never written to a log.
// W6A.1: two windows on this one session controller (one Lifecycle -> never two sessions): F8 = the public multiplayer menu
// (normal Host / Join only, styled: MultiplayerMenu.Ui.cs), F10 = the developer menu (isolated test campaign host / join,
// story-step selector, scenario reset, technical status: MultiplayerMenu.Dev.cs), present only with
// Developer.EnableDeveloperMenu = true. MenuRouting (src/Shared/MultiplayerLifecycle.cs) decides the open panel, the join
// kind check after the host's Welcome and the availability of each action.
using System;
using System.IO;
using System.Reflection;
using HarmonyLib;
using TS2MP.Shared;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TS2MP.Plugin
{
    internal sealed partial class MultiplayerMenu : IDisposable
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

        // windows (main menu only)
        MenuPanel _panel; bool _open { get { return _panel != MenuPanel.None; } }
        readonly bool _devAllowed; JoinKind _joinKind = JoinKind.Public;
        double _nextIgnoredLog;

        public MultiplayerMenu(NetRunner runner, InstanceLog log, SessionSettings settings, Harmony saveHarmony, bool legacy)
        {
            _runner = runner; _log = log; _settings = settings; _saveHarmony = saveHarmony; _legacy = legacy;
            _lc.Legacy = legacy;
            _port = settings.HostPort; _joinPort = NetConfig.DefaultPort.ToString(); _portText = _port.ToString();
            _devAllowed = MenuRouting.DevMenuAllowed(settings.DevMenuEnabled, legacy);
            ResolveTypes();
            SceneManager.sceneLoaded += OnSceneLoaded;
            SceneManager.sceneUnloaded += OnSceneUnloaded;
            for (int i = 0; i < SceneManager.sceneCount; i++) Mark(SceneManager.GetSceneAt(i).name, true);
            if (!legacy) _isolationAvailable = SaveGuard.Precheck(saveHarmony, settings.DataDir, log);
            if (!legacy) _loading = new JoinLoadingScreen(log);
            if (!legacy)
            {
                _devJoin = Environment.GetEnvironmentVariable("TS2MP_DEV_AUTOJOIN");
                if (_devJoin != null) { try { Environment.SetEnvironmentVariable("TS2MP_DEV_AUTOJOIN", null); } catch { } log.Info("DEV_AUTOJOIN test hook armed (one join from the main menu)"); }
            }
            // W4.2B.1: the developer launcher's fixed-role host listens from startup. Without this gate a client was accepted
            // in the host's main menu before the host save / Player-2 record were bound (CAMPAIGN snapshot NOT sent, the
            // client waited forever). Same readiness rules as the F8 host; the client keeps retrying (HostNotReady).
            var n = runner.Node;
            if (legacy && n != null && n.Role == Role.Host) { n.HostGate = () => _gateNow; _legacyHostGate = true; log.Info("MENU launcher host: joins wait until the host is gameplay-ready (" + _hostGateReason + ")"); }
            log.Info("MENU ready key=" + settings.MenuKey + (legacy ? " (launcher mode: status only)" : " isolationAvailable=" + _isolationAvailable)
                + "; developer menu " + (_devAllowed ? "ENABLED key=" + settings.DevMenuKey + " (Developer.EnableDeveloperMenu)" : "off"));
        }
        bool _legacyHostGate; float _nextGate;
        JoinLoadingScreen _loading;   // v0.16.1 (F8 mode only)
        bool _worldLoadedOnce, _mismatchChecked;   // W4.2B.2 multiplayer test campaign (prepared flag: _lc.TestPrepared, W4.2C.A)

        void LegacyHostGate() { if (_legacyHostGate) { RefreshHostGate(Context()); UpdateGate(); } }

        // Host readiness re-evaluated 4 times per second (it is only read at a join attempt; launcher host and F8 host use the
        // same rules); transitions are logged as HOST_READINESS.
        void RefreshHostGate(GameContext ctx)
        {
            if (Time.realtimeSinceStartup < _nextGate) return;
            _nextGate = Time.realtimeSinceStartup + 0.25f;
            _hostGateReason = HostReadiness.Check(Facts(ctx));
        }

        // W6A.5: the ONE readiness state (JoinGate) = world readiness + developer scenario state. The network gate (HostGate /
        // HostGateFatal), HOST_READINESS log, lifecycle status, F8 chip and F10 status all read _gateNow; it changes reference
        // only on a real transition (logged once). Before: the log / F10 read the world gate alone ("Player 2 may join") while
        // the network also applied the scenario gate and refused every join.
        string _gateNow = "Host startet";
        void UpdateGate()
        {
            string g = JoinGate.Reason(_hostGateReason, _scenarioState, _scenarioDetail);
            if (string.Equals(g, _gateNow)) return;
            _gateNow = g;
            _log.Info("HOST_READINESS " + JoinGate.LogLine(g, JoinGate.Fatal(_scenarioState)));
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
            if (Input.GetKeyDown(_settings.MenuKey)) Hotkey(MenuPanel.Public);
            else if (_devAllowed && Input.GetKeyDown(_settings.DevMenuKey)) Hotkey(MenuPanel.Developer);
            if (_open && !MenuRules.MenuOpens(Context())) CloseMenu("left the main menu");
            SyncInputBlock();

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
                        StartJoin(JoinKind.Any);
                    }
                    else _log.Warn("DEV_AUTOJOIN malformed (expected host|port|code)");
                }
            }
            if (!_lc.Busy()) { SyncLoading(); return; }
            var ctx = Context();
            if (_lc.IsHostState || (_lc.State == MpState.Connected && _hostCode != null)) HostFrame(ctx, now);
            else ClientFrame(ctx, now);
            SyncLoading();
        }

        // v0.16.1: original loading presentation + menu input lock while Player 2 joins (MenuRules.JoinLoadingScreen).
        void SyncLoading()
        {
            if (_loading == null) return;
            var s = _lc.State;   // cheap state test first: no context probe unless Player 2 is joining
            bool joining = _hostCode == null && (s == MpState.ClientConnecting || s == MpState.ClientPreparing || s == MpState.ClientSynchronizing);
            bool show = joining && MenuRules.JoinLoadingScreen(s, Context(), _lc.ContinueIssued);
            if (show && !_loading.Shown) ReleaseInputBlock();   // the loading screen records + owns the EventSystem state from here
            if (show || _loading.Shown) _loading.Sync(show);
        }

        void HostFrame(GameContext ctx, double now)
        {
            var node = _runner.Node;
            if (node == null) { _lc.Fail("Host-Sitzung nicht vorhanden", now); _hostCode = null; return; }
            bool entered = _lc.HostWorldEntered;
            RefreshHostGate(ctx);
            if (_devHosting) DevHostTick(ctx);   // W6A.3: verify the loaded story index once, report "host ready"
            UpdateGate();                         // W6A.5: world + scenario -> one readiness state
            if (_lc.HostTick(ctx, _gateNow, node.IsConnected, now)) { StopSession("host returned to the main menu"); return; }
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
                string kindErr = MenuRouting.JoinMismatch(_joinKind, node.HostTestCampaign);
                if (kindErr != null)
                {
                    _log.Error("JOIN refused: host test campaign=" + node.HostTestCampaign + ", join started from the " + _joinKind + " menu");
                    _lc.Fail(kindErr, now); EndClient("campaign kind mismatch"); return;
                }
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
                case ClientAction.ContinueGame: if (!ContinueOriginal()) { _lc.Fail(_hint ?? "Laden fehlgeschlagen (siehe Log)", now); EndClient("continue failed"); } break;
                case ClientAction.Abort: node.AbortPreparation("aborted"); EndClient("aborted: " + _lc.Error); break;
                case ClientAction.GiveUp:
                    if (_joinKind == JoinKind.Developer && node.LastReject == RejectCode.AuthFailed) { _hint = MenuRouting.DevAuthFailedText; _log.Warn("JOIN developer test join refused by the host (AuthFailed): no developer test host at that address"); }
                    else if (_joinKind == JoinKind.Developer) _hint = MenuRouting.DevHostUnreachableText;
                    EndClient("connection ended: " + _lc.Error); break;
                case ClientAction.EndSession: StopSession("player 2 returned to the main menu"); break;
            }
            if (_joinKind == JoinKind.Developer && _lc.State == MpState.Connected) DevClientTick(ctx);   // W6A.4
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
            string kindErr = MenuRouting.JoinMismatch(_joinKind, test);   // W6A.1: F8 never joins a test campaign, F10 only test campaigns
            if (kindErr != null)
            {
                _log.Error("JOIN refused: host test campaign=" + test + ", join started from the " + _joinKind + " menu");
                node.AbortPreparation("campaign kind differs from the menu");
                _lc.Fail(kindErr, now); EndClient("campaign kind mismatch");
                return;
            }
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

        // v0.16.1: false = the original load could not be started; the caller ends the join (the main menu is restored - the
        // player never has to click Continue behind the loading screen, and Continue stays gated by CampaignBootstrap).
        bool ContinueOriginal()
        {
            bool hasSave = false;
            try { hasSave = _fbppExists != null && (bool)_fbppExists.Invoke(null, null); } catch { }
            if (!hasSave)
            {
                _hint = "Die Weltdaten des Hosts konnten nicht übernommen werden (siehe Log).";
                _log.Error("JOIN no sandbox save after the host campaign was written: not continuing (Player 2 never starts a world of its own)");
                return false;
            }
            var mm = _menuType != null ? UnityEngine.Object.FindObjectOfType(_menuType) : null;   // once
            var cont = _menuType != null ? AccessTools.Method(_menuType, "Continue") : null;
            if (mm == null || cont == null) { _hint = "Das Hauptmenü des Spiels wurde nicht gefunden (siehe Log)."; _log.Error("JOIN MainMenu.Continue not found: join ended"); return false; }
            CloseMenu("continue");
            if (_loading != null) _loading.HandOff();
            _log.Info("JOIN host world synchronized: loading the save through the original MainMenu.Continue()");
            try { cont.Invoke(mm, null); return true; }
            catch (Exception ex) { _log.Error("JOIN MainMenu.Continue failed", ex); _hint = "Laden fehlgeschlagen (siehe Log)."; return false; }
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
            _hostCode = null; _hint = null; _devHosting = false; ClearScenario(); _p2CheckState = 0;
            _lc.Stopped(Time.realtimeSinceStartup);
        }

        // W4.2C.A: "Hosten abbrechen" in the main menu. Root cause of the vanished Join option: the session itself was ended
        // (listening socket closed, systems disposed, state Offline), but the test-campaign preparation of "Testkampagne
        // hosten" (test save copy armed, test records, prepared flag) stayed, and MenuRules.Join refuses a test-host process.
        // Now the cancel releases it again while no world was ever loaded in this process.
        void CancelHost(string why)
        {
            StopSession(why);
            ReleaseTestIfDue("prepared host cancelled");
            _log.Info("HOST cancelled in the main menu: state " + _lc.State + ", test campaign " + (_lc.TestPrepared ? "still prepared" : "not prepared") + ", save isolation " + (SaveGuard.Armed ? "ARMED" : "off"));
        }

        void ReleaseTestIfDue(string why)
        {
            if (!_lc.TestReleaseDue(_worldLoadedOnce)) return;
            string err;
            if (!SaveGuard.ReleaseTestHostSave(out err))
            {
                _log.Error("TEST_CAMPAIGN not released (" + why + "): " + err + " - the test campaign stays active in this process");
                _hint = "Die Testkampagne bleibt aktiv. Zum Beitreten oder für Einzelspieler bitte das Spiel neu starten.";
                if (SaveGuard.ReArmFailed) _runner.RequestQuit("SAVE_GUARD_FAILED re-arming the test campaign after a failed release: quitting to protect the real saves (fail closed).");
                return;
            }
            _runner.DisableTestCampaign(why);
            _lc.TestCampaignReleased();
            RefreshContinueText();
            _log.Info("TEST_CAMPAIGN released (" + why + "): main menu back to the offline state (Join, Host and Test Host available); test copy kept");
        }

        // ---------------------------------------------------------------- actions
        // W4.2B.3: main menu only (MenuRules.Host / Lifecycle.RequestHost refuse any other context). test = prepare the
        // isolated multiplayer test campaign first: its save redirect and test records must exist before the systems are built.
        void StartHost(bool test)
        {
            double now = Time.realtimeSinceStartup;
            var ctx = Context();
            var a = test ? MenuRules.Host(ctx, _lc.State, _legacy, _lc.ClientModeSticky) : MenuRouting.PublicHost(ctx, _lc.State, _legacy, _lc.ClientModeSticky, TestActive);
            if (!a.Enabled) { _hint = a.Reason; return; }
            if (test && !_devAllowed) { _hint = "Entwicklermenü ist deaktiviert."; return; }   // never reachable from F8
            int port;
            if (!NetConfig.TryParsePort(_portText, out port)) { _hint = "Ungültiger Port (1024–65535)."; return; }
            _port = port;
            // W6A.3: plan -> isolated sandbox armed + verified -> seed source checked; any failure is a visible F10 error and the
            // sandbox is released (no test campaign is active then). The listener starts only after this succeeded.
            DevPlan plan = null;
            if (test && !DevPrepare(out plan)) return;
            string deny = _lc.RequestHost(ctx, _port, now);
            if (deny != null) { _hint = deny; if (test) DevError(deny); ReleaseTestIfDue("host request refused"); return; }
            string code = JoinAuth.NewCode();
            var node = new SessionNode(Role.Host, RandomHex(6), RandomHex(4), Application.version, _port, _log);
            node.BindAddress = "0.0.0.0";
            if (test) node.JoinKey = MenuRouting.SessionKey(true, null);   // W6A.2: developer key, no code (F10 Test Join)
            else node.JoinKey = JoinAuth.DeriveKey(code);
            node.HostGate = () => _gateNow;                                   // W6A.5: one readiness state (JoinGate)
            node.HostGateFatal = () => JoinGate.Fatal(_scenarioState);         // invalid story test: permanent refusal, client stops
            node.TestCampaign = _lc.TestPrepared && SaveGuard.DevHost && _runner.TestCampaignActive;   // W4.2B.2: told to Player 2 in Welcome
            _hostGateReason = "Host startet"; _gateNow = _hostGateReason;
            if (!node.Start())
            {
                node.Dispose();
                _lc.HostStarted(false, "UDP-Port " + _port + " ist belegt oder blockiert – anderen Port wählen", now);
                if (test) DevError("UDP-Port " + _port + " ist belegt oder blockiert – anderen Port wählen.");
                ReleaseTestIfDue("host start failed");
                return;
            }
            if (!_runner.AttachSession(node))
            {
                _runner.DetachSession("host systems failed");
                _lc.HostStarted(false, "Mehrspieler-Systeme konnten nicht gestartet werden (siehe Log)", now);
                if (test) DevError("Mehrspieler-Systeme konnten nicht gestartet werden (siehe Log).");
                ReleaseTestIfDue("host systems failed");
                return;
            }
            _hostCode = test ? DevHostLabel : JoinAuth.Format(code); _devHosting = test;
            _hint = null;
            _nextGate = 0;
            _lc.HostStarted(true, null, now);
            _log.Info("HOST prepared in the main menu: UDP 0.0.0.0:" + _port + " listening, session systems attached before the save loads (join code shown in the menu only)"
                + (node.TestCampaign ? " - MULTIPLAYER TEST CAMPAIGN" : "") + "; joins wait for the loaded world (HOST_READINESS)");
            if (test) DevAutoLoad(plan);   // W6A.3: the original load starts by itself (no click in the original menu)
        }

        void StartJoin(JoinKind kind)
        {
            double now = Time.realtimeSinceStartup;
            JoinRequest req;
            if (_lc.TestPrepared) { _hint = MenuRules.TestHostJoinText; return; }
            if (kind == JoinKind.Public && TestActive) { _hint = MenuRouting.DevActiveText; return; }
            if (kind == JoinKind.Developer && !_devAllowed) { _hint = "Entwicklermenü ist deaktiviert."; return; }
            _joinKind = kind;
            string deny = kind == JoinKind.Developer ? _lc.RequestDevJoin(Context(), _isolationAvailable, _joinHost, _joinPort, out req, now)
                : _lc.RequestJoin(Context(), _isolationAvailable, _joinHost, _joinPort, _joinCode, out req, now);
            if (deny != null) { _hint = deny; return; }
            var node = new SessionNode(Role.Client, "join", RandomHex(4), Application.version, req.Port, _log);
            node.ConnectAddress = req.Host;
            node.JoinKey = kind == JoinKind.Developer ? MenuRouting.SessionKey(true, null) : JoinAuth.DeriveKey(req.Code);
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
            _log.Info("JOIN started from the " + (kind == JoinKind.Developer ? "developer" : kind == JoinKind.Public ? "F8" : "DEV_AUTOJOIN") + " menu -> " + req.Host + ":" + req.Port + (node.AutoReady ? " (isolation already armed)" : ""));
        }

        // W6A.1: a process that prepared / played the isolated test campaign never offers the normal F8 Host / Join.
        bool TestActive { get { return _lc.TestPrepared || _runner.TestCampaignActive || (SaveGuard.Armed && SaveGuard.DevHost); } }

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
            ReleaseInputBlock();
            DisposeUi();
            if (_loading != null && _loading.Shown) _loading.Sync(false);
            if (_pendingClient != null) { _pendingClient.Dispose(); _pendingClient = null; }
        }
    }
}

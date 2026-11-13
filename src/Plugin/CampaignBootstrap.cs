// W4.2 Phase A: host-owned campaign bootstrap for Player 2 + persistent Player-2 spawn (design: src/Shared/Campaign.cs).
//
// HOST: on every client connection (after ClientReady) the host builds ONE CampaignSnapshot from its live FBPP model
// (FBPP._latestData = what FBPP.GetSaveFileAsJson serialises; verified IL), filtered by CampaignPolicy, with Player 2's
// host-owned record (P2Inventory) and a spawn plan (SpawnRules) from p2spawn-<profile>.dat (host data folder, atomic
// write + .bak, background writer). Player 2's position reports ("p2pos" proposals, every few seconds while the position
// is valid, plus one at every exit to the main menu) update that record; it is written at most every 5 s, at disconnect
// and at shutdown - never only at disconnect (crash / network loss keep the last periodic write).
// CLIENT: receives the snapshot in the main menu, refuses it unless the save isolation is armed and verified, writes it
// into the SANDBOX save with the original FBPP.OverwriteLocalSaveFile (WriteToSaveFile + reload; verified IL) and verifies
// the reload. Only then may the original MainMenu.Continue run (Harmony prefix: blocked otherwise while this process is
// Player 2; new games are always blocked for Player 2 - no second campaign). After the load the spawn plan is executed with
// the arrival checks of VehicleSync (ground, slope, free capsule, other player, traffic); a client back in the main menu
// while connected asks for a fresh snapshot (CampaignRequest) so Continue always rejoins through the host's decision.
// Harmony (client only, installed once when this process is Player 2): prefixes MainMenu.Continue / StartNewGame /
// StartFreeMode / ClickedNewGame (UI-event entry points, no code callers - verified), SaveGame.LoseGame (exit report;
// empty original body, callers GameManager.LoadMenu / Inventory.QuitGame / SaveGame.OnApplicationQuit - verified M4.3.3).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using TS2MP.Shared;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TS2MP.Plugin
{
    internal sealed class CampaignBootstrap : IDisposable
    {
        public static CampaignBootstrap Instance;
        readonly SessionNode _node; readonly InstanceLog _log; readonly WorldSync _world; readonly bool _host; readonly string _dataDir; readonly bool _autoContinue;
        readonly NetDataWriter _w = new NetDataWriter();
        bool _ok;

        // game accessors (compiled once)
        Func<object, object> _gmPs, _gmStory, _psPMB, _psPCB, _psCC, _smScript;
        Func<object, bool> _gmLoaded, _psInVeh, _psHiding, _psChase, _psCaught, _pmbGrounded, _ssBlockSave, _ssBlockAll, _psLockpicking;
        Func<object, int> _smStory; Func<object, float> _pcbYaw; Action<object, float> _pcbSetYaw;

        // ---- host ----
        FleetStore _store; FleetWriter _writer; int _storeProfile = int.MinValue, _storeHistory;
        P2SpawnRecord _rec; string _recErr; bool _dirty; double _nextWrite; double _requestAt = -1, _lastRequestServed = -100;
        long _reports, _snapshots, _requests;

        // ---- client ----
        static Harmony _h; static bool _patched; static volatile bool _allowContinue; static string _blockedLogged;
        bool _applied; SpawnPlan _plan; bool _placePending; int _placeRegion = Regions.None; double _regionAt; bool _menuSeen; double _menuAt;
        bool _autoDone; double _nextReport; P2PosReport _last; bool _hasLast; double _lastSentAt; uint _lastPosId; long _sent, _rejected;

        public bool Ready { get { return _ok; } }
        internal VehicleSync Vehicles;   // checked placement helpers (set by NetRunner after construction)
        public bool Applied { get { return _host || _applied; } }   // client: a campaign snapshot was written in this connection

        public CampaignBootstrap(SessionNode node, InstanceLog log, WorldSync world, string dataDir, bool autoContinue)
        {
            _node = node; _log = log; _world = world; _host = node.Role == Role.Host; _dataDir = dataDir; _autoContinue = autoContinue;
            var gm = AccessTools.TypeByName("GameManager"); var ps = AccessTools.TypeByName("PlayerScript"); var pmb = AccessTools.TypeByName("PlayerMotorBehavior");
            var pcb = AccessTools.TypeByName("PlayerCameraBehavior"); var sm = AccessTools.TypeByName("StoryManager"); var ss = AccessTools.TypeByName("StoryScript");
            _gmPs = Fast.Getter<object>(gm, "PS"); _gmLoaded = Fast.Getter<bool>(gm, "loadedMap"); _gmStory = Fast.Getter<object>(gm, "story_manager");
            _psPMB = Fast.Getter<object>(ps, "PMB"); _psPCB = Fast.Getter<object>(ps, "PCB"); _psCC = Fast.Getter<object>(ps, "CC");
            _psInVeh = Fast.Getter<bool>(ps, "is_in_vehicle"); _psHiding = Fast.Getter<bool>(ps, "isHiding"); _psChase = Fast.Getter<bool>(ps, "isPoliceChase");
            _psCaught = Fast.Getter<bool>(ps, "caughtUp"); _psLockpicking = Fast.Getter<bool>(ps, "isLockpicking");
            _pmbGrounded = Fast.Getter<bool>(pmb, "isGrounded");
            _pcbYaw = Fast.Getter<float>(pcb, "yaw"); _pcbSetYaw = Fast.Call<float>(pcb, "SetYaw");
            _smScript = Fast.Getter<object>(sm, "currentStoryScript"); _smStory = Fast.Getter<int>(sm, "story");
            _ssBlockSave = Fast.Getter<bool>(ss, "block_saving"); _ssBlockAll = Fast.Getter<bool>(ss, "block_all_buttons");
            _ok = _gmPs != null && _gmLoaded != null && _psCC != null && _psInVeh != null && _pmbGrounded != null && _psPMB != null && FbppApi.Ok;
            if (!_ok) { _log.Error("CAMPAIGN original API not found (GameManager/PlayerScript/FBPP): campaign bootstrap disabled"); return; }
            if (_host)
            {
                // Developer test hook (launcher mode only, never used by players): TS2MP_DEV_AUTOCONTINUE=1 in the host process
                // environment loads the host's own save once through the original MainMenu.Continue (normally clicked by Player 1).
                if (autoContinue && Environment.GetEnvironmentVariable("TS2MP_DEV_AUTOCONTINUE") == "1")
                { _devContinue = true; try { Environment.SetEnvironmentVariable("TS2MP_DEV_AUTOCONTINUE", null); } catch { } _log.Info("DEV_AUTOCONTINUE test hook armed (host loads its save once)"); }
                world.Host.P2PosHook = OnPositionReport;
                node.RemoteConnected += OnHostConnected;
                node.RemoteDisconnected += OnHostDisconnected;
                node.MessageReceived += OnHostMessage;
            }
            else
            {
                node.MessageReceived += OnClientMessage;
                node.RemoteConnected += OnClientConnected;
                node.RemoteDisconnected += OnClientDisconnected;
                world.ProposalResultHook6 = OnProposalResult;
                if (!InstallClientPatches()) { _ok = false; _log.Error("CAMPAIGN client patches failed: Player 2 cannot load a host campaign safely (join will time out)"); return; }
            }
            Instance = this;
            _log.Info("CAMPAIGN ready (" + (_host ? "host: campaign snapshots + Player-2 spawn store" : "client: host campaign -> sandbox, original Continue gated, spawn plan + position reports") + ")");
        }

        static double Now { get { return Time.realtimeSinceStartup; } }

        static bool InMainMenu()
        {
            var menu = SceneManager.GetSceneByName("MainMenuNew"); var main = SceneManager.GetSceneByName("MainScene");
            return menu.IsValid() && menu.isLoaded && !(main.IsValid() && main.isLoaded);
        }

        // =================================================================== host
        void EnsureStore()
        {
            var p2 = Player2Sync.Instance;
            if (p2 == null || !p2.HostBookReady) return;
            int profile = p2.HostProfile, hist = p2.HostHistoryId;
            if (profile == _storeProfile && hist == _storeHistory && _store != null) return;
            if (_writer != null) { if (_dirty) _writer.Queue(_rec.ToBytes()); _writer.Flush(2000); }
            _storeProfile = profile; _storeHistory = hist; _dirty = false;
            _store = new FleetStore(Path.Combine(_dataDir, "p2spawn-" + profile + ".dat"));
            _writer = new FleetWriter(_store);
            _rec = null; _recErr = null;
            string err1 = "missing", err2 = "missing";
            foreach (var path in new[] { _store.Path, _store.Path + ".bak" })
            {
                if (!File.Exists(path)) continue;
                P2SpawnRecord r; string err;
                try { if (P2SpawnRecord.TryParse(File.ReadAllBytes(path), out r, out err)) { _rec = r; break; } }
                catch (Exception ex) { err = ex.Message; }
                if (path == _store.Path) err1 = err; else err2 = err;
            }
            if (_rec == null && (File.Exists(_store.Path) || File.Exists(_store.Path + ".bak")))
            {
                _recErr = "main: " + err1 + ", backup: " + err2;
                string q = null; try { q = _store.Quarantine("invalid"); } catch { }
                _log.Warn("P2SPAWN record of profile " + profile + " unusable (" + _recErr + "): kept as " + q + "; Player 2 gets a first-join spawn");
            }
            else if (_rec != null && _rec.HistoryId != hist)
            {
                string q = null; try { q = _store.Quarantine("stale"); } catch { }
                _log.Warn("P2SPAWN record of profile " + profile + " belongs to another save history (record " + _rec.HistoryId + ", campaign " + hist + "): kept as " + q + "; Player 2 gets a first-join spawn");
                _recErr = null; var stale = _rec; _rec = null;
                _staleNote = stale;   // SpawnRules sees it as stale (no exact restore into another history)
            }
            _log.Info("P2SPAWN store " + _store.Path + " campaign profile=" + profile + " history=" + hist + " record=" + (_rec != null ? Regions.Name(_rec.Region) + " #" + _rec.Seq + " ctx=" + _rec.Context : "none"));
        }
        P2SpawnRecord _staleNote;

        int HostStory()
        {
            var gm = _world.GameManagerObject; var sm = gm != null && _gmStory != null ? _gmStory(gm) : null;
            if (Alive(sm) && _smStory != null) return _smStory(sm);
            return FbppApi.GetInt("Story", 0);
        }

        static bool Alive(object o) { var u = o as UnityEngine.Object; return u != null && u; }

        bool LocalFeet(out Vec3 feet, out float yaw)
        {
            feet = default(Vec3); yaw = 0f;
            var gm = _world.GameManagerObject; var ps = gm != null ? _gmPs(gm) : null;
            if (!Alive(ps)) return false;
            var cc = _psCC(ps) as CharacterController; var t = ((Component)ps).transform;
            Vector3 p = cc != null && cc.enabled ? new Vector3(t.position.x, cc.bounds.min.y, t.position.z) : t.position;
            feet = new Vec3(p.x, p.y, p.z);
            var pcb = _psPCB != null ? _psPCB(ps) : null;
            yaw = Alive(pcb) && _pcbYaw != null ? _pcbYaw(pcb) : t.eulerAngles.y;
            return true;
        }

        void OnHostConnected(string id) { SendSnapshot("connection " + id); }

        void OnHostMessage(NetPeer peer, byte type, NetPacketReader r)
        {
            if (type != (byte)MsgType.CampaignRequest) return;
            _requests++;
            if (Now - _lastRequestServed < 5.0) { _log.Warn("CAMPAIGN request ignored (rate limit 1 per 5 s)"); return; }
            _requestAt = Now + 0.5;   // after this frame's queued proposals (the exit position report) were applied in LateUpdate
        }

        void SendSnapshot(string why)
        {
            long t0 = Stopwatch.GetTimestamp();
            EnsureStore();
            var p2 = Player2Sync.Instance;
            if (p2 == null || !p2.HostBookReady || _store == null) { _log.Error("CAMPAIGN snapshot NOT sent (" + why + "): Player-2 record not bound (host readiness should have prevented this join)"); return; }
            Vec3 feet; float yaw; bool hasFeet = LocalFeet(out feet, out yaw);
            int hostRegion = _world.LocalRegion;
            string note;
            var plan = SpawnRules.Decide(_rec ?? _staleNote, _recErr, p2.HostHistoryId, hostRegion, hasFeet ? feet : (Vec3?)null, out note);
            CampaignData model; string err;
            if (!FbppApi.ReadModel(out model, out err)) { _log.Error("CAMPAIGN snapshot NOT sent (" + why + "): host FBPP model unreadable (" + err + ")"); return; }
            int[] removed;
            var snap = CampaignSnapshot.Build(model, p2.HostRecord, plan, p2.HostProfile, p2.HostHistoryId, (uint)new System.Random().Next(1, int.MaxValue), out removed);
            byte[] body = snap.Encode();
            _w.Reset(); _w.Put((byte)MsgType.CampaignSnapshot); _w.Put(body);   // raw body: no 64 KB length prefix (LiteNetLib fragments it)
            _node.Send(_w, DeliveryMethod.ReliableOrdered, 0);
            _snapshots++; _lastRequestServed = Now;
            _log.Info("CAMPAIGN snapshot #" + snap.Id.ToString("X8") + " sent (" + why + "): profile " + snap.Profile + " history " + snap.HistoryId + " story " + snap.HostStory
                + " keys " + snap.Data.Count + "/" + model.Count + " (not sent: P1-personal " + removed[(int)CampaignKeyClass.PlayerOwnP1] + ", host storage/cargo " + removed[(int)CampaignKeyClass.HostStorage]
                + ", position " + removed[(int)CampaignKeyClass.Position] + ", mod " + removed[(int)CampaignKeyClass.ModHost] + ", local prefs " + removed[(int)CampaignKeyClass.LocalPreference] + ")"
                + " P2 record items " + (p2.HostRecord != null ? p2.HostRecord.Items.Count : 0) + " | spawn " + plan + " - " + note
                + " | " + body.Length + " B, " + ((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency).ToString("0.0") + " ms (host region " + Regions.Name(hostRegion) + ")");
        }

        ProposalCode OnPositionReport(P2PosReport r)
        {
            EnsureStore();
            var p2 = Player2Sync.Instance;
            if (_store == null || p2 == null || p2.HostHistoryId == 0) return ProposalCode.NotAuthorized;
            uint seq = _rec != null ? _rec.Seq + 1 : 1;
            bool regionChanged = _rec == null || _rec.Region != r.Region || _rec.Context != r.Context;
            _rec = new P2SpawnRecord { HistoryId = p2.HostHistoryId, Region = r.Region, Pos = r.Pos, Yaw = r.Yaw, Context = r.Context, Story = HostStory(), Seq = seq, UtcTicks = DateTime.UtcNow.Ticks, Profile = p2.HostProfile };
            _staleNote = null; _recErr = null;
            _dirty = true; _reports++;
            if (regionChanged) _log.Info("P2SPAWN report #" + seq + " " + Regions.Name(r.Region) + " ctx=" + r.Context + " (story " + _rec.Story + ")");
            return ProposalCode.Accepted;
        }

        void OnHostDisconnected(string reason)
        {
            if (_dirty && _writer != null) { _writer.Queue(_rec.ToBytes()); _dirty = false; _nextWrite = Now + 5; }
            _log.Info("P2SPAWN Player 2 left (" + reason + "): last valid position " + (_rec != null ? Regions.Name(_rec.Region) + " #" + _rec.Seq + " ctx=" + _rec.Context : "none") + " written");
        }

        double _nextEnsure; bool _devContinue; double _devAt = -1;
        void HostUpdate(double now)
        {
            if (_devContinue && InMainMenu()) { if (_devAt < 0) _devAt = now + 6.0; else if (now >= _devAt) { _devContinue = false; ContinueOriginal(); } }
            if (now >= _nextEnsure) { _nextEnsure = now + 1.0; EnsureStore(); }   // profile / save history changes (new game, other save)
            if (_dirty && _writer != null && now >= _nextWrite) { _writer.Queue(_rec.ToBytes()); _dirty = false; _nextWrite = now + 5.0; }
            if (_requestAt > 0 && now >= _requestAt) { _requestAt = -1; if (_node.IsConnected) SendSnapshot("client back in the main menu"); }
        }

        // =================================================================== client
        bool InstallClientPatches()
        {
            if (_patched) return true;
            try
            {
                var mm = AccessTools.TypeByName("MainMenu"); var sg = AccessTools.TypeByName("SaveGame");
                var cont = mm != null ? AccessTools.Method(mm, "Continue", Type.EmptyTypes) : null;
                var lose = sg != null ? AccessTools.Method(sg, "LoseGame", Type.EmptyTypes) : null;
                if (cont == null || lose == null) { _log.Error("CAMPAIGN MainMenu.Continue / SaveGame.LoseGame not found"); return false; }
                _h = new Harmony("com.ts2mp.multiplayer.campaign");
                _h.Patch(cont, new HarmonyMethod(AccessTools.Method(typeof(CampaignBootstrap), "ContinuePrefix")));
                foreach (var name in new[] { "StartNewGame", "StartFreeMode", "ClickedNewGame" })
                {
                    var m = AccessTools.Method(mm, name, Type.EmptyTypes);
                    if (m != null) _h.Patch(m, new HarmonyMethod(AccessTools.Method(typeof(CampaignBootstrap), "NewGamePrefix")));
                    else _log.Warn("CAMPAIGN MainMenu." + name + " not found (not gated)");
                }
                _h.Patch(lose, new HarmonyMethod(AccessTools.Method(typeof(CampaignBootstrap), "LoseGamePrefix")));
                _patched = true;
                _log.Info("CAMPAIGN client gates installed: MainMenu.Continue (only after the host campaign was written), new game blocked, exit report at SaveGame.LoseGame");
                return true;
            }
            catch (Exception ex) { _log.Error("CAMPAIGN client patch exception", ex); return false; }
        }

        static bool ContinuePrefix()
        {
            if (!SaveGuard.Armed || _allowContinue) return true;
            NoteBlocked("MainMenu.Continue (no host campaign written in this connection: join with F8)");
            return false;
        }

        static bool NewGamePrefix(MethodBase __originalMethod)
        {
            if (!SaveGuard.Armed) return true;
            NoteBlocked("MainMenu." + __originalMethod.Name + " (Player 2 never starts a campaign of its own)");
            return false;
        }

        static void NoteBlocked(string what)
        {
            var i = Instance;
            if (i != null && _blockedLogged != what) { _blockedLogged = what; i._log.Info("CAMPAIGN blocked " + what); }
        }

        static void LoseGamePrefix()
        {
            var i = Instance;
            if (i == null || i._host) return;
            try { i.Report(Now, true, "exit to the main menu / quit"); } catch (Exception ex) { i._log.Warn("P2SPAWN exit report failed: " + ex.Message); }
        }

        void OnClientConnected(string id) { _applied = false; _allowContinue = false; _menuSeen = false; }
        void OnClientDisconnected(string reason) { _allowContinue = false; }

        void OnClientMessage(NetPeer peer, byte type, NetPacketReader r)
        {
            if (type != (byte)MsgType.CampaignSnapshot) return;
            CampaignSnapshot snap; string err;
            byte[] body = r.GetRemainingBytes();
            if (!CampaignSnapshot.TryDecode(new NetDataReader(body), out snap, out err)) { _log.Error("CAMPAIGN snapshot malformed (" + err + "): ignored; the join times out safely"); return; }
            if (!InMainMenu()) { _log.Info("CAMPAIGN snapshot #" + snap.Id.ToString("X8") + " ignored: world already loaded (applied only in the main menu, before Continue)"); return; }
            Apply(snap);
        }

        void Apply(CampaignSnapshot snap)
        {
            long t0 = Stopwatch.GetTimestamp();
            // Never write anything unless this process is isolated: the overwrite targets FBPP's active file.
            if (!SaveGuard.Armed || !SaveGuard.RuntimeCheck()) { _log.Error("CAMPAIGN snapshot refused: save isolation not armed/verified (nothing written)"); return; }
            snap.ApplyLocalPreferences(SaveGuard.LocalPreferences);
            string err;
            if (!FbppApi.Overwrite(snap.Data, out err)) { _log.Error("CAMPAIGN sandbox write failed (" + err + "): Continue stays blocked"); return; }
            int loc = FbppApi.GetInt("CurrentLocation", int.MinValue), story = FbppApi.GetInt("Story", 0);
            if (loc != snap.Plan.Region || story != snap.HostStory || !SaveGuard.RuntimeCheck())
            { _log.Error("CAMPAIGN verification failed after the sandbox write (CurrentLocation " + loc + "/" + snap.Plan.Region + ", Story " + story + "/" + snap.HostStory + "): Continue stays blocked"); return; }
            _applied = true; _plan = snap.Plan; _placePending = true; _placeRegion = Regions.None; _hasLast = false;
            _allowContinue = true;
            _log.Info("CAMPAIGN host campaign #" + snap.Id.ToString("X8") + " written to the sandbox (profile " + snap.Profile + ", history " + snap.HistoryId + ", story " + snap.HostStory
                + ", " + snap.Data.Count + " keys, " + ((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency).ToString("0.0") + " ms) and verified; spawn plan " + snap.Plan);
            if (_autoContinue && !_autoDone) { _autoDone = true; ContinueOriginal(); }
        }

        // Developer launcher mode (no F8 flow) and the menu-return rejoin: the original Continue.
        internal void ContinueOriginal()
        {
            var mmT = AccessTools.TypeByName("MainMenu");
            var mm = mmT != null ? UnityEngine.Object.FindObjectOfType(mmT) : null;   // once per join, main menu only
            var cont = mmT != null ? AccessTools.Method(mmT, "Continue") : null;
            if (mm == null || cont == null) { _log.Warn("CAMPAIGN MainMenu.Continue not available: continue manually"); return; }
            _log.Info("CAMPAIGN loading the host campaign through the original MainMenu.Continue()");
            try { cont.Invoke(mm, null); } catch (Exception ex) { _log.Error("CAMPAIGN MainMenu.Continue failed", ex); }
        }

        bool OnProposalResult(uint id, ProposalCode code)
        {
            if (id != _lastPosId || id == 0) return false;
            if (code != ProposalCode.Accepted && code != ProposalCode.Unchanged) { _rejected++; _log.Warn("P2SPAWN position report #" + id + " refused by the host: " + code); }
            return true;
        }

        void ClientUpdate(double now)
        {
            if (InMainMenu())
            {
                if (!_menuSeen) { _menuSeen = true; _menuAt = now; }
                // Back in the main menu while connected (not the initial join): a fresh snapshot so Continue rejoins through the host.
                if (_applied && _placeRegion != Regions.None && _node.IsConnected && now - _menuAt > 1.0)
                {
                    _applied = false; _allowContinue = false; _placeRegion = Regions.None;
                    _w.Reset(); _w.Put((byte)MsgType.CampaignRequest); _node.Send(_w, DeliveryMethod.ReliableOrdered, 0);
                    _log.Info("CAMPAIGN back in the main menu while connected: fresh host campaign requested (Continue gated until it is written)");
                }
                return;
            }
            _menuSeen = false;
            var gm = _world.GameManagerObject; int region = _world.LocalRegion;
            if (gm == null || region == Regions.None || !_gmLoaded(gm)) { if (region == Regions.None) _placeRegion = Regions.None; return; }
            if (_placeRegion != region) { _placeRegion = region; _regionAt = now; }
            if (_placePending)
            {
                if (now - _regionAt < 0.6) return;   // GameManager.ContinueButtonActivate + VehicleSync arrival first
                _placePending = false;
                if (region == _plan.Region) PlaceSpawn(gm); else _log.Warn("P2SPAWN plan for " + Regions.Name(_plan.Region) + " but the original loaded " + Regions.Name(region) + ": original position kept");
                _nextReport = now + 2.0;
                return;
            }
            if (now - _regionAt < 3.0 || now < _nextReport) return;
            _nextReport = now + 1.0;
            Report(now, false, null);
        }

        void PlaceSpawn(UnityEngine.Object gm)
        {
            var vs = Vehicles;
            var ps = _gmPs(gm);
            if (vs == null || !vs.SpawnReady || !Alive(ps)) { _log.Warn("P2SPAWN placement unavailable (vehicle sync not ready): original position kept"); return; }
            var t = ((Component)ps).transform; var cc = _psCC(ps) as CharacterController;
            Vector3 here = cc != null && cc.enabled ? new Vector3(t.position.x, cc.bounds.min.y, t.position.z) : t.position;
            Vector3 target = _plan.HasTarget ? new Vector3(_plan.Target.X, _plan.Target.Y, _plan.Target.Z) : here;
            var cand = new List<KeyValuePair<string, Vector3>>();
            Vector3 rf; bool rin; float rsp; bool remote = vs.TryGetRemoteHere(out rf, out rin, out rsp);
            switch (_plan.Mode)
            {
                case SpawnMode.Restore: cand.Add(new KeyValuePair<string, Vector3>("last valid position", target)); AddRing(cand, "beside the last position", target); break;
                case SpawnMode.RestoreNear: AddRing(cand, "beside the last position", target); break;
                case SpawnMode.NearHost: AddRing(cand, "near Player 1", remote && !rin ? rf : target); break;
                case SpawnMode.SafePoint:
                {
                    var lp = vs.SpawnLeavePoint(target);
                    if (lp.HasValue) { cand.Add(new KeyValuePair<string, Vector3>("nearest original leave point", lp.Value)); AddRing(cand, "beside the nearest leave point", lp.Value); }
                    cand.Add(new KeyValuePair<string, Vector3>("original spawn", here)); AddRing(cand, "beside the original spawn", here);
                    break;
                }
                default: cand.Add(new KeyValuePair<string, Vector3>("original spawn", here)); AddRing(cand, "beside the original spawn", here); break;
            }
            long t0 = Stopwatch.GetTimestamp();
            for (int i = 0; i < cand.Count; i++)
            {
                Vector3 feet;
                if (!vs.SpawnSpot(cand[i].Value, cand[i].Value, out feet)) continue;
                bool moved = (feet - here).sqrMagnitude > 0.04f;
                if (moved) vs.SpawnPlace(feet);
                if ((_plan.Mode == SpawnMode.Restore || _plan.Mode == SpawnMode.RestoreNear) && _pcbSetYaw != null)
                {
                    var pcb = _psPCB(ps); if (Alive(pcb)) _pcbSetYaw(pcb, _plan.Yaw);
                }
                var ic = System.Globalization.CultureInfo.InvariantCulture;
                _log.Info("P2SPAWN " + _plan.Mode + " (" + _plan.Reason + ") in " + Regions.Name(_plan.Region) + ": " + (moved ? "placed at " : "kept at ") + cand[i].Key + " ("
                    + feet.x.ToString("0.0", ic) + " " + feet.y.ToString("0.0", ic) + " " + feet.z.ToString("0.0", ic) + ") after " + (i + 1) + " candidate(s), " + ((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency).ToString("0.00") + " ms");
                return;
            }
            _log.Warn("P2SPAWN " + _plan.Mode + " in " + Regions.Name(_plan.Region) + ": no checked position among " + cand.Count + " candidates; original position kept (fall guard active)");
        }

        static void AddRing(List<KeyValuePair<string, Vector3>> cand, string label, Vector3 c)
        {
            var ring = new List<Vec3>(24);
            SpawnPlanner.Ring(new Vec3(c.x, c.y, c.z), 180f, ring);
            foreach (var v in ring) cand.Add(new KeyValuePair<string, Vector3>(label, new Vector3(v.X, v.Y, v.Z)));
        }

        SpawnContext Context(object gm, object ps)
        {
            var c = SpawnContext.None;
            if (_psInVeh(ps)) c |= SpawnContext.InVehicle;
            if (_psHiding != null && _psHiding(ps)) c |= SpawnContext.Hiding;
            if (_psChase != null && _psChase(ps)) c |= SpawnContext.PoliceChase;
            if (_psCaught != null && _psCaught(ps)) c |= SpawnContext.Caught;
            var sm = _gmStory != null ? _gmStory(gm) : null;
            var script = Alive(sm) && _smScript != null ? _smScript(sm) : null;
            if (Alive(script))
            {
                if (_ssBlockSave != null && _ssBlockSave(script)) c |= SpawnContext.BlockSaving;
                if (_ssBlockAll != null && _ssBlockAll(script)) c |= SpawnContext.BlockTravel;
            }
            return c;
        }

        // A position is valid when the region is confirmed, the player exists, stands on the ground (or sits in a vehicle -
        // flagged) and is not in the lockpicking minigame. Sent on a real change (0.75 m / 15 deg / context) at most every 3 s.
        void Report(double now, bool force, string why)
        {
            if (_host || !_node.IsConnected || !_applied) return;
            var gm = _world.GameManagerObject; int region = _world.LocalRegion;
            if (gm == null || region == Regions.None) return;
            var ps = _gmPs(gm);
            if (!Alive(ps)) return;
            var pmb = _psPMB(ps);
            bool inVeh = _psInVeh(ps);
            if (!inVeh && (!Alive(pmb) || !_pmbGrounded(pmb))) return;
            if (_psLockpicking != null && _psLockpicking(ps)) return;
            Vec3 feet; float yaw;
            if (!LocalFeet(out feet, out yaw) || !SpawnValidation.PositionOk(feet)) return;
            var rep = new P2PosReport { Region = region, Pos = feet, Yaw = yaw, Context = Context(gm, ps) };
            if (!force && _hasLast)
            {
                bool changed = rep.Region != _last.Region || rep.Context != _last.Context || Vec3.Distance(rep.Pos, _last.Pos) > 0.75f || Mathf.Abs(Mathf.DeltaAngle(rep.Yaw, _last.Yaw)) > 15f;
                if (!changed || now - _lastSentAt < 3.0) return;
            }
            _last = rep; _hasLast = true; _lastSentAt = now; _sent++;
            _lastPosId = _world.SendProposal(new StateEntry { Scope = StateScope.Player, ScopeArg = 2, Key = WorldKeys.P2Pos, Value = rep.ToBytes() });
            if (force) _log.Info("P2SPAWN final position report (" + why + "): " + Regions.Name(region) + " ctx=" + rep.Context);
        }

        // =================================================================== frame
        public void Update()
        {
            if (!_ok) return;
            double now = Now;
            if (_host) HostUpdate(now); else ClientUpdate(now);
        }

        public string Stats
        {
            get
            {
                return _host ? "CAMPAIGN_STATS host snapshots=" + _snapshots + " requests=" + _requests + " spawnReports=" + _reports + " writes=" + (_writer != null ? _writer.Writes : 0) + " writeFailures=" + (_writer != null ? _writer.Failures : 0)
                             : "CAMPAIGN_STATS client applied=" + _applied + " reports=" + _sent + " refused=" + _rejected;
            }
        }

        public void Dispose()
        {
            if (_host)
            {
                _node.RemoteConnected -= OnHostConnected; _node.RemoteDisconnected -= OnHostDisconnected; _node.MessageReceived -= OnHostMessage;
                if (_world.Host != null && _world.Host.P2PosHook == (Func<P2PosReport, ProposalCode>)OnPositionReport) _world.Host.P2PosHook = null;
                if (_writer != null) { if (_dirty && _rec != null) _writer.Queue(_rec.ToBytes()); _dirty = false; if (!_writer.Flush(3000)) _log.Warn("P2SPAWN final write did not finish within 3 s"); }
            }
            else
            {
                _node.MessageReceived -= OnClientMessage; _node.RemoteConnected -= OnClientConnected; _node.RemoteDisconnected -= OnClientDisconnected;
                if (_world.ProposalResultHook6 == (Func<uint, ProposalCode, bool>)OnProposalResult) _world.ProposalResultHook6 = null;
                _allowContinue = false;   // the gate stays installed: Player 2 continues only after the next join wrote a campaign
            }
            if (Instance == this) Instance = null;
        }
    }

    // FBPP (FileBasedPrefs.dll) access used by the bootstrap. Verified IL: FBPP._latestData (static FBPPFileModel with
    // StringItem[]/IntItem[]/FloatItem[]/BoolItem[] {Key, Value}); OverwriteLocalSaveFile(string json) = WriteToSaveFile(json)
    // (guarded: sandbox only while armed) + _latestData = null + GetSaveFile() (reload from that file); GetInt(string,int).
    internal static class FbppApi
    {
        static readonly Type _fbpp = AccessTools.TypeByName("FBPP"), _model = AccessTools.TypeByName("FBPPFileModel"), _json = AccessTools.TypeByName("UnityEngine.JsonUtility");
        static readonly FieldInfo _latest = _fbpp != null ? AccessTools.Field(_fbpp, "_latestData") : null;
        static readonly MethodInfo _overwrite = _fbpp != null ? AccessTools.Method(_fbpp, "OverwriteLocalSaveFile", new[] { typeof(string) }) : null;
        static readonly MethodInfo _getInt = _fbpp != null ? AccessTools.Method(_fbpp, "GetInt", new[] { typeof(string), typeof(int) }) : null;
        static readonly MethodInfo _toJson = _json != null ? AccessTools.Method(_json, "ToJson", new[] { typeof(object) }) : null;
        static readonly string[] Arrays = { "IntData", "FloatData", "StringData", "BoolData" };

        public static bool Ok { get { return _latest != null && _overwrite != null && _getInt != null && _toJson != null && _model != null; } }

        public static int GetInt(string key, int dflt)
        {
            try { return (int)_getInt.Invoke(null, new object[] { key, dflt }); } catch { return dflt; }
        }

        public static bool ReadModel(out CampaignData data, out string err)
        {
            data = new CampaignData(); err = null;
            try
            {
                var m = _latest.GetValue(null);
                if (m == null) { err = "no FBPP model in memory"; return false; }
                foreach (var name in Arrays)
                {
                    var arr = AccessTools.Field(_model, name).GetValue(m) as Array;
                    if (arr == null) continue;
                    foreach (var it in arr)
                    {
                        if (it == null) continue;
                        var t = it.GetType();
                        var k = AccessTools.Field(t, "Key").GetValue(it) as string; var v = AccessTools.Field(t, "Value").GetValue(it);
                        if (string.IsNullOrEmpty(k)) continue;
                        if (v is int) data.Ints[k] = (int)v; else if (v is float) data.Floats[k] = (float)v; else if (v is bool) data.Bools[k] = (bool)v; else data.Strings[k] = v as string ?? "";
                    }
                }
                return true;
            }
            catch (Exception ex) { err = ex.Message; return false; }
        }

        // Builds an FBPPFileModel with the original item types, serialises it with JsonUtility (as FBPP itself does) and lets
        // the original OverwriteLocalSaveFile write + reload it.
        public static bool Overwrite(CampaignData d, out string err)
        {
            err = null;
            try
            {
                var m = Activator.CreateInstance(_model);
                Fill(m, "IntData", d.Ints); Fill(m, "FloatData", d.Floats); Fill(m, "StringData", d.Strings); Fill(m, "BoolData", d.Bools);
                string json = (string)_toJson.Invoke(null, new object[] { m });
                _overwrite.Invoke(null, new object[] { json });
                return true;
            }
            catch (Exception ex) { err = (ex.InnerException ?? ex).Message; return false; }
        }

        static void Fill<T>(object model, string field, Dictionary<string, T> values)
        {
            var f = AccessTools.Field(_model, field);
            var itemT = f.FieldType.GetElementType();
            var arr = Array.CreateInstance(itemT, values.Count);
            // verified: the item types have only the constructor (string key, T value) (e.g. IntItem..ctor(String,Int32))
            int i = 0;
            foreach (var kv in values) arr.SetValue(Activator.CreateInstance(itemT, new object[] { kv.Key, kv.Value }), i++);
            f.SetValue(model, arr);
        }
    }
}

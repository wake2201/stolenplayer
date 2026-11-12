// M2 movement replication. Main-thread only (driven by NetRunner.Update after SessionNode.Tick, which dispatches
// LiteNetLib events on the same thread). Reads the original local player via reflection; never writes to it.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using TS2MP.Shared;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TS2MP.Plugin
{
    public sealed class SyncSettings
    {
        public int SendRateHz = 20;
        public int InterpolationDelayMs = 100;
        public float YawOffset = 0f;
        public bool ShowNameTag = true;
        public float FallbackAfterSeconds = 15f;
    }

    internal sealed class PlayerSync : IDisposable
    {
        readonly SessionNode _node;
        readonly InstanceLog _log;
        readonly SyncSettings _cfg;
        readonly byte _localSlot, _remoteSlot;
        readonly Stopwatch _clock = Stopwatch.StartNew();
        readonly NetDataWriter _w = new NetDataWriter();

        // local player (read-only access to original game objects)
        Type _psType, _pmbType;
        Component _ps, _pmb;
        CharacterController _cc;
        FieldInfo _fCC, _fPMB, _fCam, _fInVehicle, _fHiding, _fGrounded, _fMoving, _fRunning, _fCrouching;
        float _walkRef = 2.5f, _runRef = 5f, _crouchRef = 1.5f;
        // W3C/W3D local presentation state (compiled accessors, read at the send rate only)
        Func<object, bool> _torch, _carrying; Func<object, int> _held, _pickItemId, _itemAmount; Func<object, object> _carryItem, _psItems2; Type _pickT;
        static int _attacks;                    // W3.1: original attacks of the local player (PlayerScript.Attack postfix)
        Harmony _h;
        Transform _carryT; ushort _carryId;
        RemoteGear _gear;
        float _nextProbe;
        bool _localPresent;

        string _sceneKey = "";
        int _sceneHash;
        bool _sceneInfoDirty = true;

        uint _seq;
        double _nextSend;
        long _sent;

        // remote
        readonly SnapshotBuffer _buffer = new SnapshotBuffer();
        bool _hasLast; uint _lastSeq; int _lastRemoteScene;
        double _lastRecvAt = -100;
        MoveFlags _remoteFlags;
        int _remoteSceneHash;
        string _remoteSceneKey = "?";
        bool _remotePresent;
        RemoteAvatar _avatar;
        RemoteView _view = RemoteView.NotConnected;
        double _visibleConditionsSince = -1;
        int _seatFrame = -10;   // M4.2.2: last Time.frameCount VehicleSync posed the avatar in a driver seat
        long _accepted, _stale, _invalid, _wrongSlot;
        double _nextStats;

        public PlayerSync(SessionNode node, InstanceLog log, SyncSettings cfg)
        {
            _node = node; _log = log; _cfg = cfg;
            _localSlot = node.Role == Role.Host ? (byte)1 : (byte)2;
            _remoteSlot = node.Role == Role.Host ? (byte)2 : (byte)1;
            _buffer.InterpolationDelay = cfg.InterpolationDelayMs / 1000.0;
            node.MessageReceived += OnMessage;
            node.RemoteConnected += OnRemoteConnected;
            node.RemoteDisconnected += OnRemoteDisconnected;
            SceneManager.sceneLoaded += OnSceneLoaded;
            SceneManager.sceneUnloaded += OnSceneUnloaded;
            ResolveTypes();
            RecomputeSceneKey();
            log.Info("SYNC_INIT slot=" + _localSlot + " sendRate=" + cfg.SendRateHz + "Hz interpDelay=" + cfg.InterpolationDelayMs + "ms snapshot=" + PlayerStateCodec.WireSize + "B");
        }

        double Now { get { return _clock.Elapsed.TotalSeconds; } }

        void ResolveTypes()
        {
            _psType = AccessTools.TypeByName("PlayerScript");
            _pmbType = AccessTools.TypeByName("PlayerMotorBehavior");
            if (_psType == null || _pmbType == null) { _log.Error("SYNC PlayerScript/PlayerMotorBehavior type not found; local state cannot be read"); return; }
            _fCC = AccessTools.Field(_psType, "CC");
            _fPMB = AccessTools.Field(_psType, "PMB");
            _fCam = AccessTools.Field(_psType, "MainCam");
            _fInVehicle = AccessTools.Field(_psType, "is_in_vehicle");
            _fHiding = AccessTools.Field(_psType, "isHiding");
            _fGrounded = AccessTools.Field(_pmbType, "isGrounded");
            _fMoving = AccessTools.Field(_pmbType, "isMoving");
            _fRunning = AccessTools.Field(_pmbType, "isRunning");
            _fCrouching = AccessTools.Field(_pmbType, "isCrouching");
            _torch = Fast.Getter<bool>(_psType, "torch"); _carrying = Fast.Getter<bool>(_psType, "isCarrying"); _held = Fast.Getter<int>(_psType, "currentItem");
            _carryItem = Fast.Getter<object>(_psType, "CarryItem"); _pickT = AccessTools.TypeByName("Pickupable"); _pickItemId = Fast.Getter<int>(_pickT, "item_ID");
            if (_torch == null || _carrying == null || _held == null || _carryItem == null || _pickItemId == null) _log.Warn("SYNC gear fields not found (held tool / carry / flashlight presentation degraded)");
            _psItems2 = Fast.Getter<object>(_psType, "item_IDs2"); _itemAmount = Fast.Getter<int>(AccessTools.TypeByName("ItemID"), "item_amount");
            try
            {
                var atk = AccessTools.Method(_psType, "Attack", Type.EmptyTypes);   // IEnumerator factory, called once per original attack (PlayerScript.Update IL_3994)
                if (atk != null) { _h = new Harmony("com.ts2mp.multiplayer.gear"); _h.Patch(atk, null, new HarmonyMethod(AccessTools.Method(typeof(PlayerSync), "AttackPostfix"))); }
                else _log.Warn("SYNC PlayerScript.Attack not found: remote swing not shown");
            }
            catch (Exception ex) { _log.Error("SYNC attack hook failed (remote swing not shown)", ex); }
            try { _gear = new RemoteGear(_log); } catch (Exception ex) { _log.Error("AVATAR_GEAR init failed (held/carried objects not shown)", ex); _gear = null; }
            var missing = new[] { "CC", "PMB", "MainCam", "is_in_vehicle", "isHiding", "isGrounded", "isMoving", "isRunning", "isCrouching" }
                .Where((n, i) => new[] { _fCC, _fPMB, _fCam, _fInVehicle, _fHiding, _fGrounded, _fMoving, _fRunning, _fCrouching }[i] == null).ToArray();
            if (missing.Length > 0) _log.Warn("SYNC fields not found (state degraded): " + string.Join(",", missing));
        }

        // ---------------- per frame ----------------
        public void Update()
        {
            ProbeLocal();
            if (_node.IsConnected)
            {
                if (_sceneInfoDirty) SendSceneInfo();
                double now = Now;
                double interval = _localPresent ? 1.0 / Math.Max(1, _cfg.SendRateHz) : 0.5;
                if (now >= _nextSend)
                {
                    _nextSend = now + interval;
                    SendSnapshot();
                }
            }
            UpdateRemote();
            if (Now >= _nextStats)
            {
                _nextStats = Now + 30;
                if (_node.IsConnected)
                    _log.Info("SYNC_STATS sent=" + _sent + " accepted=" + _accepted + " stale=" + (_stale + _buffer.StaleDropped) + " invalid=" + _invalid + " wrongSlot=" + _wrongSlot
                        + " buffer=" + _buffer.Count + " view=" + _view + " clip=" + (_avatar != null ? _avatar.CurrentClip : "-") + " gear " + (_gear != null ? _gear.Status : "off") + " localScene=" + _sceneKey + " remoteScene=" + _remoteSceneKey);
            }
        }

        void ProbeLocal()
        {
            bool alive = _ps != null && _ps.gameObject.activeInHierarchy;
            if (!alive && Time.unscaledTime >= _nextProbe && _psType != null)
            {
                _nextProbe = Time.unscaledTime + 1f;
                _ps = UnityEngine.Object.FindObjectOfType(_psType) as Component;
                if (_ps != null)
                {
                    _cc = _fCC != null ? _fCC.GetValue(_ps) as CharacterController : null;
                    _pmb = _fPMB != null ? _fPMB.GetValue(_ps) as Component : null;
                    ReadRefSpeeds();
                    _log.Info("LOCAL_PLAYER found '" + _ps.name + "' scene=" + _ps.gameObject.scene.name + " cc=" + (_cc != null) + " pmb=" + (_pmb != null)
                        + " speeds walk/run/crouch=" + _walkRef + "/" + _runRef + "/" + _crouchRef);
                    alive = _ps.gameObject.activeInHierarchy;
                }
            }
            if (alive != _localPresent)
            {
                _localPresent = alive;
                _sceneInfoDirty = true;
                _log.Info("LOCAL_PLAYER " + (alive ? "present" : "absent"));
            }
        }

        void ReadRefSpeeds()
        {
            if (_pmb == null) return;
            _walkRef = ReadFloat(_pmb, "moveSpeed", _walkRef);
            _runRef = ReadFloat(_pmb, "runSpeed", _runRef);
            _crouchRef = ReadFloat(_pmb, "crouchSpeed", _crouchRef);
            if (_walkRef < 0.5f) _walkRef = 2.5f;
            if (_runRef <= _walkRef) _runRef = _walkRef * 2f;
            if (_crouchRef < 0.3f) _crouchRef = 1.5f;
        }

        static float ReadFloat(object o, string field, float def)
        {
            var f = AccessTools.Field(o.GetType(), field);
            if (f == null || f.FieldType != typeof(float)) return def;
            return (float)f.GetValue(o);
        }

        static bool ReadBool(FieldInfo f, object o) { return f != null && o != null && (bool)f.GetValue(o); }

        void RecomputeSceneKey()
        {
            var names = new List<string>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var s = SceneManager.GetSceneAt(i);
                if (s.isLoaded) names.Add(s.name);
            }
            names.Sort(StringComparer.Ordinal);
            string key = string.Join("+", names.ToArray());
            if (key == _sceneKey) return;
            _sceneKey = key;
            _sceneHash = SceneKeys.Hash(key);
            _sceneInfoDirty = true;
            _log.Info("SCENE local=" + key + " hash=" + _sceneHash.ToString("X8"));
        }

        void OnSceneLoaded(Scene s, LoadSceneMode m) { RecomputeSceneKey(); _nextProbe = 0; }
        void OnSceneUnloaded(Scene s) { RecomputeSceneKey(); }

        // ---------------- sending ----------------
        void SendSceneInfo()
        {
            if (_sceneKey.Length == 0) RecomputeSceneKey();
            _w.Reset();
            new SceneInfo { Slot = _localSlot, SceneHash = _sceneHash, SceneKey = _sceneKey, Present = _localPresent }.Write(_w);
            _node.Send(_w, DeliveryMethod.ReliableOrdered, SessionNode.ChannelReliable);
            _sceneInfoDirty = false;
        }

        void SendSnapshot()
        {
            var s = new PlayerSnapshot { Slot = _localSlot, Seq = ++_seq, TimeMs = (uint)_clock.ElapsedMilliseconds, SceneHash = _sceneHash };
            if (_localPresent)
            {
                var flags = MoveFlags.Present;
                Vector3 feet;
                if (_cc != null && _cc.enabled)
                {
                    var b = _cc.bounds;
                    feet = new Vector3(b.center.x, b.min.y, b.center.z);
                }
                else feet = _ps.transform.position;
                Camera cam = _fCam != null ? _fCam.GetValue(_ps) as Camera : null;
                float yaw = cam != null ? cam.transform.eulerAngles.y : _ps.transform.eulerAngles.y;
                if (ReadBool(_fGrounded, _pmb)) flags |= MoveFlags.Grounded;
                if (ReadBool(_fMoving, _pmb)) flags |= MoveFlags.Moving;
                if (ReadBool(_fRunning, _pmb)) flags |= MoveFlags.Running;
                if (ReadBool(_fCrouching, _pmb)) flags |= MoveFlags.Crouching;
                if (ReadBool(_fInVehicle, _ps) || LocalSeated) flags |= MoveFlags.InVehicle;
                if (ReadBool(_fHiding, _ps)) flags |= MoveFlags.Hidden;
                s.Pos = new Vec3(feet.x, feet.y, feet.z);
                s.Yaw = yaw;
                s.Flags = flags;
                ReadGear(ref s, cam);
            }
            _w.Reset();
            PlayerStateCodec.Write(_w, s);
            _node.Send(_w, DeliveryMethod.Sequenced, SessionNode.ChannelState);
            _sent++;
        }

        // W3C/W3D: flashlight (PlayerScript.torch), held tool (currentItem), heavy carry (isCarrying + CarryItem's
        // Pickupable.item_ID, resolved once per carried object), camera pitch.
        void ReadGear(ref PlayerSnapshot s, Camera cam)
        {
            try
            {
                if (cam != null) s.Pitch = PlayerStateCodec.QuantizePitch(cam.transform.eulerAngles.x);
                var g = GearFlags.None;
                if (_torch != null && _torch(_ps)) g |= GearFlags.Torch;
                var items = _psItems2 != null ? _psItems2(_ps) as Array : null;   // item_IDs2[3] = flashlight (ownership, PlayerScript.Update IL_37c0)
                if (items != null && items.Length > 3 && items.GetValue(3) != null && _itemAmount(items.GetValue(3)) > 0) g |= GearFlags.HasLight;
                s.Swing = (byte)(_attacks & 0x0F);
                if (_carrying != null && _carrying(_ps))
                {
                    g |= GearFlags.Carrying;
                    var t = _carryItem != null ? _carryItem(_ps) as Transform : null;
                    if (!ReferenceEquals(t, _carryT))
                    {
                        _carryT = t; _carryId = 0;
                        var p = t != null ? t.GetComponent(_pickT) : null;
                        if (p != null) { int id = _pickItemId(p); if (id >= 0 && id < ushort.MaxValue) _carryId = (ushort)(id + 1); }
                    }
                    s.CarryItem = _carryId;
                }
                s.Gear = g;
                int held = _held != null ? _held(_ps) : 0;
                s.Held = held >= 0 && held <= 255 ? (byte)held : (byte)0;
            }
            catch { }
        }

        static void AttackPostfix() { unchecked { _attacks++; } }

        // W3.1: procedural arm pose of the remote avatar (after the legacy Animation evaluated this frame).
        public void LateUpdate()
        {
            if (_gear == null || _avatar == null || !_avatar.Visible) return;
            try { _gear.LateFrame(Time.deltaTime); } catch (Exception ex) { _log.Error("AVATAR_GEAR arm pose failed (disabled)", ex); _gear = null; }
        }

        // ---------------- receiving ----------------
        void OnMessage(NetPeer peer, byte type, NetPacketReader r)
        {
            if (type == (byte)MsgType.PlayerState)
            {
                PlayerSnapshot s;
                if (!PlayerStateCodec.TryRead(r, out s)) { _invalid++; return; }
                var verdict = SnapshotValidation.Check(s, _remoteSlot, _hasLast, _lastSeq);
                if (verdict == SnapshotVerdict.Stale) { _stale++; return; }
                if (verdict == SnapshotVerdict.WrongSlot) { if (_wrongSlot++ == 0) _log.Warn("SNAPSHOT wrong slot " + s.Slot + " (expected " + _remoteSlot + ")"); return; }
                if (verdict == SnapshotVerdict.Invalid) { if (_invalid++ == 0) _log.Warn("SNAPSHOT invalid values pos=" + s.Pos + " yaw=" + s.Yaw); return; }
                _hasLast = true; _lastSeq = s.Seq; _accepted++;
                _remoteFlags = s.Flags;
                _remoteSceneHash = s.SceneHash;
                if (s.SceneHash != _lastRemoteScene) { _buffer.Clear(); _lastRemoteScene = s.SceneHash; }
                if (s.Has(MoveFlags.Present)) { _buffer.Add(s, Now); _lastRecvAt = Now; }
                if (_node.Role == Role.Host)
                {
                    // Host authority: only validated states are relayed (no other clients exist while MaxClients = 1).
                    _w.Reset(); PlayerStateCodec.Write(_w, s);
                    _node.SendToOthers(_w, DeliveryMethod.Sequenced, SessionNode.ChannelState, peer);
                }
            }
            else if (type == (byte)MsgType.SceneInfo)
            {
                SceneInfo si;
                if (!SceneInfo.TryRead(r, out si) || si.Slot != _remoteSlot) { _invalid++; return; }
                _remoteSceneKey = si.SceneKey;
                _remotePresent = si.Present;
                _log.Info("SCENE remote=" + si.SceneKey + " hash=" + si.SceneHash.ToString("X8") + " present=" + si.Present
                    + (si.SceneHash == _sceneHash ? " (same as local)" : " (local=" + _sceneKey + ")"));
            }
            else if (type < (byte)MsgType.StateSnapshot) _log.Warn("SYNC unknown message type " + type);   // 4+ handled by WorldSync
        }

        void OnRemoteConnected(string id)
        {
            _hasLast = false; _buffer.Clear(); _remotePresent = false; _remoteSceneKey = "?";
            _sceneInfoDirty = true;
            _nextSend = 0;
        }

        void OnRemoteDisconnected(string reason)
        {
            _hasLast = false; _buffer.Clear(); _remotePresent = false; _remoteSceneKey = "?";
            DestroyAvatar("remote disconnected (" + reason + ")");
        }

        // ---------------- remote avatar ----------------
        void UpdateRemote()
        {
            double now = Now;
            bool fresh = _buffer.HasData && now - _lastRecvAt < 3.0;
            var view = RemoteVisibility.Decide(_node.IsConnected, _localPresent, _remotePresent, _sceneHash, _remoteSceneHash, _remoteFlags, fresh);
            if (view != _view)
            {
                _log.Info("REMOTE_VIEW " + _view + " -> " + view + (view == RemoteView.OtherScene ? " (local=" + _sceneKey + " remote=" + _remoteSceneKey + ")" : ""));
                _view = view;
            }

            if (view != RemoteView.Visible)
            {
                _visibleConditionsSince = -1;
                // M4.2.2: in a car, VehicleSync (runs after this) poses the avatar in the driver seat every frame it finds
                // the car presented here; without a pose in the previous frame the avatar is hidden (as before M4.2.2).
                if (view == RemoteView.InVehicle && _avatar != null && _seatFrame >= Time.frameCount - 1) return;
                if (_avatar != null) _avatar.SetVisible(false);
                if (view == RemoteView.NotConnected) DestroyAvatar(null);
                return;
            }

            if (_visibleConditionsSince < 0) _visibleConditionsSince = now;
            if (_avatar == null)
            {
                bool haveTemplate = RemoteAvatar.TryEnsureTemplate(_log);
                bool allowFallback = !haveTemplate && now - _visibleConditionsSince > _cfg.FallbackAfterSeconds;
                if (!haveTemplate && !allowFallback) return;
                _avatar = RemoteAvatar.Spawn(_node.RemoteInstanceId ?? "remote", _log, allowFallback);
                if (_avatar == null) return;
            }
            RemoteSample sample;
            if (_buffer.TrySample(now, out sample))
            {
                _avatar.Apply(sample, Time.unscaledDeltaTime, _cfg.YawOffset, _walkRef, _runRef, _crouchRef);
                _avatar.SetVisible(true);
                if (_gear != null && !_avatar.IsFallback) { try { _gear.Apply(_avatar.Root, sample, _ps); } catch (Exception ex) { _log.Error("AVATAR_GEAR presentation failed (disabled)", ex); _gear = null; } }
            }
        }

        // ---------------- M4.2.2 seated remote driver (called by VehicleSync after the car moved this frame) ----------------
        // M4.3: set by VehicleSync while the local player sits in a passenger seat (the game's is_in_vehicle stays false:
        // the passenger is no driver, see VehicleSync.EnterSeat).
        public bool LocalSeated;

        public bool RemoteSeatRequested { get { return _view == RemoteView.InVehicle && Now - _lastRecvAt < 3.0; } }

        public void PoseSeated(Vector3 anchorPos, Quaternion anchorRot, bool codenameRoot)
        {
            if (_view != RemoteView.InVehicle) return;
            if (_avatar == null)
            {
                if (!RemoteAvatar.TryEnsureTemplate(_log)) return;   // no capsule fallback for seats
                _avatar = RemoteAvatar.Spawn(_node.RemoteInstanceId ?? "remote", _log, false);
                if (_avatar == null) return;
            }
            _avatar.ApplySeated(anchorPos, anchorRot, codenameRoot);
            _avatar.SetVisible(true);
            if (_gear != null) { try { _gear.Seated(_avatar.Root); } catch (Exception ex) { _log.Error("AVATAR_GEAR seated presentation failed (disabled)", ex); _gear = null; } }
            _seatFrame = Time.frameCount;
        }

        void DestroyAvatar(string why)
        {
            if (_avatar == null) return;
            _avatar.Destroy();
            _avatar = null;
            if (why != null) _log.Info("AVATAR_DESTROYED (" + why + ")");
        }

        // ---------------- overlay ----------------
        public string StatusLine
        {
            get
            {
                string s = "remote " + _view;
                if (_view == RemoteView.OtherScene) s += " [" + _remoteSceneKey + "]";
                if (_avatar != null && _avatar.Visible) s += " clip=" + _avatar.CurrentClip + (_avatar.IsFallback ? " (capsule)" : "");
                return s;
            }
        }

        // M4.2 arrival placement: the remote player's latest feet position (same scene, fresh data only). Read-only.
        public bool TryGetRemote(out Vector3 feet, out bool inVehicle, out float horizontalSpeed)
        {
            feet = Vector3.zero; inVehicle = false; horizontalSpeed = 0f;
            if (!_node.IsConnected || !_remotePresent || !_buffer.HasData || Now - _lastRecvAt > 3.0 || _remoteSceneHash != _sceneHash) return false;
            RemoteSample s;
            if (!_buffer.TrySample(Now, out s)) return false;
            feet = new Vector3(s.Pos.X, s.Pos.Y, s.Pos.Z);
            inVehicle = (s.Flags & MoveFlags.InVehicle) != 0;
            horizontalSpeed = s.HorizontalSpeed;
            return true;
        }

        public bool TryGetNameTag(out Vector3 worldPos, out string label)
        {
            worldPos = Vector3.zero; label = null;
            if (!_cfg.ShowNameTag || _avatar == null || !_avatar.Visible) return false;
            worldPos = _avatar.Position + Vector3.up * 2.05f;
            label = (_remoteSlot == 1 ? "Host " : "Player 2 ") + (_node.RemoteInstanceId ?? "");
            return true;
        }

        public void Dispose()
        {
            _node.MessageReceived -= OnMessage;
            _node.RemoteConnected -= OnRemoteConnected;
            _node.RemoteDisconnected -= OnRemoteDisconnected;
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneUnloaded -= OnSceneUnloaded;
            DestroyAvatar("shutdown");
            if (_h != null) { _h.UnpatchSelf(); _h = null; }
        }
    }
}

// Shared between plugin and offline tests. No UnityEngine references. C# 5 (built with .NET Framework csc).
using System;
using LiteNetLib.Utils;

namespace TS2MP.Shared
{
    public static class ProtocolInfo
    {
        public const string Magic = "TS2MP";
        public const int ProtocolVersion = 19;  // 19 = M5 W3.2 dropped-item lifecycle bit (falling / settled) in the "witems" record; 18 = M5 W3.1 gear byte: owns-flashlight bit + 4-bit attack counter (remote swing), drop capture fix; 17 = M5 W3 shared hideout locker (storage kind 1), dropped-item registry "witems" + "itemTxn", 34 B player snapshot with gear (pitch, flashlight, held tool, heavy carry); 16 = M5 W2 world-key mirror messages (WorldKeySnapshot/Delta/Resync); 15 = M4.3 Phase B "jointTrip" (host-coordinated driver + passenger travel); 14 = M4.3.3 hotfix: P2Inventory v2 (record id), report base revision; 13 = M4.3.3 "p2inv"/"p2report"/"storageTxn"/"sleepShow"; 12 = M4.3.2b shared CarBox lease "carbox"/"carboxItems"/"carboxReq"; 11 = M4.3.1 "vdoor" slow/fast-finish door modes (3-byte proposal, slow bits in the state); 10 = M4.3 "vehicleRide" passenger seat, VehicleRecord PassengerPending/Tpp flags, VehicleState Tpp flag; 9 = M5.0 "vdoor" car door state, 39 B vehicle snapshots with steer angle; 8 = M4.2.2 "vehicleTake" driver handoff, VehicleState Moving flag; 7 = M4.2 travel grants/reservations, VehicleRecord v2 (holder), "vpark", 38 B vehicle snapshots with fuel; 6 = M4.1 vehicle fleet (key "vehicle"/"vehicleClaim", VehicleState on channel 2); 5 = M3.1.2 sleepStart + ProposalCode.Busy; 4 = M3.1 region clocks + sleep requests; 3 = M3 state messages; 2 = M2 PlayerState + SceneInfo
        public const string ModVersion = "0.10.4";  // 0.10.4 = M5 W3.4: CarBox / locker durability (unsaved Player-1 deposits journaled before a dependent Player-2 withdrawal, replay in commit order, p2inv file v3; protocol unchanged); 0.10.3 = M5 W3.3: remote flashlight presentation removed (no remote mesh/material/light; protocol unchanged); 0.10.2 = M5 W3.2: dropped items shown on the surface until the dropper reports the rest pose, settle detection from the original rigidbody lifecycle, original police flashlight with lens-only glow, crowbar centred in the palm; 0.10.1 = M5 W3.1 hotfix: dropped-item capture from the original Instantiate (shared drops work), refusal before inventory change, remote crowbar grip + swing, chest lamp instead of a remote spotlight; 0.10.0 = M5 W3 shared locker, shared dropped items, held-tool / heavy-carry / flashlight presentation; 0.9.1 = M5 hotfix: on-foot placement after a load without travel grant (caught-screen Load / Continue into Madison/Hillville), fall guard; 0.9.0 = M5 W1 world identity census + W2 host-authoritative world-key mirror; 0.8.0 = M4.3 Phase B joint vehicle travel (driver + passenger); 0.7.1 = M4.3.3 hotfix: Player-2 restore/acknowledge lifecycle, durable reports, final sync on scene exit; 0.7.0 = M4.3.3 durable Player-2 inventory, client CarBox transfers, Blackbay/locker guards, co-op native sleep presentation; 0.6.3 = M4.3.2b native passenger entry prompt, shared CarBox lease + host-authoritative contents; 0.6.2 = M4.3.2a no mod text in the game UI, accessibility-based passenger entry, exit beside the seat without door animation, canInteract kept off while seated; 0.6.1 = M4.3.1 passenger E entry/exit resolver, storage from the seat, local passenger body in third person, slow car-door sync; 0.6.0 = M4.3 Phase A passenger seat, passenger camera mode, door rule "stopped"; 0.5.0 = M5.0 original car object as the remote presentation in every region (kinematic, solid, original doors/trigger), door state sync, remote wheel visuals; 0.4.6 = M4.2.4 proxy shell without car-door colliders, enter trigger follows the seat; 0.4.5 = M4.2.3 driver carries the car, proxy enter trigger body, seat calibration; 0.4.4 = M4.2.2 driver handoff, seated remote driver, remote car collision, stay handover on travel; 0.4.3 = M4.2.1 hideout travel proxy + state restoration; 0.4.2 = M4.2 host-authoritative travel, region car assignment, fleet persistence; 0.4.0 = M4.1 shared fleet registry + host car replication
        public const int MaxClients = 1;
    }

    public enum Role { None, Host, Client }

    public enum ConnState { Stopped, Listening, Connecting, Handshaking, Connected, WaitingRetry, Failed }

    public enum MsgType : byte { Welcome = 1, PlayerState = 2, SceneInfo = 3, StateSnapshot = 4, StateDelta = 5, StateResyncRequest = 6, StateProposal = 7, ProposalResult = 8, VehicleState = 9, WorldKeySnapshot = 10, WorldKeyDelta = 11, WorldKeyResync = 12 }

    // Sent by the host as reject payload. Values are part of the protocol; do not renumber.
    public enum RejectCode : byte
    {
        Malformed = 1,
        BadMagic = 2,
        ProtocolMismatch = 3,
        ModVersionMismatch = 4,
        GameVersionMismatch = 5,
        SessionMismatch = 6,
        SessionFull = 7,
        DuplicateInstance = 8
    }

    // Client -> host, carried as LiteNetLib connection-request data.
    public sealed class Hello
    {
        public string Magic = ProtocolInfo.Magic;
        public int ProtocolVersion = ProtocolInfo.ProtocolVersion;
        public string ModVersion = ProtocolInfo.ModVersion;
        public string GameVersion = "";
        public string SessionId = "";
        public string InstanceId = "";

        public void Write(NetDataWriter w)
        {
            w.Put(Magic ?? "");
            w.Put(ProtocolVersion);
            w.Put(ModVersion ?? "");
            w.Put(GameVersion ?? "");
            w.Put(SessionId ?? "");
            w.Put(InstanceId ?? "");
        }

        public static bool TryRead(NetDataReader r, out Hello hello)
        {
            hello = null;
            try
            {
                string magic;
                if (!r.TryGetString(out magic)) return false;
                var h = new Hello();
                h.Magic = magic;
                if (magic != ProtocolInfo.Magic) { hello = h; return true; } // caller rejects BadMagic
                if (!r.TryGetInt(out h.ProtocolVersion)) return false;
                if (!r.TryGetString(out h.ModVersion)) return false;
                if (!r.TryGetString(out h.GameVersion)) return false;
                if (!r.TryGetString(out h.SessionId)) return false;
                if (!r.TryGetString(out h.InstanceId)) return false;
                hello = h;
                return true;
            }
            catch (Exception) { return false; }
        }
    }

    // Host -> client after accept, ReliableOrdered.
    public sealed class Welcome
    {
        public string SessionId = "";
        public string HostInstanceId = "";
        public string HostModVersion = ProtocolInfo.ModVersion;
        public string GameVersion = "";
        public byte PlayerSlot = 2;

        public void Write(NetDataWriter w)
        {
            w.Put((byte)MsgType.Welcome);
            w.Put(SessionId ?? "");
            w.Put(HostInstanceId ?? "");
            w.Put(HostModVersion ?? "");
            w.Put(GameVersion ?? "");
            w.Put(PlayerSlot);
        }

        // Reader positioned after the MsgType byte.
        public static bool TryRead(NetDataReader r, out Welcome welcome)
        {
            welcome = null;
            var x = new Welcome();
            if (!r.TryGetString(out x.SessionId)) return false;
            if (!r.TryGetString(out x.HostInstanceId)) return false;
            if (!r.TryGetString(out x.HostModVersion)) return false;
            if (!r.TryGetString(out x.GameVersion)) return false;
            if (!r.TryGetByte(out x.PlayerSlot)) return false;
            welcome = x;
            return true;
        }
    }

    public static class Validation
    {
        // Host-side check of a client hello. Returns null if accepted.
        public static RejectCode? Check(Hello h, string sessionId, string gameVersion, string hostInstanceId)
        {
            if (h == null) return RejectCode.Malformed;
            if (h.Magic != ProtocolInfo.Magic) return RejectCode.BadMagic;
            if (h.ProtocolVersion != ProtocolInfo.ProtocolVersion) return RejectCode.ProtocolMismatch;
            if (h.ModVersion != ProtocolInfo.ModVersion) return RejectCode.ModVersionMismatch;
            if (h.GameVersion != gameVersion) return RejectCode.GameVersionMismatch;
            if (h.SessionId != sessionId) return RejectCode.SessionMismatch;
            if (h.InstanceId == hostInstanceId || string.IsNullOrEmpty(h.InstanceId)) return RejectCode.DuplicateInstance;
            return null;
        }

        // Rejections the client should not retry (configuration problems).
        public static bool IsFatal(RejectCode c)
        {
            return c != RejectCode.SessionFull;
        }
    }
}

// Shared between plugin and offline tests. No UnityEngine references. C# 5 (built with .NET Framework csc).
using System;
using LiteNetLib.Utils;

namespace TS2MP.Shared
{
    public static class ProtocolInfo
    {
        public const string Magic = "TS2MP";
        public const int ProtocolVersion = 41;  // 41 = W6B.4: storyCall / storyCallAnswer record v2 (stage key + scene-path hash identity, answered / finished phase); 40 = W6B.3: "missionAct" proposal + "missions" state (cooperative story tasks), storyAdvance kind 2 BlackbaySale; 39 = W6B.2: heavyTxn op 9 Unpack (mission-box unpacking, host consumes the box record once); 38 = W6B: player snapshot drone block (MoveFlags.Drone + 18 B drone pose), region object kind SecurityCamera (permanent disable), story call answer (proposal "storyCallAnswer", state "storyCall"); 37 = W6A: shared story progression (client proposal "storyAdvance" = from index + stage key + listed trigger, host-committed session state "story" = the host StoryManager index + stage key); 36 = W2B: shared parcel codes (client proposal "parcelCode" = source + pref, host-committed session state "parcels" = the host campaign's discovered code prefs) + region-object kind ParcelLocker = 22 (registry "wobj22[.page]", state 0 closed / 1 open); 35 = W2A: shared destructible world objects (original DestroyObject: House 102 barrier stages, breakable furniture, toilets) - new region-object kind Breakable = 26 (registry "wobj26[.page]", state 0 intact / 1 destroyed, terminal for the region epoch) in "objTxn" / "objInit"; 34 = H3: "heavyTxn" ops Sell = 7 / Unsell = 8 (host-consumed heavy shop sale), "vdoor" mask v2: 3 doors (L, R, Ranger rear hatch), open bits 0-2, slow bits 3-5; 33 = V1.4.1: shared single-use car medkit (client proposal "medkitUse" 1 B, arg = car id; host-committed state "medkit" 4 B); 32 = W1: shared hideout purchases (client proposal "hbuy" 3 B, host-committed state "hideout" 8 B); 31 = V1.2: client vehicle purchase proposal "vbuy" (Shadowhawk, host-charged once), Auto Shop travel proxy; 30 = H2: shared heavy vehicle cargo (registry "hcargo" per car, "heavyTxn" op Load = 6, synthetic cargo ids in "wheavy"); 29 = H1: heavy-object leases ("heavyTxn" proposal, registry "wheavy" per region); 28 = W4.2D: loot takes ("lootTxn" / registry "wloot0..3") for every census namespace incl. MainScene (-1000) and regions the host never loaded (host scene-file catalogs); 27 = W4.1B: shared money (registry "wallet" 16 B, P2Inventory v3 with Wallet/Spend, lootTxn for world money + unique valuables); 26 = W4.2B.2: Welcome carries the host's multiplayer-test-campaign flag (1 byte); 25 = W4.2B.1: the host commits a door / window-sash "slow mark dropped" transition (actor finished a hold-E swing fast; same physical state, new version) instead of answering Unchanged; 24 = W4.2B: "objTxn" v2 (19 B: op set/reserve/release/NPC set + region epoch), proposals "objScene" (scene instance of a region) and "objInit" (initializer chunks), registry "wrgn" (region epoch record) + paged "wobj<kind>[.<page>]" for doors / locks / windows; 23 = W4.2 Phase A: CampaignSnapshot message (host campaign base for the client sandbox + Player-2 spawn plan), proposals "objTxn" (world-object transition) and "p2pos" (Player-2 last valid position), registry keys "wobj<kind>" (per-region object states); 22 = N1.2 F8 menu: ClientReady step after Welcome (client prepares save isolation before gameplay messages), reject code HostNotReady (host not gameplay-ready, retryable); 21 = N1 direct connect: Hello carries a client nonce, AuthChallenge/AuthResponse (HMAC join-code challenge-response), Welcome carries the host proof, reject codes AuthRequired/AuthFailed/RateLimited/Oversized; 20 = M5 W4.1 shared world loot: client proposal "lootTxn", registry keys "wloot0".."wloot3" (taken pre-placed loot per region); 19 = M5 W3.2 dropped-item lifecycle bit (falling / settled) in the "witems" record; 18 = M5 W3.1 gear byte: owns-flashlight bit + 4-bit attack counter (remote swing), drop capture fix; 17 = M5 W3 shared hideout locker (storage kind 1), dropped-item registry "witems" + "itemTxn", 34 B player snapshot with gear (pitch, flashlight, held tool, heavy carry); 16 = M5 W2 world-key mirror messages (WorldKeySnapshot/Delta/Resync); 15 = M4.3 Phase B "jointTrip" (host-coordinated driver + passenger travel); 14 = M4.3.3 hotfix: P2Inventory v2 (record id), report base revision; 13 = M4.3.3 "p2inv"/"p2report"/"storageTxn"/"sleepShow"; 12 = M4.3.2b shared CarBox lease "carbox"/"carboxItems"/"carboxReq"; 11 = M4.3.1 "vdoor" slow/fast-finish door modes (3-byte proposal, slow bits in the state); 10 = M4.3 "vehicleRide" passenger seat, VehicleRecord PassengerPending/Tpp flags, VehicleState Tpp flag; 9 = M5.0 "vdoor" car door state, 39 B vehicle snapshots with steer angle; 8 = M4.2.2 "vehicleTake" driver handoff, VehicleState Moving flag; 7 = M4.2 travel grants/reservations, VehicleRecord v2 (holder), "vpark", 38 B vehicle snapshots with fuel; 6 = M4.1 vehicle fleet (key "vehicle"/"vehicleClaim", VehicleState on channel 2); 5 = M3.1.2 sleepStart + ProposalCode.Busy; 4 = M3.1 region clocks + sleep requests; 3 = M3 state messages; 2 = M2 PlayerState + SceneInfo
        public const string ModVersion = "0.30.0";  // 0.30.0 = W6B.4: shared original story calls (identity fix, finish, no replay) + mission minimap blips; 0.29.0 = W6B.3: mission-box contents shared as one host world item (both PCs see / either picks once), cooperative story tasks 110 plant / 105 photo, story16 Blackbay sale by Player 2; 0.28.0 = W6B.2: mission-box unpacking exactly once - CardboardMinigame.SkipMinigameWin / MinigameWon gated: the host consumes the box record once (HeavyOp.Unpack), the contents are created once by the HOST's original SpawnItem (host-owned; Player 2 never runs SpawnItem / SpawnItemOnStart), the other PC's box disappears with the record; protocol 39; 0.27.1 = W6B.1: two-PC log follow-up - Player-2 inventory reports count the drone in flight as owned (original ActivateDrone item_amount-- / landing ++ made reports flip 1 <-> 0 and a mid-flight disconnect could lose the drone), event diagnostics for drone start / end (body, drone, transform), remote drone, camera paths (incl. local-only), the Player-2 camera story step and ring start; protocol 38 unchanged; 0.27.0 = W6B: drone flight shown on the other PC (the pilot body stays standing at the original joystick_character spot, the drone is a renderer-only clone of the original drone model, interpolated), position reports never use the drone pose; permanent security-camera disables shared per region epoch on the original SecurityCamera (either player, host commits once; the host applies DisablePermamently with its original onDisableAdvanceStory -> Player 2 completes story14 with its drone); story phone call answered once for both (original answer / bypass branch mirrored; declining stays personal); protocol 38; 0.26.5 = W6A.5: story-test join deadlock fixed - the W6A.4 scenario check required the note_103 holder (story object 103 notka), which is serialized inactive in the original MadisonSt asset and never activated (and sits in the distance-culled Interior_103), so every index-18 world failed and the client got HostNotReady forever; story actions are now diagnostics (ScenarioActions: camera_104_drone = the live story14 objective, note_103 = unavailable in the original), one readiness state (JoinGate) for the network gate / HOST_READINESS / F8 chip / F10, an invalid scenario refuses joins permanently (RejectCode.ScenarioInvalid = 14, client stops retrying); Player-2 developer drone grant rule extracted (ScenarioPrereqs.P2Grants); no wire-format change (protocol 37, both PCs need 0.26.5); 0.26.4 = W6A.4: story-test prerequisites (ScenarioPrereqs: Flyby drone item 89 + Drone key + camera module flyby_upgrade3 from the original persisted keys; Player 2 gets its own drone in its fresh record), Player 2 first spawn beside the host in story tests, full scenario check after the world is ready gating joins + HOST READY; no wire change (protocol 37); 0.26.3 = W6A.3: F10 story test = fresh isolated story seed (original MainMenu.StartNewGame skip-intro inside the verified sandbox, then the selected index) loaded automatically, never a copy of the genuine (possibly free-mode) save; visible persistent start errors, world check of the loaded index (note_103 for story14), HOST READY status (F10 / in-world read-only F10 status / log); no wire change (protocol 37); 0.26.2 = W6A.2: own visible pointer in the F8 / F10 menus (the main menu hides the hardware cursor every frame and its software cursor stops with the disabled EventSystem), game cursor restored on close; F10 developer test sessions join without a code (fixed developer key per protocol, refused by every F8 host); F10 story list as selectable rows instead of checkboxes; no wire change (protocol 37); 0.26.1 = W6A.1: public F8 multiplayer menu redesigned (normal Host / Join only, styled with the game font + click sound, input of the original main menu blocked while open) and a separate F10 developer menu (Developer.EnableDeveloperMenu, default off: isolated test campaign host / join, story-step selector 0-80 with support classification, scenario reset); no wire change (protocol 37); 0.26.0 = W6A: shared story foundation - one story index (the host campaign), Player 2 follows it with the original InitiateStory presentation and never advances a private copy; note_103 read by either player advances story14 once through the host's original AdvanceStory; a code note whose parcel was taken stays readable while its story step is current; 0.25.0 = W2B: shared parcel codes - a code note read (or item hacked) by either player becomes known on both PCs through the host campaign (original discovery mutation applied once by the host, durable with its next save, re-applied after travel / reload / reconnect), parcel-locker doors opened by either player open on both PCs, Player 2 reading a story note never advances a private story copy (protocol 36); 0.24.0 = W2A: shared destructible world objects - either player breaks an original DestroyObject (House 102 plywood + plank stages, breakable furniture, toilets) with the crowbar, the host commits each object exactly once (Player 2 only breaks after the host accepted), the other PC shows the original broken variant without NPC alert or mission effects, late joiners see the region state, region reload resets like the original (protocol 35); 0.23.0 = H3: heavy loot sale integrity (each heavy object sold once: the host consumes the record, the original sale pays once on the seller's PC, repeated / concurrent / stale / replayed sales refused) + Ranger rear hatch synchronized on the original Door (vdoor door 2) (protocol 34); 0.22.1 = S1: shared sleep control - only the player who started the sleep can cancel / end it with the original PassTime controls (observer Esc / buttons refused, observer holds at the target until the sleeper ends), own sleep refused with the original warning while the other player sleeps in the region, busy observers retry, short client cancels release the host transaction (protocol unchanged); 0.22.0 = V1.4.1: first real car home takes the hideout main spot (Ranger or Shadowhawk), the other proxy yields to the second spot, proxies hold while a car moves; Player 2 uses the original car medkit (on foot and from the passenger seat when reachable) as a host transaction, consumed once on both PCs, never on a travel proxy (protocol 33); 0.21.3 = V1.4 follow-up: a hideout car the driver sat in / drove is never rearranged afterwards and, while stopped, is a layout obstacle on the holder too (same rule as the observer; protocol unchanged); 0.21.2 = V1.4: shared hideout parking layout of both cars (HideoutLayout: identity spots Ranger = SpawnHideoutCar, others = secondCarHideoutPos; committed hideout poses kept; proxies follow the shared layout; protocol unchanged); 0.21.1 = W1.1: Player-2-initiated renovation rebuild on Player 1 (HideoutRebuild: retry kept, disablesound load detection, no Player-2 purchase during a host scene transition; protocol unchanged); 0.21.0 = W1: shared garage purchase + hideout renovations (both players, host transaction on the shared balance, original keys / queues on the host campaign, live garage swap / wallpapers, coordinated original hideout rebuild for players in the hideout); 0.20.0 = V1.2: shared Shadowhawk purchase (both players, host transaction on the shared balance + original unlock key), fleet unlock follows the host unlock state live, newly owned car placed at the original hideout spot, Auto Shop travel-only proxy for the player who stays; 0.19.0 = H2 shared heavy vehicle cargo: both players load / unload heavy objects in the Ranger cargo bed on the original objects, host-authoritative cargo list saved in the original HeavyVan_ keys by the host; 0.18.0 = H1 heavy objects: host lease, Player-2 pickup / carry / drop on the original carry system, rest pose committed by the dropper; 0.17.0 = W4.2D cross-region loot (host scene-file catalogs, MainScene loot, LiveOnly / keys / contract / parcel / seeded cabinets); 0.16.1 = two-PC QA fixes (protocol unchanged): Player 2 cannot duplicate unshared world loot, shared-name loot families shared, original loading screen during the join, Player-2 spawn from the host checkpoint. 0.16.0 = W4.1B integrated on 0.15.0: one host-owned shared money balance (Player-2 money changes journaled + replayed, client purchases checked by the host, host/world purchases refused on the client), shared world money and unique valuables in Madison Street; 0.15.0 = W4.2C Phase B: shared furniture - drawers, cupboards, fridges, file / glass cabinets and containers (original Door leaves) on the host-owned region-object registry like doors; shared object regions Madison Street + Hillville + hideout; incremental committed-page cache (protocol unchanged, both instances need 0.15.0); 0.14.4 = W4.2C Phase A: cancelling a prepared F8 host in the main menu returns to the complete offline menu (the "Testkampagne hosten" preparation - test save copy, test records, test conveniences - is released again when no world was loaded; protocol unchanged); 0.14.3 = W4.2B.3: main-menu-only multiplayer lifecycle (host prepared in the main menu before the save loads, F8 inert in the world, menu return ends the session), host fleet holder invariant right after placement, observer wheel ray skips the car's own colliders (EVP DisableCollidersRaycast), VEHICLE_GROUND diagnostics, logged session cleanup (protocol unchanged); 0.14.2 = W4.2B.2: F8 multiplayer test campaign (isolated host save copy + test records, all skills, test money for both players), first-person legs hidden for a seated passenger, observed-car wheels placed like EVP (no floating car); 0.14.1 = W4.2B.1: regression fixes - Player-2 driving camera (TPP default), launcher host readiness gate, door close after relock, gates + broken doors + hold-E swings on the replica, developer test mode (launcher "devtest" only); 0.14.0 = W4.2B: shared doors (incl. broken hinges), locks / lockpicking (host reservations, key, padlock, crowbar) and windows (unlock from inside, glass cutting, broken glass, sash) in Madison Street; region epochs with canonical initial state (randomly open doors) and member grace; host already in the region when hosting starts; 0.13.0 = W4.2 Phase A: host-owned campaign bootstrap for Player 2 (no solo world/progress/position), persistent Player-2 spawn + story-safe fallback, host-owned region-object registry, shared ordinary doors (Madison Street slice); 0.12.0 = N1.2: in-game F8 multiplayer menu (host from a loaded world, join from the main menu with late-armed save isolation), drag-and-drop release; 0.11.1 = N1: direct-IP internet multiplayer with join-code authentication, launcher install/host/join, release package (gameplay sync unchanged from 0.11.0); 0.11.0 = M5 W4.1 Phase B: host-authoritative shared pre-placed world loot (Madison Street subset), durable Player-2 world takes + Player-1 loot provenance (p2inv file v4); 0.10.4 = M5 W3.4: CarBox / locker durability (unsaved Player-1 deposits journaled before a dependent Player-2 withdrawal, replay in commit order, p2inv file v3; protocol unchanged); 0.10.3 = M5 W3.3: remote flashlight presentation removed (no remote mesh/material/light; protocol unchanged); 0.10.2 = M5 W3.2: dropped items shown on the surface until the dropper reports the rest pose, settle detection from the original rigidbody lifecycle, original police flashlight with lens-only glow, crowbar centred in the palm; 0.10.1 = M5 W3.1 hotfix: dropped-item capture from the original Instantiate (shared drops work), refusal before inventory change, remote crowbar grip + swing, chest lamp instead of a remote spotlight; 0.10.0 = M5 W3 shared locker, shared dropped items, held-tool / heavy-carry / flashlight presentation; 0.9.1 = M5 hotfix: on-foot placement after a load without travel grant (caught-screen Load / Continue into Madison/Hillville), fall guard; 0.9.0 = M5 W1 world identity census + W2 host-authoritative world-key mirror; 0.8.0 = M4.3 Phase B joint vehicle travel (driver + passenger); 0.7.1 = M4.3.3 hotfix: Player-2 restore/acknowledge lifecycle, durable reports, final sync on scene exit; 0.7.0 = M4.3.3 durable Player-2 inventory, client CarBox transfers, Blackbay/locker guards, co-op native sleep presentation; 0.6.3 = M4.3.2b native passenger entry prompt, shared CarBox lease + host-authoritative contents; 0.6.2 = M4.3.2a no mod text in the game UI, accessibility-based passenger entry, exit beside the seat without door animation, canInteract kept off while seated; 0.6.1 = M4.3.1 passenger E entry/exit resolver, storage from the seat, local passenger body in third person, slow car-door sync; 0.6.0 = M4.3 Phase A passenger seat, passenger camera mode, door rule "stopped"; 0.5.0 = M5.0 original car object as the remote presentation in every region (kinematic, solid, original doors/trigger), door state sync, remote wheel visuals; 0.4.6 = M4.2.4 proxy shell without car-door colliders, enter trigger follows the seat; 0.4.5 = M4.2.3 driver carries the car, proxy enter trigger body, seat calibration; 0.4.4 = M4.2.2 driver handoff, seated remote driver, remote car collision, stay handover on travel; 0.4.3 = M4.2.1 hideout travel proxy + state restoration; 0.4.2 = M4.2 host-authoritative travel, region car assignment, fleet persistence; 0.4.0 = M4.1 shared fleet registry + host car replication
        public const int MaxClients = 1;
    }

    public enum Role { None, Host, Client }

    public enum ConnState { Stopped, Listening, Connecting, Handshaking, Connected, WaitingRetry, Failed, Preparing }   // Preparing (N1.2): client authenticated + welcomed, preparing save isolation before ClientReady

    public enum MsgType : byte { Welcome = 1, PlayerState = 2, SceneInfo = 3, StateSnapshot = 4, StateDelta = 5, StateResyncRequest = 6, StateProposal = 7, ProposalResult = 8, VehicleState = 9, WorldKeySnapshot = 10, WorldKeyDelta = 11, WorldKeyResync = 12, AuthChallenge = 13, AuthResponse = 14, ClientReady = 15, CampaignSnapshot = 16, CampaignRequest = 17, HeavyMotion = 18 }   // 18: H1.1 heavy-object movement snapshots (unreliable)

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
        DuplicateInstance = 8,
        AuthRequired = 9,   // N1: the host requires a join code and the client has none
        AuthFailed = 10,    // N1: wrong join code (or a replayed / unbound response)
        RateLimited = 11,   // N1: too many attempts from this address; retry later
        Oversized = 12,     // N1: connection request larger than Hello.MaxBytes
        HostNotReady = 13,  // N1.2: host is not gameplay-ready (loading, menu, persistence not bound); retry later
        ScenarioInvalid = 14 // W6A.5: developer story test failed its scenario check on the host; permanent, do not retry
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
        public byte[] ClientNonce = new byte[JoinAuth.NonceBytes]; // protocol >= 21; fresh random per connection attempt

        public const int MaxBytes = 256;  // host refuses larger connection requests before parsing

        public void Write(NetDataWriter w)
        {
            w.Put(Magic ?? "");
            w.Put(ProtocolVersion);
            w.Put(ModVersion ?? "");
            w.Put(GameVersion ?? "");
            w.Put(SessionId ?? "");
            w.Put(InstanceId ?? "");
            w.PutBytesWithLength(ClientNonce ?? new byte[0]);
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
                if (h.ProtocolVersion != ProtocolInfo.ProtocolVersion) { h.ClientNonce = null; hello = h; return true; } // caller rejects ProtocolMismatch
                if (!r.TryGetBytesWithLength(out h.ClientNonce)) return false;
                if (h.ClientNonce == null || h.ClientNonce.Length != JoinAuth.NonceBytes || r.AvailableBytes != 0) return false;
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
        public byte[] HostProof = new byte[0];  // N1: HMAC proof of the join code (empty when the host runs without a code: loopback only)
        public bool TestCampaign;               // W4.2B.2: the host plays the isolated multiplayer TEST campaign (client uses its test sandbox + test conveniences)

        public void Write(NetDataWriter w)
        {
            w.Put((byte)MsgType.Welcome);
            w.Put(SessionId ?? "");
            w.Put(HostInstanceId ?? "");
            w.Put(HostModVersion ?? "");
            w.Put(GameVersion ?? "");
            w.Put(PlayerSlot);
            w.PutBytesWithLength(HostProof ?? new byte[0]);
            w.Put((byte)(TestCampaign ? 1 : 0));
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
            if (!r.TryGetBytesWithLength(out x.HostProof)) return false;
            if (x.HostProof == null || (x.HostProof.Length != 0 && x.HostProof.Length != JoinAuth.ProofBytes)) return false;
            byte flags; if (!r.TryGetByte(out flags) || flags > 1) return false;
            x.TestCampaign = flags == 1;
            welcome = x;
            return true;
        }
    }

    public static class Validation
    {
        // Host-side check of a client hello. Returns null if accepted.
        public static RejectCode? Check(Hello h, string sessionId, string gameVersion, string hostInstanceId)
        {
            return Check(h, sessionId, gameVersion, hostInstanceId, true);
        }

        // checkSession = false when the host authenticates with a join code: a remote client cannot know the random
        // session id, the join code replaces it.
        public static RejectCode? Check(Hello h, string sessionId, string gameVersion, string hostInstanceId, bool checkSession)
        {
            if (h == null) return RejectCode.Malformed;
            if (h.Magic != ProtocolInfo.Magic) return RejectCode.BadMagic;
            if (h.ProtocolVersion != ProtocolInfo.ProtocolVersion) return RejectCode.ProtocolMismatch;
            if (h.ModVersion != ProtocolInfo.ModVersion) return RejectCode.ModVersionMismatch;
            if (h.GameVersion != gameVersion) return RejectCode.GameVersionMismatch;
            if (h.ClientNonce == null || h.ClientNonce.Length != JoinAuth.NonceBytes) return RejectCode.Malformed;
            if (checkSession && h.SessionId != sessionId) return RejectCode.SessionMismatch;
            if (h.InstanceId == hostInstanceId || string.IsNullOrEmpty(h.InstanceId)) return RejectCode.DuplicateInstance;
            return null;
        }

        // Rejections the client should not retry (configuration problems).
        public static bool IsFatal(RejectCode c)
        {
            return c != RejectCode.SessionFull && c != RejectCode.RateLimited && c != RejectCode.HostNotReady;
        }
    }

    // N1 auth messages (reader positioned after the MsgType byte). Exact sizes; anything else is malformed.
    public static class AuthMessages
    {
        public static void WriteChallenge(NetDataWriter w, byte[] serverNonce)
        {
            w.Put((byte)MsgType.AuthChallenge);
            w.Put(serverNonce, 0, JoinAuth.NonceBytes);
        }

        public static bool TryReadChallenge(NetDataReader r, out byte[] serverNonce)
        {
            serverNonce = null;
            if (r.AvailableBytes != JoinAuth.NonceBytes) return false;
            serverNonce = new byte[JoinAuth.NonceBytes];
            r.GetBytes(serverNonce, JoinAuth.NonceBytes);
            return true;
        }

        public static void WriteResponse(NetDataWriter w, byte[] proof)
        {
            w.Put((byte)MsgType.AuthResponse);
            w.Put(proof, 0, JoinAuth.ProofBytes);
        }

        public static bool TryReadResponse(NetDataReader r, out byte[] proof)
        {
            proof = null;
            if (r.AvailableBytes != JoinAuth.ProofBytes) return false;
            proof = new byte[JoinAuth.ProofBytes];
            r.GetBytes(proof, JoinAuth.ProofBytes);
            return true;
        }
    }
}

// M4.2: host-authoritative travel, region car assignment, per-car parking/fuel, fleet persistence. Unity-free.
// Game facts: CODEBASE_MAP §3.7 (two permanent cars, carNr 0/1), §3.9 (GameManager.Select* save + reload MainScene,
// arrival in Madison/Hillville seats the player in GameManager.Car), PROJECT_PLAN "Region vehicle rule".
//
// Transaction (one host decision + arrival): the traveller's travel click is held (client) or decided in place (host);
// the host's TravelDesk decides against the registry in one serialized step and commits the vehicle records plus a
// per-player "travelGrant" entry; the traveller resumes the original travel exactly once; the traveller's confirmed
// region at the destination commits the reservation, anything else (other region after departure, cancel, disconnect,
// timeout) rolls it back.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using LiteNetLib.Utils;

namespace TS2MP.Shared
{
    public static class TravelRules
    {
        public const int Hideout = 0;
        public const int Jail = 93;
        public const float MaxFuel = 100f;                 // SaveGame.Update refills an empty tank to 60
        public const double ReservationTimeout = 180;      // safety net only; normal end = arrival / failure event

        // Regions where the mod controls player-car activation. Heists, Warehouse, Bank, Jail, SwatRange, IntroHouse and
        // PawnShop (no own scene in 1.27c, never confirmed) keep the original behaviour (mission-specific transport).
        public static bool IsFleetRegion(int r) { return r == Hideout || r == 1 || r == 3 || r == 97 || r == 99; }
        public static bool OneCarRegion(int r) { return IsFleetRegion(r) && r != Hideout; }   // the hideout garage stores both
        public static bool IsMenuDestination(int r) { return Regions.IsKnown(r) && r != Jail && r != -1; }
        public static bool SeatsOnArrival(int r) { return r == 97 || r == 99; }   // GameManager.Start -> CarController.IsStartingCar

        // Phone "fast travel to car" (PlayerScript.SpawnAtCar charges before checking the car): in fleet regions only when
        // this player simulates an active car of its own here; mission/special regions keep the original behaviour.
        public static bool FastTravelAllowed(int region, bool ownActiveCarHere) { return !IsFleetRegion(region) || ownActiveCarHere; }
    }

    // M4.2.1: how one local original car object is presented, derived only from the host's authoritative record and the
    // local region (never from the local save's CurrentCar). Pure.
    public enum CarPresentation : byte
    {
        Hidden = 0,        // original object inactive (car elsewhere outside the hideout, or not unlocked on the host)
        Real = 1,          // this instance simulates the real car (holder): original controller, driving, storage
        ParkedOther = 2,   // hideout: the real car is here but the other player simulates it -> frozen original object
                           //          following its pose; E requests the free driver seat (M4.2.2 handoff), no storage
        AwayProxy = 3,     // hideout: the real car is in another region -> static travel access point, E opens the travel menu
        RemoteVisual = 4   // outside the hideout: the other player simulates it here -> stripped visual (M4.1 proxy) with a
                           //          kinematic collision shell and the original enter trigger (M4.2.2)
    }

    // M4.2.2: what E on a car presentation does.
    public enum EnterAction : byte
    {
        Original = 0,       // this instance simulates the car: original CarController.EnterVehicle
        TravelMenu = 1,     // hideout access point of a car in another region: original travel menu
        RequestDriver = 2,  // the car is here, simulated by the other instance, driver seat free and car settled
        Occupied = 3,       // driver and passenger seats taken
        Wait = 4,           // car still moving: no handoff / no entry
        Unavailable = 5,    // travelling / locked / not presented
        RequestPassenger = 6 // M4.3: the other player drives, passenger seat free, car stopped -> passenger seat request
    }

    // M4.2.2: local collision of a non-simulated presentation. Never physically authoritative.
    public enum CollisionMode : byte
    {
        None = 0,           // nothing presented
        Original = 1,       // this instance simulates the car (original physics)
        Solid = 2,          // static/parked: colliders on, body frozen (hideout) or kinematic shell (outside)
        Ghost = 3           // driven or moving remotely: colliders off (no pushing of local players, NPCs or traffic)
    }

    public static class PresentationRules
    {
        public static CarPresentation Decide(VehicleRecord r, int localRegion, byte mySlot)
        {
            if (localRegion == Regions.None || !TravelRules.IsFleetRegion(localRegion) || r.Region == Regions.None) return CarPresentation.Hidden;
            bool here = r.Region == localRegion;
            if (here && r.Holder == mySlot && r.Unlocked) return CarPresentation.Real;
            if (localRegion == TravelRules.Hideout)
            {
                if (!r.Unlocked) return CarPresentation.Hidden;                 // never show an unbought car as owned
                return here ? CarPresentation.ParkedOther : CarPresentation.AwayProxy;
            }
            return here && r.Holder != 0 ? CarPresentation.RemoteVisual : CarPresentation.Hidden;
        }

        // M5.0: every non-simulated presentation uses the ORIGINAL scene object of the car. ParkedOther (hideout) and
        // RemoteVisual (other regions) are "observed": kinematic, driven by the holder's snapshots, original colliders,
        // doors and enter trigger. AwayProxy (hideout copy of a car that is elsewhere) is a frozen travel access point.
        public static bool IsTravelProxy(CarPresentation p) { return p == CarPresentation.AwayProxy; }
        public static bool IsObserved(CarPresentation p) { return p == CarPresentation.ParkedOther || p == CarPresentation.RemoteVisual; }
        public static bool AllowsDriving(CarPresentation p) { return p == CarPresentation.Real; }
        public static bool AllowsStorage(CarPresentation p) { return p == CarPresentation.Real; }
        public static bool EnterOpensTravelMenu(CarPresentation p) { return p == CarPresentation.AwayProxy; }
        public static bool FollowsRemotePose(CarPresentation p) { return p == CarPresentation.ParkedOther || p == CarPresentation.RemoteVisual; }
        public static bool IsSharedHere(CarPresentation p) { return p == CarPresentation.ParkedOther || p == CarPresentation.RemoteVisual; }

        // 'settled' = the observer saw no movement / driving of the car recently (the host re-checks with its own data).
        public static EnterAction Enter(CarPresentation p, VehicleRecord r, byte mySlot, bool settled) { return Enter(p, r, mySlot, settled, settled); }

        // M4.3: 'stopped' = no movement seen (a driver may sit inside). Occupied driver seat -> passenger request.
        public static EnterAction Enter(CarPresentation p, VehicleRecord r, byte mySlot, bool settled, bool stopped)
        {
            switch (p)
            {
                case CarPresentation.Real: return EnterAction.Original;
                case CarPresentation.AwayProxy: return EnterAction.TravelMenu;
                case CarPresentation.ParkedOther:
                case CarPresentation.RemoteVisual:
                    if (!r.Unlocked || r.Reserved) return EnterAction.Unavailable;
                    if (r.Passenger == mySlot) return EnterAction.Unavailable;
                    if (r.Driver != 0 && r.Driver != mySlot)
                    {
                        if (r.Passenger != 0) return EnterAction.Occupied;
                        return stopped ? EnterAction.RequestPassenger : EnterAction.Wait;
                    }
                    if (!settled) return EnterAction.Wait;
                    return EnterAction.RequestDriver;
                default: return EnterAction.Unavailable;
            }
        }

        // M5.0: a presented car is a physical obstacle in every state (parked, occupied, starting, driving), like the
        // original. The observed copy is a kinematic body moved to the holder's poses; it never simulates driving,
        // damage or repair costs (CarController.OnCollisionEnter charges only while the local player sits inside).
        // The plugin delays solidity only while the local player stands inside the car's bounds when it appears.
        public static CollisionMode Collision(CarPresentation p, VehicleRecord r, bool settled)
        {
            switch (p)
            {
                case CarPresentation.Real: return CollisionMode.Original;
                case CarPresentation.AwayProxy:
                case CarPresentation.ParkedOther:
                case CarPresentation.RemoteVisual:
                    return CollisionMode.Solid;
                default: return CollisionMode.None;
            }
        }
    }

    // M4.2.2 occupants. Seat roles are separate so a passenger seat can be added later without changing the driver path.
    public enum SeatRole : byte { None = 0, Driver = 1, Passenger = 2 }
    public enum SeatSource : byte { None = 0, MirroredAssociate = 1, TppCharacter = 2, SitTransform = 3, TppCalibrated = 4, AssociateSeat = 5 }

    // Car-local seat anchor for the remote character model (positions/rotations in the car's local space).
    public struct SeatSpec
    {
        public SeatSource Source;
        public Vec3 Pos;
        public Quat Rot;
        public bool CodenameRoot;    // anchor is for the Codename root (apply the anim child's local offset)
        public bool Valid { get { return Source != SeatSource.None; } }
        public override string ToString() { return Source + " local(" + Pos + ")"; }
    }

    public enum OccupantView : byte { Walking = 0, Seated = 1, Hidden = 2 }

    public static class SeatRules
    {
        // Driver anchor from the car's own seat transforms (verified fields, CODEBASE_MAP §3.7):
        //  1. CarController.associateSitPos is where CodenameAI puts the Codename root + Sitting clip (IL CodenameAI.Update).
        //     When it is the front passenger seat (same row as sit_transform, other side) its mirror image is the driver seat
        //     for the same model and clip.
        //  2. CarController.tpp_character: the original third-person driver model placed in the driver seat (Set_TPP).
        //  3. CarController.sit_transform: where the first-person player root is moved while seated (last resort).
        //  2b. M4.2.3: tpp_character height is a model reference, not a Codename seat (in game: the Ranger driver's head went
        //      through the roof). When some car has a front-row associate seat, the verified height of a seated Codename root
        //      below the first-person sit root ('codenameBelowSit' = associateSitPos.y - sit_transform.y; Shadowhawk: -0.52)
        //      is applied: anchor = (tpp.x, sit.y + codenameBelowSit, tpp.z), Codename-root semantics.
        public static SeatSpec Driver(bool hasAssoc, Vec3 assoc, Quat assocRot, bool hasSit, Vec3 sit, bool hasTpp, Vec3 tpp, Quat tppRot)
        {
            return Driver(hasAssoc, assoc, assocRot, hasSit, sit, hasTpp, tpp, tppRot, float.NaN);
        }

        public static bool FrontRowAssociate(bool hasAssoc, Vec3 assoc, bool hasSit, Vec3 sit)
        {
            return hasAssoc && hasSit && Math.Abs(assoc.Z - sit.Z) < 0.4f && assoc.X * sit.X < 0 && Math.Abs(assoc.X) > 0.15f;
        }

        public static float CodenameBelowSit(bool hasAssoc, Vec3 assoc, bool hasSit, Vec3 sit)
        {
            return FrontRowAssociate(hasAssoc, assoc, hasSit, sit) ? assoc.Y - sit.Y : float.NaN;
        }

        public static SeatSpec Driver(bool hasAssoc, Vec3 assoc, Quat assocRot, bool hasSit, Vec3 sit, bool hasTpp, Vec3 tpp, Quat tppRot, float codenameBelowSit)
        {
            if (FrontRowAssociate(hasAssoc, assoc, hasSit, sit))
                return new SeatSpec { Source = SeatSource.MirroredAssociate, Pos = new Vec3(-assoc.X, assoc.Y, assoc.Z), Rot = new Quat(assocRot.X, -assocRot.Y, -assocRot.Z, assocRot.W).Normalized(), CodenameRoot = true };
            bool tppOk = hasTpp && Math.Abs(tpp.X) + Math.Abs(tpp.Y) + Math.Abs(tpp.Z) > 0.2f;
            if (tppOk && hasSit && !float.IsNaN(codenameBelowSit))
                return new SeatSpec { Source = SeatSource.TppCalibrated, Pos = new Vec3(tpp.X, sit.Y + codenameBelowSit, tpp.Z), Rot = tppRot.Normalized(), CodenameRoot = true };
            if (tppOk)
                return new SeatSpec { Source = SeatSource.TppCharacter, Pos = tpp, Rot = tppRot.Normalized() };
            if (hasSit) return new SeatSpec { Source = SeatSource.SitTransform, Pos = sit, Rot = Quat.Identity };
            return default(SeatSpec);
        }

        // Manual calibration (BepInEx config Vehicles.DriverSeatAdjust): "carNr:dx,dy,dz;..." in car-local metres, each
        // component clamped to ±0.5 m. Invalid parts are ignored.
        public static Dictionary<int, Vec3> ParseAdjust(string s)
        {
            var d = new Dictionary<int, Vec3>();
            if (string.IsNullOrEmpty(s)) return d;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            foreach (var part in s.Split(';'))
            {
                var kv = part.Split(':');
                int nr; if (kv.Length != 2 || !int.TryParse(kv[0].Trim(), out nr) || nr < 0 || nr > VehicleIds.MaxCarNr) continue;
                var c = kv[1].Split(',');
                float x, y, z;
                if (c.Length != 3 || !float.TryParse(c[0].Trim(), System.Globalization.NumberStyles.Float, inv, out x) || !float.TryParse(c[1].Trim(), System.Globalization.NumberStyles.Float, inv, out y)
                    || !float.TryParse(c[2].Trim(), System.Globalization.NumberStyles.Float, inv, out z)) continue;
                Func<float, float> cl = v => float.IsNaN(v) ? 0f : Math.Max(-0.5f, Math.Min(0.5f, v));
                d[nr] = new Vec3(cl(x), cl(y), cl(z));
            }
            return d;
        }

        public static SeatSpec Adjust(SeatSpec s, Vec3 delta)
        {
            if (!s.Valid) return s;
            s.Pos = new Vec3(s.Pos.X + delta.X, s.Pos.Y + delta.Y, s.Pos.Z + delta.Z);
            return s;
        }

        public static SeatRole RoleOf(VehicleRecord r, byte slot)
        {
            if (slot == 0) return SeatRole.None;
            return r.Driver == slot ? SeatRole.Driver : r.Passenger == slot ? SeatRole.Passenger : SeatRole.None;
        }

        // The car (in 'region') whose seat the slot occupies according to the host's records.
        public static bool Find(IEnumerable<KeyValuePair<ushort, VehicleRecord>> fleet, byte slot, int region, out ushort id, out SeatRole role)
        {
            id = VehicleIds.None; role = SeatRole.None;
            if (region == Regions.None) return false;
            foreach (var kv in fleet)
            {
                if (kv.Value.Region != region) continue;
                var r = RoleOf(kv.Value, slot);
                if (r == SeatRole.None) continue;
                id = kv.Key; role = r; return true;
            }
            return false;
        }

        // Remote avatar: walking outside cars; seated (driver or passenger) when the host's record puts it into a car presented
        // here and the role's anchor exists; otherwise hidden (stolen/NPC cars are not synchronized).
        public static OccupantView Occupant(bool remoteInVehicle, bool seatFound, SeatRole role, bool anchorAvailable)
        {
            if (!remoteInVehicle) return OccupantView.Walking;
            return seatFound && (role == SeatRole.Driver || role == SeatRole.Passenger) && anchorAvailable ? OccupantView.Seated : OccupantView.Hidden;
        }

        // M4.3 passenger seat = CarController.associateSitPos, the seat the original companion uses: CodenameAI.Update puts the
        // Codename ROOT at associateSitPos position + rotation and cross-fades its sit clip (IL). Ranger: "Female Sit
        // Position" (rear bench centre); Shadowhawk: "Associate Sit" (front passenger). Remote passenger = same model, clip
        // and anchor semantics (verified original behaviour, not an estimate).
        public static SeatSpec Passenger(bool hasAssoc, Vec3 assoc, Quat assocRot)
        {
            if (!hasAssoc) return default(SeatSpec);
            return new SeatSpec { Source = SeatSource.AssociateSeat, Pos = assoc, Rot = assocRot, CodenameRoot = true };
        }

        // Local passenger (first person): the player ROOT height in a seat. The original driver root is sit_transform; the
        // seated Codename root sits 'codenameBelowSit' below it (learned from a car whose associate seat is in the front row,
        // Shadowhawk: -0.52 m). Player root = associate seat raised by that difference, x/z of the associate seat. Without
        // the calibration value: associate x/z at the car's own sit_transform height. Needs in-game calibration (roof/eye
        // height on the Ranger rear bench) -> config Vehicles.PassengerSeatAdjust.
        public static Vec3 PassengerRoot(Vec3 assoc, bool hasSit, Vec3 sit, float codenameBelowSit)
        {
            if (!float.IsNaN(codenameBelowSit)) return new Vec3(assoc.X, assoc.Y - codenameBelowSit, assoc.Z);
            return new Vec3(assoc.X, hasSit ? sit.Y : assoc.Y, assoc.Z);
        }
    }

    // Detects per-vehicle changes of region/holder between registry views (the receiver then forgets the vehicle's last
    // snapshot sequence: a new holder or a holder after a scene reload starts a fresh sequence). Pure.
    public sealed class FleetChangeTracker
    {
        readonly Dictionary<ushort, long> _last = new Dictionary<ushort, long>();
        public List<ushort> Changed(IEnumerable<KeyValuePair<ushort, VehicleRecord>> records)
        {
            List<ushort> changed = null;
            foreach (var kv in records)
            {
                long key = ((long)kv.Value.Region << 8) | kv.Value.Holder;
                long old;
                if (_last.TryGetValue(kv.Key, out old) && old == key) continue;
                bool known = _last.ContainsKey(kv.Key);
                _last[kv.Key] = key;
                if (known) (changed ?? (changed = new List<ushort>())).Add(kv.Key);
            }
            return changed;
        }
        public void Clear() { _last.Clear(); }
    }

    // Last known parking state of one car (registry key "vpark", Session scope, arg = vehicle id). 29 bytes.
    public struct ParkRecord
    {
        public int Region;           // region the pose belongs to (a reserved car keeps its origin pose until arrival)
        public bool HasPose;
        public Vec3 Pos;
        public Quat Rot;
        public float Fuel;           // < 0 = unknown
        public const int WireSize = 29;

        static short Q(float v) { return (short)Math.Round(Math.Max(-1f, Math.Min(1f, v)) * 32767f); }

        public byte[] ToBytes()
        {
            var w = new NetDataWriter(); Write(w); return w.CopyData();
        }

        public void Write(NetDataWriter w)
        {
            var r = HasPose ? Rot.Normalized() : Quat.Identity;
            w.Put(Region); w.Put(HasPose); w.Put(Pos.X); w.Put(Pos.Y); w.Put(Pos.Z);
            w.Put(Q(r.X)); w.Put(Q(r.Y)); w.Put(Q(r.Z)); w.Put(Q(r.W)); w.Put(Fuel);
        }

        public static bool TryRead(NetDataReader r, out ParkRecord p)
        {
            p = default(ParkRecord);
            if (r.AvailableBytes < WireSize) return false;
            p.Region = r.GetInt(); p.HasPose = r.GetBool();
            p.Pos = new Vec3(r.GetFloat(), r.GetFloat(), r.GetFloat());
            float x = r.GetShort() / 32767f, y = r.GetShort() / 32767f, z = r.GetShort() / 32767f, ww = r.GetShort() / 32767f;
            p.Rot = new Quat(x, y, z, ww).Normalized();
            p.Fuel = r.GetFloat();
            return p.IsValid;
        }

        public static bool TryParse(byte[] b, out ParkRecord p)
        {
            p = default(ParkRecord);
            return b != null && b.Length == WireSize && TryRead(new NetDataReader(b), out p);
        }

        static bool Finite(float f) { return !float.IsNaN(f) && !float.IsInfinity(f); }

        public bool IsValid
        {
            get
            {
                if (Region != Regions.None && !Regions.IsKnown(Region)) return false;
                if (!Finite(Fuel) || Fuel > TravelRules.MaxFuel || (Fuel < 0 && Fuel != -1f)) return false;
                if (!HasPose) return true;
                return Finite(Pos.X) && Finite(Pos.Y) && Finite(Pos.Z) && Math.Abs(Pos.X) <= SnapshotValidation.MaxCoordinate
                    && Math.Abs(Pos.Y) <= SnapshotValidation.MaxCoordinate && Math.Abs(Pos.Z) <= SnapshotValidation.MaxCoordinate;
            }
        }

        public override string ToString()
        {
            return Regions.Name(Region) + (HasPose ? string.Format(" ({0:0.0},{1:0.0},{2:0.0})", Pos.X, Pos.Y, Pos.Z) : " (no pose)") + (Fuel >= 0 ? " fuel=" + Fuel.ToString("0.0") : " fuel=?");
        }
    }

    public enum TravelOutcome : byte
    {
        None = 0,      // arrive on foot, no permanent car brought or assigned to this traveller
        Carry = 1,     // the traveller's car moves to the (empty) destination with them (original travel behaviour)
        Join = 2,      // destination car is held by the other player: arrive on foot next to it
        Parked = 3,    // destination car is parked with nobody there: the traveller will simulate it where it was left
        Special = 4    // mission/special destination: original game behaviour, fleet untouched
    }

    // Client -> host proposal value (key "travel", Player scope, arg = sender slot; proposal id = request id). 35 bytes.
    public struct TravelRequest
    {
        public int Dest;
        public ushort Car;           // car the traveller would bring (sitting in / holding); 0 = none
        public ParkRecord Pose;      // final pose + fuel of that car when the traveller holds it (HasPose)

        public byte[] ToBytes() { var w = new NetDataWriter(); w.Put(Dest); w.Put(Car); Pose.Write(w); return w.CopyData(); }

        public static bool TryParse(byte[] b, out TravelRequest t)
        {
            t = default(TravelRequest);
            if (b == null || b.Length != 6 + ParkRecord.WireSize) return false;
            var r = new NetDataReader(b);
            t.Dest = r.GetInt(); t.Car = r.GetUShort();
            return ParkRecord.TryRead(r, out t.Pose);
        }
    }

    // Host decision, committed as Player-scope entry "travelGrant" (arg = slot) before the proposal result. 11 bytes.
    public struct TravelGrant
    {
        public uint Id;
        public int Dest;
        public TravelOutcome Outcome;
        public ushort Car;

        public byte[] ToBytes() { var w = new NetDataWriter(); w.Put(Id); w.Put(Dest); w.Put((byte)Outcome); w.Put(Car); return w.CopyData(); }

        public static bool TryParse(byte[] b, out TravelGrant g)
        {
            g = default(TravelGrant);
            if (b == null || b.Length != 11) return false;
            g.Id = BitConverter.ToUInt32(b, 0); g.Dest = BitConverter.ToInt32(b, 4); g.Outcome = (TravelOutcome)b[8]; g.Car = BitConverter.ToUInt16(b, 9);
            return (byte)g.Outcome <= (byte)TravelOutcome.Special && Regions.IsKnown(g.Dest);
        }

        public override string ToString() { return "#" + Id + " -> " + Regions.Name(Dest) + " " + Outcome + (Car != 0 ? " car#" + Car : ""); }
    }

    // Host-side reservations (pure). All decisions run on the host's main thread, one at a time.
    public sealed class TravelDesk
    {
        sealed class Res
        {
            public byte Slot; public uint Id; public int Dest, Origin; public TravelOutcome Outcome; public ushort Car;
            public int PrevRegion; public bool Departed; public double At;
        }

        readonly StateRegistry _reg;
        readonly FleetBook _fleet;
        readonly Dictionary<byte, Res> _res = new Dictionary<byte, Res>();
        public readonly List<string> Notes = new List<string>();   // decisions/commits/rollbacks for the log (bounded)
        public long Decisions, Commits, Rollbacks;

        public TravelDesk(StateRegistry reg, FleetBook fleet) { _reg = reg; _fleet = fleet; }

        void Note(string n) { if (Notes.Count < 32) Notes.Add(n); }
        public int Pending { get { return _res.Count; } }
        public bool HasReservation(byte slot) { return _res.ContainsKey(slot); }

        public bool Carriable(ushort car, byte slot, int origin, Func<byte, int> regionOf)
        {
            VehicleRecord r;
            if (car == VehicleIds.None || !_fleet.TryGet(car, out r) || !r.Unlocked || r.Reserved) return false;
            if (origin == Regions.None || r.Region != origin || !TravelRules.IsFleetRegion(origin)) return false;
            if ((r.Holder != 0 && r.Holder != slot) || (r.Driver != 0 && r.Driver != slot)) return false;
            if (r.Driver == slot) return true;   // M4.2.3 rule A: the confirmed driver takes the car, whoever stays behind
            byte other = (byte)(slot == 1 ? 2 : 1);
            return !TravelRules.OneCarRegion(origin) || regionOf(other) != origin;   // rule B: on foot, the region car stays with the player who stays
        }

        // M4.2.3 travel intent (host side, for the log and tests). The intent is the host-committed driver lease, which the
        // traveller's instance claims from the original CarController.is_sitting_inside + PlayerScript.current_vehicle and
        // releases on exit; client lease/release proposals share the travel request's ordered id sequence, so the lease seen
        // here is the state at the click. Never inferred from CurrentCar, the holder, region occupancy or a previous driver.
        public static string Intent(VehicleRecord r, byte slot) { return r.Driver == slot ? "driving" : "on foot"; }

        // One serialized decision. 'origin' = the traveller's committed region on the host.
        public TravelGrant Decide(byte slot, uint id, TravelRequest req, int origin, Func<byte, int> regionOf, double now, List<StateEntry> outbox)
        {
            Decisions++;
            Res old;
            if (_res.TryGetValue(slot, out old)) Rollback(old, "superseded by request #" + id, outbox);
            ApplyPose(slot, origin, req, outbox);
            VehicleRecord intentRec; string intent = req.Car != VehicleIds.None && _fleet.TryGet(req.Car, out intentRec) ? Intent(intentRec, slot) : "no car";
            var g = new TravelGrant { Id = id, Dest = req.Dest, Outcome = TravelOutcome.None };
            string why;
            if (!TravelRules.IsFleetRegion(req.Dest)) { g.Outcome = TravelOutcome.Special; why = "special destination (original behaviour)"; }
            else if (!TravelRules.OneCarRegion(req.Dest))
            {
                if (Carriable(req.Car, slot, origin, regionOf)) { Reserve(slot, id, req.Dest, origin, TravelOutcome.Carry, req.Car, now, outbox); g.Outcome = TravelOutcome.Carry; g.Car = req.Car; why = "car stored in the garage"; }
                else why = req.Car == VehicleIds.None ? "no car" : "car #" + req.Car + " not available to carry";
            }
            else
            {
                ushort x = _fleet.AssignedTo(req.Dest);
                VehicleRecord xr;
                if (x != VehicleIds.None && _fleet.TryGet(x, out xr))
                {
                    if (xr.Holder != 0 && xr.Holder != slot) { g.Outcome = TravelOutcome.Join; g.Car = x; why = "destination car held by P" + xr.Holder + (xr.Reserved ? " (reserved)" : ""); }
                    else if (!xr.Unlocked) why = "destination car locked";
                    else { Reserve(slot, id, req.Dest, origin, TravelOutcome.Parked, x, now, outbox); g.Outcome = TravelOutcome.Parked; g.Car = x; why = "destination car parked there"; }
                }
                else if (Carriable(req.Car, slot, origin, regionOf)) { Reserve(slot, id, req.Dest, origin, TravelOutcome.Carry, req.Car, now, outbox); g.Outcome = TravelOutcome.Carry; g.Car = req.Car; why = "empty destination"; }
                else why = req.Car == VehicleIds.None ? "empty destination, no car" : "empty destination, car #" + req.Car + " stays (not available to carry)";
            }
            HandOverStaying(slot, origin, g, regionOf, outbox);
            var e = _reg.Commit(StateScope.Player, slot, WorldKeys.TravelGrant, g.ToBytes());
            if (e != null && outbox != null) outbox.Add(e);
            Note("P" + slot + " " + g + " from " + Regions.Name(origin) + " (" + intent + "): " + why);
            return g;
        }

        void ApplyPose(byte slot, int origin, TravelRequest req, List<StateEntry> outbox)
        {
            VehicleRecord r;
            if (!req.Pose.HasPose || req.Car == VehicleIds.None || !_fleet.TryGet(req.Car, out r)) return;
            if (r.Holder != slot || r.Reserved || r.Region != origin || origin == Regions.None) return;   // only the simulating player reports a pose
            var p = req.Pose; p.Region = origin;
            _fleet.SetPark(req.Car, p, outbox);
        }

        // M4.2.2: a car the traveller simulates that stays behind (not carried) while the other player is in that region is
        // handed to the other player at decision time, at the pose the traveller just reported (ApplyPose). Before, it kept
        // its old holder until the traveller's scene unloaded, so the observer saw it frozen (possibly mid-air at a map
        // exit) for seconds and then jump to an older parking pose.
        void HandOverStaying(byte slot, int origin, TravelGrant g, Func<byte, int> regionOf, List<StateEntry> outbox)
        {
            if (origin == Regions.None || !TravelRules.IsFleetRegion(origin)) return;
            byte other = (byte)(slot == 1 ? 2 : 1);
            if (regionOf(other) != origin) return;
            foreach (var id in _fleet.VehiclesIn(origin))
            {
                VehicleRecord r;
                if (!_fleet.TryGet(id, out r) || r.Reserved || r.Holder != slot || !r.Unlocked) continue;
                if (g.Outcome == TravelOutcome.Carry && g.Car == id) continue;
                r.Holder = other; r.Driver = 0; r.Flags &= ~VehicleFlags.EngineOn;
                if (_fleet.Set(id, r, outbox)) Note("car#" + id + " stays in " + Regions.Name(origin) + ": now simulated by P" + other + " (P" + slot + " leaves)");
            }
        }

        void Reserve(byte slot, uint id, int dest, int origin, TravelOutcome o, ushort car, double now, List<StateEntry> outbox)
        {
            VehicleRecord r;
            if (!_fleet.TryGet(car, out r)) return;
            _res[slot] = new Res { Slot = slot, Id = id, Dest = dest, Origin = origin, Outcome = o, Car = car, PrevRegion = r.Region, At = now };
            r.Region = dest; r.Holder = slot; r.Driver = 0; r.Flags = (r.Flags | VehicleFlags.Reserved) & ~VehicleFlags.EngineOn;
            _fleet.Set(car, r, outbox);
        }

        // Committed region for persistence: a car reserved by a travel in progress is stored where it was before.
        public int CommittedRegion(ushort car, int recordRegion)
        {
            foreach (var r in _res.Values) if (r.Car == car && r.Outcome == TravelOutcome.Carry) return r.PrevRegion;
            return recordRegion;
        }

        public bool OnPlayerRegion(byte slot, int region, List<StateEntry> outbox)
        {
            Res r;
            if (!_res.TryGetValue(slot, out r)) return false;
            if (region == Regions.None) { r.Departed = true; return false; }
            if (region == r.Dest) { Commit(r, outbox); return true; }
            if (r.Departed) { Rollback(r, "arrived in " + Regions.Name(region) + " instead of " + Regions.Name(r.Dest), outbox); return true; }
            return false;   // still in the origin, the reload has not started yet
        }

        void Commit(Res r, List<StateEntry> outbox)
        {
            _res.Remove(r.Slot);
            VehicleRecord v;
            if (_fleet.TryGet(r.Car, out v) && v.Reserved) { v.Flags &= ~VehicleFlags.Reserved; _fleet.Set(r.Car, v, outbox); }
            Commits++;
            Note("P" + r.Slot + " arrived in " + Regions.Name(r.Dest) + ": reservation #" + r.Id + " committed (" + r.Outcome + " car#" + r.Car + ")");
        }

        void Rollback(Res r, string why, List<StateEntry> outbox)
        {
            _res.Remove(r.Slot);
            VehicleRecord v;
            if (_fleet.TryGet(r.Car, out v) && v.Reserved && v.Holder == r.Slot)
            {
                if (r.Outcome == TravelOutcome.Carry) v.Region = r.PrevRegion;
                v.Holder = 0; v.Driver = 0; v.Flags &= ~(VehicleFlags.Reserved | VehicleFlags.EngineOn);
                _fleet.Set(r.Car, v, outbox);
            }
            Rollbacks++;
            Note("P" + r.Slot + " reservation #" + r.Id + " rolled back (" + why + "): car#" + r.Car + " stays in " + Regions.Name(r.Outcome == TravelOutcome.Carry ? r.PrevRegion : r.Dest));
        }

        public ProposalCode Cancel(byte slot, uint id, List<StateEntry> outbox)
        {
            Res r;
            if (!_res.TryGetValue(slot, out r) || r.Id != id) return ProposalCode.Unchanged;
            Rollback(r, "cancelled by the traveller", outbox);
            return ProposalCode.Accepted;
        }

        public bool Abort(byte slot, string why, List<StateEntry> outbox)
        {
            Res r;
            if (!_res.TryGetValue(slot, out r)) return false;
            Rollback(r, why, outbox);
            return true;
        }

        public int Expire(double now, List<StateEntry> outbox)
        {
            List<Res> old = null;
            foreach (var r in _res.Values) if (now - r.At > TravelRules.ReservationTimeout) (old ?? (old = new List<Res>())).Add(r);
            if (old == null) return 0;
            foreach (var r in old) Rollback(r, "no arrival within " + TravelRules.ReservationTimeout + " s", outbox);
            return old.Count;
        }
    }

    // Client-side state of one held travel click (pure). The menu stays open, nothing is saved or loaded while pending.
    public enum GateAction { Ignore, Resume, Denied, CancelLate }

    public sealed class TravelGate
    {
        public double ResultTimeout = 15;    // failsafe only: the reliable channel delivers the result while connected
        public uint PendingId { get; private set; }
        public int PendingDest { get; private set; }
        uint _abandoned;
        double _at;

        public bool Pending { get { return PendingId != 0; } }

        public bool Begin(uint id, int dest, double now)
        {
            if (Pending || id == 0) return false;
            PendingId = id; PendingDest = dest; _at = now;
            return true;
        }

        public GateAction OnResult(uint id, bool accepted)
        {
            if (id != 0 && id == PendingId) { PendingId = 0; return accepted ? GateAction.Resume : GateAction.Denied; }
            if (id != 0 && id == _abandoned) { _abandoned = 0; return accepted ? GateAction.CancelLate : GateAction.Ignore; }
            return GateAction.Ignore;
        }

        public bool CheckTimeout(double now)
        {
            if (!Pending || now - _at <= ResultTimeout) return false;
            Abandon();
            return true;
        }

        // Gives up the pending click locally (timeout, menu closed, scene change); a late acceptance must be cancelled.
        public uint Abandon()
        {
            uint id = PendingId;
            if (id != 0) _abandoned = id;
            PendingId = 0;
            return id;
        }
    }

    // One-shot continuation guard for resuming an intercepted original method exactly once.
    public sealed class OneShot
    {
        object _armed;
        public bool Armed { get { return _armed != null; } }
        public void Arm(object key) { _armed = key; }
        public void Disarm() { _armed = null; }
        public bool TryConsume(object key)
        {
            if (_armed == null || !_armed.Equals(key)) return false;
            _armed = null;
            return true;
        }
    }

    // ---- persistent fleet record (host only) ----
    public sealed class FleetSaveCar
    {
        public ushort Id; public byte Kind; public bool Unlocked; public int Region; public ParkRecord Park;
    }

    public sealed class FleetSave
    {
        public const int Version = 1;
        static readonly byte[] Magic = Encoding.ASCII.GetBytes("TS2MPFLT");
        public int Profile;          // PlayerPrefs "Profile" (InitializePrefs.Reinitialize: 0 -> saveData.thief, 1..4 -> saveData2..5)
        public int Story;            // SPrefs "Story" when written
        public int DayIndex;         // GameManager.currentWeek * 7 + currentDayInt when written
        public long SavedUtcTicks;
        public readonly List<FleetSaveCar> Cars = new List<FleetSaveCar>();

        public byte[] Serialize()
        {
            var w = new NetDataWriter();
            w.Put(Magic); w.Put((ushort)Version); w.Put(Profile); w.Put(Story); w.Put(DayIndex); w.Put(SavedUtcTicks);
            w.Put((byte)Cars.Count);
            foreach (var c in Cars) { w.Put(c.Id); w.Put(c.Kind); w.Put(c.Unlocked); w.Put(c.Region); c.Park.Write(w); }
            var body = w.CopyData();
            var all = new byte[body.Length + 4];
            Buffer.BlockCopy(body, 0, all, 0, body.Length);
            Buffer.BlockCopy(BitConverter.GetBytes(Fnv(body, body.Length)), 0, all, body.Length, 4);
            return all;
        }

        static uint Fnv(byte[] b, int n) { uint h = 2166136261; for (int i = 0; i < n; i++) { h ^= b[i]; h *= 16777619; } return h; }

        public static bool TryParse(byte[] b, out FleetSave s, out string error)
        {
            s = null; error = null;
            const int header = 8 + 2 + 4 + 4 + 4 + 8 + 1;
            if (b == null || b.Length < header + 4) { error = "too short"; return false; }
            if (BitConverter.ToUInt32(b, b.Length - 4) != Fnv(b, b.Length - 4)) { error = "checksum mismatch"; return false; }
            for (int i = 0; i < 8; i++) if (b[i] != Magic[i]) { error = "bad magic"; return false; }
            var r = new NetDataReader(b, 8, b.Length - 4);
            ushort ver = r.GetUShort();
            if (ver != Version) { error = "unsupported version " + ver; return false; }
            var x = new FleetSave { Profile = r.GetInt(), Story = r.GetInt(), DayIndex = r.GetInt(), SavedUtcTicks = r.GetLong() };
            int n = r.GetByte();
            if (n > FleetBook.MaxVehicles || r.AvailableBytes != n * (2 + 1 + 1 + 4 + ParkRecord.WireSize)) { error = "bad vehicle count/length"; return false; }
            var seen = new HashSet<ushort>();
            var oneCar = new HashSet<int>();
            for (int i = 0; i < n; i++)
            {
                var c = new FleetSaveCar { Id = r.GetUShort(), Kind = r.GetByte(), Unlocked = r.GetBool(), Region = r.GetInt() };
                if (!ParkRecord.TryRead(r, out c.Park)) { error = "invalid parking record"; return false; }
                if (c.Id == VehicleIds.None || VehicleIds.ToCarNr(c.Id) != c.Kind || !seen.Add(c.Id)) { error = "invalid or duplicate vehicle id"; return false; }
                if (c.Region != Regions.None && !Regions.IsKnown(c.Region)) { error = "unknown region " + c.Region; return false; }
                if (TravelRules.OneCarRegion(c.Region) && !oneCar.Add(c.Region)) { error = "two cars in " + Regions.Name(c.Region); return false; }
                x.Cars.Add(c);
            }
            s = x;
            return true;
        }

        // Heuristic guard against applying a fleet record to a different game history in the same profile slot (new game
        // or an older backup restored): the game's Story and day counter only grow during play.
        public string StaleReason(int profile, int story, int dayIndex)
        {
            if (profile != Profile) return "profile " + Profile + " != " + profile;
            if (story < Story) return "save story " + story + " < recorded " + Story;
            if (dayIndex + 1 < DayIndex) return "save day " + dayIndex + " < recorded " + DayIndex;
            return null;
        }
    }

    public sealed class FleetStore
    {
        public readonly string Path;
        public FleetStore(string dir, int profile) { Path = System.IO.Path.Combine(dir, "fleet-" + profile + ".dat"); }
        public FleetStore(string fullPath) { Path = fullPath; }   // M4.3.3: same atomic write/quarantine for other host files (p2inv)

        // Main file, else the previous version kept by the atomic replace (crash during a write / corrupted file).
        public bool TryLoad(out FleetSave s, out string info)
        {
            s = null; info = null;
            string err1 = "missing", err2 = "missing";
            if (File.Exists(Path) && FleetSave.TryParse(File.ReadAllBytes(Path), out s, out err1)) { info = "loaded"; return true; }
            var bak = Path + ".bak";
            if (File.Exists(bak) && FleetSave.TryParse(File.ReadAllBytes(bak), out s, out err2)) { info = "main file " + err1 + ", loaded backup"; return true; }
            info = File.Exists(Path) || File.Exists(bak) ? "unusable (main: " + err1 + ", backup: " + err2 + ")" : "missing";
            return false;
        }

        // temp file + flush + atomic replace (previous version kept as .bak)
        public void WriteAtomic(byte[] data)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path));
            var tmp = Path + ".tmp";
            using (var f = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None)) { f.Write(data, 0, data.Length); f.Flush(true); }
            if (File.Exists(Path)) File.Replace(tmp, Path, Path + ".bak", true);
            else File.Move(tmp, Path);
        }

        // Keeps an unusable/stale record for inspection instead of deleting it.
        public string Quarantine(string tag)
        {
            if (!File.Exists(Path)) return null;
            var dst = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path), System.IO.Path.GetFileNameWithoutExtension(Path) + "." + tag + "-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".dat");
            File.Move(Path, dst);
            if (File.Exists(Path + ".bak")) File.Delete(Path + ".bak");
            return dst;
        }
    }

    // Coalescing background writer: the main thread hands over a byte[]; at most one write runs at a time and only the
    // newest pending payload is written. Flush() waits (shutdown only).
    public sealed class FleetWriter
    {
        readonly FleetStore _store;
        readonly object _lock = new object();
        byte[] _pending; bool _running;
        public long Writes, Failures;
        public string LastError;
        public FleetWriter(FleetStore store) { _store = store; }

        public void Queue(byte[] data)
        {
            lock (_lock) { _pending = data; if (_running) return; _running = true; }
            ThreadPool.QueueUserWorkItem(Work);
        }

        void Work(object state)
        {
            while (true)
            {
                byte[] d;
                lock (_lock) { d = _pending; _pending = null; if (d == null) { _running = false; Monitor.PulseAll(_lock); return; } }
                try { _store.WriteAtomic(d); Interlocked.Increment(ref Writes); }
                catch (Exception ex) { Interlocked.Increment(ref Failures); LastError = ex.Message; }
            }
        }

        public bool Flush(int timeoutMs)
        {
            lock (_lock) { while (_running) if (!Monitor.Wait(_lock, timeoutMs)) return false; }
            return true;
        }
    }

    // When a held car's parking record is committed (host): state flags change -> at once (the moment it stops /
    // the engine goes off), otherwise at most every MinInterval and only after a real change. Bounded registry traffic
    // while driving, a fresh pose for takeover/persistence once parked. Pure.
    public sealed class ParkPolicy
    {
        public double MinInterval = 2.0;
        public float MoveEpsilon = 0.5f, AngleEpsilon = 5f, FuelEpsilon = 0.5f;
        double _last = -1; Vec3 _pos; Quat _rot; float _fuel; byte _flags; bool _has;

        public bool ShouldCommit(double now, Vec3 pos, Quat rot, float fuel, byte flags)
        {
            bool flagChange = !_has || flags != _flags;
            bool changed = !_has || Vec3.Distance(pos, _pos) > MoveEpsilon || Quat.AngleDegrees(rot, _rot) > AngleEpsilon || Math.Abs(fuel - _fuel) > FuelEpsilon;
            if (!flagChange && (!changed || now - _last < MinInterval)) return false;
            _last = now; _pos = pos; _rot = rot; _fuel = fuel; _flags = flags; _has = true;
            return true;
        }

        public void Reset() { _has = false; _last = -1; }
    }

    // Candidate positions around an anchor for safe player placement (pure; the plugin applies the Unity checks).
    public static class SpawnPlanner
    {
        public static readonly float[] Radii = { 2.0f, 3.0f, 4.5f };
        public const int Angles = 8;

        public static void Ring(Vec3 anchor, float startDeg, List<Vec3> output)
        {
            foreach (var rad in Radii)
                for (int i = 0; i < Angles; i++)
                {
                    double a = (startDeg + i * 360.0 / Angles) * Math.PI / 180.0;
                    output.Add(new Vec3(anchor.X + (float)(Math.Sin(a) * rad), anchor.Y, anchor.Z + (float)(Math.Cos(a) * rad)));
                }
        }

        public static int Choose(List<Vec3> candidates, Func<Vec3, bool> safe)
        {
            for (int i = 0; i < candidates.Count; i++) if (safe(candidates[i])) return i;
            return -1;
        }
    }
}

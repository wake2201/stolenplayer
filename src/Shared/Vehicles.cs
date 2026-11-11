// M4.1/M4.2: shared player-vehicle fleet (host-authoritative). Verified game facts: CODEBASE_MAP §3.7, §3.9.
// Identity = the game's own persistent car number (CarController.carNr, saved as SPrefs "CurrentCar"; VehiclesManager
// maps 0 -> firstCar "PLAYER CAR"/Ranger, 1 -> field 'van' "MUSTANG"/Shadowhawk GT). Nothing here assumes a fleet size.
// Low-rate fleet state lives in the existing StateRegistry (Session scope, key "vehicle", arg = vehicle id): revisioned,
// snapshot on (re)connect, ordered deltas. Movement uses unreliable sequenced VehicleState snapshots on their own channel.
using System;
using System.Collections.Generic;
using LiteNetLib.Utils;

namespace TS2MP.Shared
{
    public static class VehicleIds
    {
        public const ushort None = 0;
        public const int MaxCarNr = 254;   // kind is stored as a byte
        public static ushort FromCarNr(int carNr) { return carNr >= 0 && carNr <= MaxCarNr ? (ushort)(carNr + 1) : None; }
        public static int ToCarNr(ushort id) { return id == None ? -1 : id - 1; }
    }

    [Flags]
    // PassengerPending (M4.3): the passenger seat is reserved for an entry in progress (host timeout). Tpp (M4.3): the driver
    // uses the original third-person vehicle camera (CarController.Set_TPP), followed by the passenger's camera MODE.
    public enum VehicleFlags : byte { None = 0, Unlocked = 1, Reserved = 2, EngineOn = 4, PassengerPending = 8, Tpp = 16 }

    // Registry value (9 bytes, M4.2). Region = the car's one authoritative region (the destination while Reserved).
    // Holder = the instance whose game has the physical car spawned and simulates it (0 = parked with nobody in its
    // region: the car exists only logically). Reserved = a travel decision moved/assigned it and the traveller has not
    // arrived yet (host memory keeps the rollback data; the persistent fleet file stores the pre-reservation state).
    public struct VehicleRecord
    {
        public byte Kind;            // carNr
        public VehicleFlags Flags;
        public int Region;           // Regions id; Regions.None = not placed yet
        public byte Driver;          // slot, 0 = none; a driver is always the holder (M4.2.2: the seat passes via FleetBook.TakeOver)
        public byte Passenger;       // slot, 0 = none (reserved for the passenger milestone)
        public byte Holder;          // slot, 0 = none
        public const int WireSize = 9;

        public bool Unlocked { get { return (Flags & VehicleFlags.Unlocked) != 0; } }
        public bool Reserved { get { return (Flags & VehicleFlags.Reserved) != 0; } }
        public bool PassengerPending { get { return (Flags & VehicleFlags.PassengerPending) != 0; } }
        public bool Tpp { get { return (Flags & VehicleFlags.Tpp) != 0; } }
        public bool Active { get { return Holder != 0; } }

        public byte[] ToBytes() { var w = new NetDataWriter(); w.Put(Kind); w.Put((byte)Flags); w.Put(Region); w.Put(Driver); w.Put(Passenger); w.Put(Holder); return w.CopyData(); }

        public static bool TryParse(byte[] b, out VehicleRecord r)
        {
            r = default(VehicleRecord);
            if (b == null || b.Length != WireSize) return false;
            r.Kind = b[0]; r.Flags = (VehicleFlags)b[1]; r.Region = BitConverter.ToInt32(b, 2); r.Driver = b[6]; r.Passenger = b[7]; r.Holder = b[8];
            return ((byte)r.Flags & 0xE0) == 0 && r.Driver <= 2 && r.Passenger <= 2 && r.Holder <= 2 && (r.Driver == 0 || r.Driver != r.Passenger)
                && (r.Region == Regions.None || Regions.IsKnown(r.Region));
        }

        public override string ToString()
        {
            return "kind=" + Kind + (Unlocked ? "" : " locked") + " @" + Regions.Name(Region) + (Reserved ? " reserved" : "")
                + (Holder != 0 ? " holder=P" + Holder : " parked") + ((Flags & VehicleFlags.EngineOn) != 0 ? " engine" : "")
                + (Passenger != 0 ? " passenger=P" + Passenger + (PassengerPending ? "(entering)" : "") : "") + (Tpp ? " tpp" : "")
                + (Driver != 0 ? " driver=P" + Driver : "") + (Passenger != 0 ? " passenger=P" + Passenger : "");
        }
    }

    // Host-only fleet bookkeeping over the StateRegistry. Pure.
    // M4.2.2 terms: identity = the record key (never deleted when a driver leaves); driver seat = Driver (empty/occupied);
    // simulation authority = Holder (may stay with a player while the car is parked); presentation = derived per instance.
    public sealed class FleetBook
    {
        public const int MaxVehicles = 64;
        public const double TakeLockSeconds = 5.0;   // after a granted handoff only the new holder may claim/take for this long
        readonly StateRegistry _reg;
        readonly Dictionary<ushort, KeyValuePair<byte, double>> _takeLock = new Dictionary<ushort, KeyValuePair<byte, double>>();
        public FleetBook(StateRegistry reg) { _reg = reg; }

        public bool TryGet(ushort id, out VehicleRecord r)
        {
            r = default(VehicleRecord);
            var e = id == VehicleIds.None ? null : _reg.Get(StateScope.Session, id, WorldKeys.Vehicle);
            return e != null && VehicleRecord.TryParse(e.Value, out r);
        }

        public bool TryGetPark(ushort id, out ParkRecord p)
        {
            p = default(ParkRecord);
            var e = id == VehicleIds.None ? null : _reg.Get(StateScope.Session, id, WorldKeys.VehiclePark);
            return e != null && ParkRecord.TryParse(e.Value, out p);
        }

        public List<ushort> Ids()
        {
            var l = new List<ushort>();
            foreach (var e in _reg.Entries) if (e.Scope == StateScope.Session && e.Key == WorldKeys.Vehicle && e.ScopeArg > 0 && e.ScopeArg <= ushort.MaxValue) l.Add((ushort)e.ScopeArg);
            l.Sort();
            return l;
        }

        public int Count { get { return Ids().Count; } }

        // Every vehicle whose authoritative region is 'region' (reserved ones included).
        public List<ushort> VehiclesIn(int region)
        {
            var l = new List<ushort>();
            foreach (var id in Ids()) { VehicleRecord r; if (TryGet(id, out r) && r.Region == region && region != Regions.None) l.Add(id); }
            return l;
        }

        // The single permanent car assigned to a one-car region (None if empty). The hideout garage may hold several.
        public ushort AssignedTo(int region)
        {
            var l = VehiclesIn(region);
            return l.Count > 0 ? l[0] : VehicleIds.None;
        }

        bool Put(ushort id, VehicleRecord r, List<StateEntry> outbox)
        {
            var e = _reg.Commit(StateScope.Session, id, WorldKeys.Vehicle, r.ToBytes());
            if (e != null && outbox != null) outbox.Add(e);
            return e != null;
        }

        public bool Set(ushort id, VehicleRecord r, List<StateEntry> outbox) { VehicleRecord cur; return TryGet(id, out cur) && cur.Kind == r.Kind && Put(id, r, outbox); }

        // Registers (or refreshes) a vehicle discovered in the host's game. A known id must keep its kind (same physical car).
        public ProposalCode Register(ushort id, byte kind, bool unlocked, List<StateEntry> outbox)
        {
            if (id == VehicleIds.None) return ProposalCode.Invalid;
            VehicleRecord r;
            if (TryGet(id, out r))
            {
                if (r.Kind != kind) return ProposalCode.Invalid;           // duplicate identity with a different car
                if (r.Unlocked == unlocked) return ProposalCode.Unchanged;
                r.Flags = unlocked ? r.Flags | VehicleFlags.Unlocked : r.Flags & ~VehicleFlags.Unlocked;
                return Put(id, r, outbox) ? ProposalCode.Accepted : ProposalCode.Unchanged;
            }
            if (Ids().Count >= MaxVehicles) return ProposalCode.Invalid;
            return Put(id, new VehicleRecord { Kind = kind, Flags = unlocked ? VehicleFlags.Unlocked : VehicleFlags.None, Region = Regions.None }, outbox) ? ProposalCode.Accepted : ProposalCode.Unchanged;
        }

        // Initial placement (fleet file or the host's own save). Refuses to put a second car into a one-car region.
        public bool Place(ushort id, int region, List<StateEntry> outbox)
        {
            VehicleRecord r;
            if (!TryGet(id, out r) || (region != Regions.None && !Regions.IsKnown(region))) return false;
            if (region != Regions.None && TravelRules.OneCarRegion(region)) { var other = AssignedTo(region); if (other != VehicleIds.None && other != id) return false; }
            r.Region = region; r.Holder = 0; r.Driver = 0; r.Passenger = 0; r.Flags &= VehicleFlags.Unlocked;
            Put(id, r, outbox);
            return true;
        }

        public bool SetPark(ushort id, ParkRecord p, List<StateEntry> outbox)
        {
            VehicleRecord r;
            if (!TryGet(id, out r) || !p.IsValid) return false;
            var e = _reg.Commit(StateScope.Session, id, WorldKeys.VehiclePark, p.ToBytes());
            if (e != null && outbox != null) outbox.Add(e);
            return e != null;
        }

        // M4.3: the driver's camera mode (reported by the holder: host physics / client snapshot flag).
        public bool SetTpp(ushort id, bool on, List<StateEntry> outbox)
        {
            VehicleRecord r;
            if (!TryGet(id, out r) || r.Tpp == on) return false;
            r.Flags = on ? r.Flags | VehicleFlags.Tpp : r.Flags & ~VehicleFlags.Tpp;
            return Put(id, r, outbox);
        }

        // ---- M4.3 passenger seat (host-authoritative). Separate from the driver seat and from the simulation holder:
        // a passenger never drives and never becomes the holder by entering. Entry = reservation (Passenger = slot,
        // PassengerPending, bounded timeout) -> confirmation once the passenger is seated; leaving / disconnect / region
        // change / timeout frees the seat. Conditions: unlocked, same fleet region, not travelling, a driver (the other
        // player) sits in the car, seat free, requester has no other seat, car stopped, no handoff in progress.
        public const double PassengerEntrySeconds = 10.0;
        readonly Dictionary<ushort, double> _rideDeadline = new Dictionary<ushort, double>();

        public ProposalCode ReservePassenger(ushort id, byte slot, int slotRegion, bool stopped, double now, List<StateEntry> outbox, out string why)
        {
            why = null;
            VehicleRecord r;
            if (slot == 0 || slot > 2 || !TryGet(id, out r)) { why = "unknown vehicle"; return ProposalCode.Invalid; }
            if (!r.Unlocked) { why = "locked"; return ProposalCode.NotAuthorized; }
            if (r.Region == Regions.None || r.Region != slotRegion || !TravelRules.IsFleetRegion(r.Region)) { why = "not in the requester's region"; return ProposalCode.NotAuthorized; }
            if (r.Reserved) { why = "travelling"; return ProposalCode.Busy; }
            if (r.Passenger == slot) { why = "already the passenger"; return ProposalCode.Unchanged; }
            if (r.Driver == slot) { why = "requester is the driver"; return ProposalCode.Busy; }
            if (r.Driver == 0) { why = "no driver (the free driver seat is taken through the driver handoff)"; return ProposalCode.Busy; }
            if (r.Passenger != 0) { why = "passenger seat occupied by P" + r.Passenger; return ProposalCode.Busy; }
            if (r.Holder == slot) { why = "requester simulates the car"; return ProposalCode.Busy; }
            if (DrivenBy(slot) != VehicleIds.None || RiddenBy(slot) != VehicleIds.None) { why = "requester already has a seat"; return ProposalCode.Busy; }
            if (TakeLocked(id, slot, now)) { why = "handoff in progress"; return ProposalCode.Busy; }
            if (!stopped) { why = "car is moving"; return ProposalCode.Busy; }
            r.Passenger = slot; r.Flags |= VehicleFlags.PassengerPending;
            _rideDeadline[id] = now + PassengerEntrySeconds;
            return Put(id, r, outbox) ? ProposalCode.Accepted : ProposalCode.Unchanged;
        }

        public ProposalCode ConfirmPassenger(ushort id, byte slot, List<StateEntry> outbox)
        {
            VehicleRecord r;
            if (!TryGet(id, out r)) return ProposalCode.Invalid;
            if (r.Passenger != slot) return ProposalCode.NotAuthorized;      // expired / never reserved: the client exits
            _rideDeadline.Remove(id);
            if (!r.PassengerPending) return ProposalCode.Unchanged;
            r.Flags &= ~VehicleFlags.PassengerPending;
            return Put(id, r, outbox) ? ProposalCode.Accepted : ProposalCode.Unchanged;
        }

        public ProposalCode ReleasePassenger(ushort id, byte slot, List<StateEntry> outbox)
        {
            VehicleRecord r;
            if (!TryGet(id, out r)) return ProposalCode.Invalid;
            if (r.Passenger != slot) return ProposalCode.Unchanged;
            _rideDeadline.Remove(id);
            r.Passenger = 0; r.Flags &= ~VehicleFlags.PassengerPending;
            return Put(id, r, outbox) ? ProposalCode.Accepted : ProposalCode.Unchanged;
        }

        // Entries never confirmed within PassengerEntrySeconds are cancelled.
        public int ExpirePassengers(double now, List<StateEntry> outbox)
        {
            if (_rideDeadline.Count == 0) return 0;
            var due = new List<ushort>();
            foreach (var kv in _rideDeadline) if (now >= kv.Value) due.Add(kv.Key);
            int n = 0;
            foreach (var id in due)
            {
                _rideDeadline.Remove(id);
                VehicleRecord r;
                if (!TryGet(id, out r) || !r.PassengerPending) continue;
                r.Passenger = 0; r.Flags &= ~VehicleFlags.PassengerPending;
                if (Put(id, r, outbox)) n++;
            }
            return n;
        }

        // A passenger whose region is not the car's region is no longer in it (travel, scene change).
        public int ReleasePassengerOutside(byte slot, int region, List<StateEntry> outbox)
        {
            int n = 0;
            foreach (var id in Ids())
            {
                VehicleRecord r;
                if (!TryGet(id, out r) || r.Passenger != slot || r.Region == region) continue;
                if (ReleasePassenger(id, slot, outbox) == ProposalCode.Accepted) n++;
            }
            return n;
        }

        public ushort RiddenBy(byte slot)
        {
            foreach (var id in Ids()) { VehicleRecord r; if (TryGet(id, out r) && r.Passenger == slot) return id; }
            return VehicleIds.None;
        }

        public bool SetEngine(ushort id, bool on, List<StateEntry> outbox)
        {
            VehicleRecord r;
            if (!TryGet(id, out r) || ((r.Flags & VehicleFlags.EngineOn) != 0) == on) return false;
            r.Flags = on ? r.Flags | VehicleFlags.EngineOn : r.Flags & ~VehicleFlags.EngineOn;
            return Put(id, r, outbox);
        }

        // Driving lease: the holder (the instance simulating the car) in the car's region, one vehicle per driver.
        public ProposalCode Claim(ushort id, byte slot, int slotRegion, List<StateEntry> outbox)
        {
            VehicleRecord r;
            if (slot == 0 || !TryGet(id, out r)) return ProposalCode.Invalid;
            if (!r.Unlocked) return ProposalCode.NotAuthorized;
            if (r.Driver == slot) return ProposalCode.Unchanged;
            if (r.Driver != 0 || r.Passenger == slot) return ProposalCode.Busy;
            if (r.Holder != slot || r.Region != slotRegion) return ProposalCode.NotAuthorized;   // no driver handoff in M4.2
            if (DrivenBy(slot) != VehicleIds.None) return ProposalCode.Busy;
            r.Driver = slot;
            KeyValuePair<byte, double> lk;
            if (_takeLock.TryGetValue(id, out lk) && lk.Key == slot) _takeLock.Remove(id);
            return Put(id, r, outbox) ? ProposalCode.Accepted : ProposalCode.Unchanged;
        }

        // M4.2.2 driver handoff: a player in the car's region requests the free driver seat. Granting moves the simulation
        // authority (Holder) to the requester; the requester then enters through the original CarController.EnterVehicle
        // and claims the seat with the normal lease. Conditions: registered, unlocked, same fleet region, not travelling,
        // driver seat empty, requester drives no other car, no handoff to the other player in progress, and the car is
        // settled (no movement/driver reported by its holder for VehicleMotion.SettleSeconds). commit=false: check only.
        public ProposalCode TakeOver(ushort id, byte slot, int slotRegion, bool settled, double now, bool commit, List<StateEntry> outbox, out string why)
        {
            why = null;
            VehicleRecord r;
            if (slot == 0 || slot > 2 || !TryGet(id, out r)) { why = "unknown vehicle"; return ProposalCode.Invalid; }
            if (!r.Unlocked) { why = "locked"; return ProposalCode.NotAuthorized; }
            if (r.Region == Regions.None || r.Region != slotRegion || !TravelRules.IsFleetRegion(r.Region)) { why = "not in the requester's region"; return ProposalCode.NotAuthorized; }
            if (r.Reserved) { why = "travelling"; return ProposalCode.Busy; }
            if (r.Driver == slot) { why = "already the driver"; return ProposalCode.Unchanged; }
            if (r.Driver != 0) { why = "driver seat occupied by P" + r.Driver; return ProposalCode.Busy; }
            if (r.Passenger == slot) { why = "requester is the passenger"; return ProposalCode.Busy; }
            var other = DrivenBy(slot);
            if (other != VehicleIds.None && other != id) { why = "requester drives car #" + other; return ProposalCode.Busy; }
            KeyValuePair<byte, double> lk;
            if (_takeLock.TryGetValue(id, out lk) && lk.Key != slot && now < lk.Value) { why = "handoff to P" + lk.Key + " in progress"; return ProposalCode.Busy; }
            if (r.Holder == slot) { why = "already simulated by the requester"; return ProposalCode.Unchanged; }
            if (!settled) { why = "vehicle still moving / driven"; return ProposalCode.Busy; }
            if (!commit) return ProposalCode.Accepted;
            byte prev = r.Holder;
            r.Holder = slot; r.Flags &= ~VehicleFlags.EngineOn;
            _takeLock[id] = new KeyValuePair<byte, double>(slot, now + TakeLockSeconds);
            why = "simulation P" + prev + " -> P" + slot;
            return Put(id, r, outbox) ? ProposalCode.Accepted : ProposalCode.Unchanged;
        }

        public bool TakeLocked(ushort id, byte slot, double now)
        {
            KeyValuePair<byte, double> lk;
            return _takeLock.TryGetValue(id, out lk) && lk.Key != slot && now < lk.Value;
        }

        public ProposalCode Release(ushort id, byte slot, List<StateEntry> outbox)
        {
            VehicleRecord r;
            if (!TryGet(id, out r)) return ProposalCode.Invalid;
            if (r.Driver != slot) return r.Driver == 0 ? ProposalCode.Unchanged : ProposalCode.NotAuthorized;
            r.Driver = 0;
            return Put(id, r, outbox) ? ProposalCode.Accepted : ProposalCode.Unchanged;
        }

        public ushort DrivenBy(byte slot)
        {
            foreach (var id in Ids()) { VehicleRecord r; if (TryGet(id, out r) && r.Driver == slot) return id; }
            return VehicleIds.None;
        }

        // Disconnect / menu: drop every lease/seat of that slot.
        public int ReleaseAll(byte slot, List<StateEntry> outbox)
        {
            int n = 0;
            var locks = new List<ushort>();
            foreach (var kv in _takeLock) if (kv.Value.Key == slot) locks.Add(kv.Key);
            foreach (var id in locks) _takeLock.Remove(id);
            foreach (var id in Ids())
            {
                VehicleRecord r;
                if (!TryGet(id, out r) || (r.Driver != slot && r.Passenger != slot)) continue;
                if (r.Driver == slot) r.Driver = 0;
                if (r.Passenger == slot) { r.Passenger = 0; r.Flags &= ~VehicleFlags.PassengerPending; _rideDeadline.Remove(id); }
                if (Put(id, r, outbox)) n++;
            }
            return n;
        }

        // Holder invariant (host, after every membership/reservation change): a car that is not reserved is simulated by
        // its current holder while that player is in the car's region, else by the lowest present slot (host first),
        // else by nobody (parked, logical only). Reserved cars keep the traveller as holder until arrival or rollback.
        public int ReassignHolders(Func<byte, int> regionOf, List<StateEntry> outbox)
        {
            int n = 0;
            foreach (var id in Ids())
            {
                VehicleRecord r;
                if (!TryGet(id, out r) || r.Reserved) continue;
                byte h = r.Holder;
                if (h != 0 && (r.Region == Regions.None || regionOf(h) != r.Region)) h = 0;
                if (h == 0 && r.Region != Regions.None && r.Unlocked)
                    for (byte s = 1; s <= 2; s++) if (regionOf(s) == r.Region) { h = s; break; }
                if (h == r.Holder) continue;
                r.Holder = h;
                if (r.Driver != 0 && r.Driver != h) r.Driver = 0;
                if (h == 0) r.Flags &= ~VehicleFlags.EngineOn;
                if (Put(id, r, outbox)) n++;
            }
            return n;
        }
    }

    // Cache of the fleet records, rebuilt only when the registry head revision changes (no per-packet string lookups).
    public sealed class FleetView
    {
        readonly Dictionary<ushort, VehicleRecord> _map = new Dictionary<ushort, VehicleRecord>();
        readonly Dictionary<ushort, ParkRecord> _park = new Dictionary<ushort, ParkRecord>();
        public int Count { get { return _map.Count; } }

        public void Refresh(StateRegistry reg)
        {
            _map.Clear(); _park.Clear();
            foreach (var e in reg.Entries)
            {
                if (e.Scope != StateScope.Session || e.ScopeArg <= 0 || e.ScopeArg > ushort.MaxValue) continue;
                VehicleRecord r; ParkRecord p;
                if (e.Key == WorldKeys.Vehicle && VehicleRecord.TryParse(e.Value, out r)) _map[(ushort)e.ScopeArg] = r;
                else if (e.Key == WorldKeys.VehiclePark && ParkRecord.TryParse(e.Value, out p)) _park[(ushort)e.ScopeArg] = p;
            }
        }

        public bool TryGet(ushort id, out VehicleRecord r) { return _map.TryGetValue(id, out r); }

        // M4.2.2 per-frame seat lookup without allocations (struct enumerator; same rule as SeatRules.Find).
        public bool FindSeat(byte slot, int region, out ushort id, out SeatRole role)
        {
            id = VehicleIds.None; role = SeatRole.None;
            if (region == Regions.None || slot == 0) return false;
            foreach (var kv in _map)
            {
                if (kv.Value.Region != region) continue;
                var r = SeatRules.RoleOf(kv.Value, slot);
                if (r == SeatRole.None) continue;
                id = kv.Key; role = r; return true;
            }
            return false;
        }
        public bool TryGetPark(ushort id, out ParkRecord p) { return _park.TryGetValue(id, out p); }
        public IEnumerable<KeyValuePair<ushort, VehicleRecord>> All { get { return _map; } }

        // Who may stream a vehicle's movement: its holder, for the car's own region only.
        public static bool SenderSimulates(VehicleRecord r, byte senderSlot, int snapshotRegion)
        {
            return r.Holder != 0 && r.Holder == senderSlot && r.Region == snapshotRegion;
        }
    }

    public struct Quat
    {
        public float X, Y, Z, W;
        public Quat(float x, float y, float z, float w) { X = x; Y = y; Z = z; W = w; }
        public static readonly Quat Identity = new Quat(0, 0, 0, 1);
        public float Dot(Quat o) { return X * o.X + Y * o.Y + Z * o.Z + W * o.W; }

        public Quat Normalized()
        {
            float n = (float)Math.Sqrt(X * X + Y * Y + Z * Z + W * W);
            return n < 1e-6f ? Identity : new Quat(X / n, Y / n, Z / n, W / n);
        }

        // Shortest-path normalized lerp (snapshot steps are small).
        public static Quat Nlerp(Quat a, Quat b, float t)
        {
            if (a.Dot(b) < 0) b = new Quat(-b.X, -b.Y, -b.Z, -b.W);
            return new Quat(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t, a.W + (b.W - a.W) * t).Normalized();
        }

        public static float AngleDegrees(Quat a, Quat b)
        {
            float d = Math.Min(1f, Math.Abs(a.Normalized().Dot(b.Normalized())));
            return (float)(2.0 * Math.Acos(d) * 180.0 / Math.PI);
        }
    }

    [Flags]
    public enum VehicleStateFlags : byte { None = 0, EngineOn = 1, Driven = 2, Moving = 4, Tpp = 8 }   // Moving: protocol 8 (M4.2.2); Tpp: protocol 10 (M4.3)

    // Movement snapshot of one vehicle from its holder. 38 bytes incl. message type (M4.2: + fuel, centi-units).
    public struct VehicleSnapshot
    {
        public ushort Id;
        public uint Seq;
        public uint TimeMs;          // sender clock
        public int Region;
        public Vec3 Pos;
        public Quat Rot;
        public VehicleStateFlags Flags;
        public float Fuel;           // the holder's SaveGame.gasTankLeft for this car (M4.2 per-vehicle fuel)
        public float Steer;          // M5.0 front-wheel steer angle of the holder's EVP controller (degrees, +-127), observer wheel visuals only
        public const int WireSize = 39;
        public const float MaxSteer = 127f;

        static short Q(float v) { return (short)Math.Round(Math.Max(-1f, Math.Min(1f, v)) * 32767f); }
        static float DQ(short v) { return v / 32767f; }

        public void Write(NetDataWriter w)
        {
            var r = Rot.Normalized();
            w.Put((byte)MsgType.VehicleState); w.Put(Id); w.Put(Seq); w.Put(TimeMs); w.Put(Region);
            w.Put(Pos.X); w.Put(Pos.Y); w.Put(Pos.Z);
            w.Put(Q(r.X)); w.Put(Q(r.Y)); w.Put(Q(r.Z)); w.Put(Q(r.W));
            w.Put((byte)Flags);
            w.Put((ushort)Math.Round(Math.Max(0f, Math.Min(TravelRules.MaxFuel, Fuel)) * 100f));
            w.Put((sbyte)Math.Round(float.IsNaN(Steer) ? 0f : Math.Max(-MaxSteer, Math.Min(MaxSteer, Steer))));
        }

        // Reader positioned after the message type byte.
        public static bool TryRead(NetDataReader r, out VehicleSnapshot s)
        {
            s = default(VehicleSnapshot);
            if (r.AvailableBytes != WireSize - 1) return false;
            s.Id = r.GetUShort(); s.Seq = r.GetUInt(); s.TimeMs = r.GetUInt(); s.Region = r.GetInt();
            s.Pos = new Vec3(r.GetFloat(), r.GetFloat(), r.GetFloat());
            float x = DQ(r.GetShort()), y = DQ(r.GetShort()), z = DQ(r.GetShort()), w = DQ(r.GetShort());
            s.Flags = (VehicleStateFlags)r.GetByte();
            s.Fuel = r.GetUShort() / 100f;
            s.Steer = r.GetSByte();
            float n = x * x + y * y + z * z + w * w;
            if (n < 0.9f || n > 1.1f) return false;
            s.Rot = new Quat(x, y, z, w).Normalized();
            if (s.Id == VehicleIds.None || ((byte)s.Flags & 0xF0) != 0 || !Regions.IsKnown(s.Region) || s.Fuel > TravelRules.MaxFuel) return false;
            return Finite(s.Pos.X) && Finite(s.Pos.Y) && Finite(s.Pos.Z)
                && Math.Abs(s.Pos.X) <= SnapshotValidation.MaxCoordinate && Math.Abs(s.Pos.Y) <= SnapshotValidation.MaxCoordinate && Math.Abs(s.Pos.Z) <= SnapshotValidation.MaxCoordinate;
        }

        static bool Finite(float f) { return !float.IsNaN(f) && !float.IsInfinity(f); }
    }

    public enum VehicleVerdict { Accept, Stale, UnknownVehicle, NotDriverAuthority, WrongRegion, TooMany }

    // Receiver-side acceptance: registered vehicle, sender is its simulating owner, newer sequence per vehicle, and the
    // vehicle is in the receiver's region (others are ignored: no remote visual simulation for unviewed regions).
    public sealed class VehicleSnapshotFilter
    {
        readonly Dictionary<ushort, uint> _last = new Dictionary<ushort, uint>();
        public int MaxTracked = FleetBook.MaxVehicles;
        public int Tracked { get { return _last.Count; } }

        public VehicleVerdict Check(VehicleSnapshot s, bool registered, bool senderOwns, int localRegion)
        {
            if (!registered) return VehicleVerdict.UnknownVehicle;
            if (!senderOwns) return VehicleVerdict.NotDriverAuthority;
            uint last;
            bool has = _last.TryGetValue(s.Id, out last);
            if (has && !SnapshotValidation.IsNewer(s.Seq, last)) return VehicleVerdict.Stale;
            if (!has && _last.Count >= MaxTracked) return VehicleVerdict.TooMany;
            _last[s.Id] = s.Seq;
            return s.Region == localRegion && localRegion != Regions.None ? VehicleVerdict.Accept : VehicleVerdict.WrongRegion;
        }

        public void Forget(ushort id) { _last.Remove(id); }
        public void Clear() { _last.Clear(); }
    }

    public struct VehiclePose { public Vec3 Pos; public Quat Rot; public VehicleStateFlags Flags; public bool Snapped; }

    // Bounded interpolation buffer for one remote vehicle (sender time + min-filtered clock offset, like SnapshotBuffer).
    public sealed class VehiclePoseBuffer
    {
        public const int Capacity = 16;
        public double InterpolationDelay = 0.12;
        public float TeleportDistance = 25f;      // metres between consecutive snapshots (respawn/travel)
        public double MaxGap = 3.0;               // parked vehicles send a heartbeat every 2 s

        struct Entry { public double T; public VehicleSnapshot S; }
        readonly Entry[] _ring = new Entry[Capacity];
        int _start, _count;
        double _offset; bool _hasOffset;

        public int Count { get { return _count; } }
        public void Clear() { _count = 0; _start = 0; _hasOffset = false; }

        Entry At(int i) { return _ring[(_start + i) % Capacity]; }

        public void Add(VehicleSnapshot s, double localNow)
        {
            double t = s.TimeMs / 1000.0;
            double off = localNow - t;
            if (!_hasOffset || off < _offset) { _offset = off; _hasOffset = true; }
            if (_count > 0 && t <= At(_count - 1).T) return;          // caller filtered sequence; equal time -> ignore
            if (_count == Capacity) { _start = (_start + 1) % Capacity; _count--; }
            _ring[(_start + _count) % Capacity] = new Entry { T = t, S = s };
            _count++;
        }

        public bool Sample(double localNow, out VehiclePose p)
        {
            p = default(VehiclePose);
            if (_count == 0) return false;
            double rt = localNow - _offset - InterpolationDelay;
            while (_count >= 3 && At(1).T <= rt) { _start = (_start + 1) % Capacity; _count--; }   // keep one older sample
            var a = At(0);
            if (_count == 1 || rt <= a.T) { p = Pose(a.S, rt > a.T); return true; }
            var b = At(1);
            if (rt >= b.T) { p = Pose(b.S, false); return true; }
            if (b.T - a.T > MaxGap || Vec3.Distance(a.S.Pos, b.S.Pos) > TeleportDistance) { p = Pose(b.S, true); return true; }
            float k = (float)((rt - a.T) / (b.T - a.T));
            p.Pos = Vec3.Lerp(a.S.Pos, b.S.Pos, k);
            p.Rot = Quat.Nlerp(a.S.Rot, b.S.Rot, k);
            p.Flags = b.S.Flags;
            return true;
        }

        static VehiclePose Pose(VehicleSnapshot s, bool snapped) { return new VehiclePose { Pos = s.Pos, Rot = s.Rot, Flags = s.Flags, Snapped = snapped }; }
    }

    // Owner-side send policy: moving -> up to RateHz; parked -> heartbeat. Pure.
    public sealed class VehicleSendPolicy
    {
        public double RateHz = 20, HeartbeatSeconds = 2;
        public float MoveEpsilon = 0.02f, AngleEpsilon = 0.5f;
        double _lastSend = -1; Vec3 _lastPos; Quat _lastRot; VehicleStateFlags _lastFlags; int _lastRegion = int.MinValue;

        public bool ShouldSend(double now, Vec3 pos, Quat rot, VehicleStateFlags flags, int region)
        {
            if (_lastSend >= 0 && now - _lastSend < 1.0 / RateHz - 0.002) return false;   // tolerance: 60 fps frames sum to just under 50 ms
            bool changed = _lastSend < 0 || region != _lastRegion || flags != _lastFlags
                || Vec3.Distance(pos, _lastPos) > MoveEpsilon || Quat.AngleDegrees(rot, _lastRot) > AngleEpsilon;
            if (!changed && now - _lastSend < HeartbeatSeconds) return false;
            _lastSend = now; _lastPos = pos; _lastRot = rot; _lastFlags = flags; _lastRegion = region;
            return true;
        }

        public void Reset() { _lastSend = -1; _lastRegion = int.MinValue; }
    }

    // M4.2.2 holder-side movement flag with hysteresis (no flapping near standstill -> no extra sends/commits). Pure.
    public sealed class MotionLatch
    {
        public float OnSpeed = 0.5f, OffSpeed = 0.15f;   // m/s
        public double OffDelay = 0.5;                     // s below OffSpeed before "stationary"
        bool _moving; double _below = -1;
        public bool Moving { get { return _moving; } }

        public bool Update(float speed, double now)
        {
            if (speed > OnSpeed) { _moving = true; _below = -1; }
            else if (_moving)
            {
                if (speed >= OffSpeed) _below = -1;
                else if (_below < 0) _below = now;
                else if (now - _below >= OffDelay) { _moving = false; _below = -1; }
            }
            return _moving;
        }

        public void Reset() { _moving = false; _below = -1; }
    }

    // M4.2.2 host-only: last movement/driving activity per vehicle, reported by its holder (host: local physics;
    // client-held: VehicleState flags). A handoff is only granted when the car is settled. Not replicated. Pure.
    public sealed class VehicleMotion
    {
        public double SettleSeconds = 1.0;
        readonly Dictionary<ushort, double> _active = new Dictionary<ushort, double>();

        readonly Dictionary<ushort, double> _moved = new Dictionary<ushort, double>();
        public void Report(ushort id, bool moving, bool driven, double now) { if (moving || driven) _active[id] = now; if (moving) _moved[id] = now; }
        // Settled: neither moving nor driven (driver handoff). Stopped: not moving, a driver may sit inside (doors, passenger entry).
        public bool Settled(ushort id, double now) { double t; return !_active.TryGetValue(id, out t) || now - t >= SettleSeconds; }
        public bool Stopped(ushort id, double now) { double t; return !_moved.TryGetValue(id, out t) || now - t >= SettleSeconds; }
        public void Forget(ushort id) { _active.Remove(id); _moved.Remove(id); }
        public void Clear() { _active.Clear(); _moved.Clear(); }
    }

    // M5.0 car doors: the original Door components referenced by CarController.car_door_L (0) / car_door_R (1).
    // Committed state = registry key "vdoor" (Session scope, arg = vehicle id): 1 byte, bit n = door n open,
    // bit 2 + n = the last change of door n was the original SLOW (hold-E, quiet) movement and has not been finished fast.
    // Proposal (client -> host) = 3 bytes {door, open, mode}. Host-validated in WorldHost.SetDoor. (M4.3.1, protocol 11)
    // Original (Door.Update / Door.SlowFailed / Door.Use IL): E held >= 0.15 s toggles isOpen at once and swings slowly
    // (openSpeed 17 x speedMultiplier, quiet squeak); releasing E before slowMoving > 0.8 calls SlowFailed, which finishes
    // the SAME movement at 150 x speedMultiplier with the loud open/close clip (FastFinish). A short press is Door.Use (Quick).
    public enum DoorMode : byte { Quick = 0, Slow = 1, FastFinish = 2 }
    public enum DoorStep : byte { None, OpenQuick, CloseQuick, OpenSlow, CloseSlow, FinishFast }

    public static class VehicleDoors
    {
        public const int Count = 2;
        public const byte OpenBits = 0x03, SlowBits = 0x0C;
        public static bool IsOpen(byte mask, int door) { return ((mask >> door) & 1) != 0; }
        public static bool IsSlow(byte mask, int door) { return ((mask >> (2 + door)) & 1) != 0; }
        public static byte With(byte mask, int door, bool open) { return open ? (byte)(mask | (1 << door)) : (byte)(mask & ~(1 << door)); }
        static byte WithSlow(byte mask, int door, bool slow) { return slow ? (byte)(mask | (1 << (2 + door))) : (byte)(mask & ~(1 << (2 + door))); }

        // Next committed mask for one reported door change. A state change records whether it was slow; FastFinish only
        // clears the slow mark of a door whose slow movement is committed (same target state); anything else is no change.
        public static byte Next(byte mask, int door, bool open, DoorMode mode)
        {
            if (IsOpen(mask, door) != open) return WithSlow(With(mask, door, open), door, mode == DoorMode.Slow);
            if (mode == DoorMode.FastFinish && IsSlow(mask, door)) return WithSlow(mask, door, false);
            return mask;
        }

        public static byte[] Proposal(int door, bool open, DoorMode mode) { return new[] { (byte)door, open ? (byte)1 : (byte)0, (byte)mode }; }
        public static bool TryParseProposal(byte[] v, out int door, out bool open, out DoorMode mode)
        {
            door = 0; open = false; mode = DoorMode.Quick;
            if (v == null || v.Length != 3 || v[0] >= Count || v[1] > 1 || v[2] > (byte)DoorMode.FastFinish) return false;
            door = v[0]; open = v[1] == 1; mode = (DoorMode)v[2]; return true;
        }
        public static bool TryParseState(byte[] v, out byte mask)
        {
            mask = 0;
            if (v == null || v.Length != 1 || v[0] > (OpenBits | SlowBits)) return false;
            mask = v[0]; return true;
        }
    }

    // Per-instance replica of one car's door state. 'Local' is what the local Door objects show (open bits). Local changes
    // (the local player or the local original CarController code) are reported; the committed state is applied only while
    // no own report is unanswered, so a delayed commit of an earlier report never re-opens / re-closes a door (the registry
    // delta precedes its proposal result). The host commits in place, so its Pending stays 0.
    // M4.3.1: 'AppliedSlow' = doors this instance is swinging slowly because of a committed slow change; a later commit that
    // clears the slow mark (same open state) finishes that movement fast (the remote player released E early). Right after
    // binding new scene objects (scene load / late join) committed slow marks are ignored: the door just swings to its state.
    public sealed class DoorReplica
    {
        public byte Local;
        public byte Committed;
        public bool HasCommitted;
        public int Pending;
        public byte AppliedSlow;
        public bool Fresh = true;

        public void Bind(byte actual) { Local = actual; Pending = 0; AppliedSlow = 0; Fresh = true; }   // new scene objects
        public byte TakeLocalChanges(byte actual) { byte c = (byte)(actual ^ Local); Local = actual; return c; }
        public void Sent() { Pending++; }
        public void Result() { if (Pending > 0) Pending--; }
        public void OnCommitted(byte mask) { Committed = mask; HasCommitted = true; }
        public bool Wanted(out byte mask)
        {
            mask = Committed;
            if (!HasCommitted || Pending != 0) return false;
            return (Committed & VehicleDoors.OpenBits) != Local || (AppliedSlow & ~Committed & VehicleDoors.SlowBits) != 0;
        }

        public DoorStep Plan(int door)
        {
            bool want = VehicleDoors.IsOpen(Committed, door), slow = !Fresh && VehicleDoors.IsSlow(Committed, door);
            if (want != VehicleDoors.IsOpen(Local, door)) return want ? (slow ? DoorStep.OpenSlow : DoorStep.OpenQuick) : (slow ? DoorStep.CloseSlow : DoorStep.CloseQuick);
            if (VehicleDoors.IsSlow(AppliedSlow, door) && !VehicleDoors.IsSlow(Committed, door)) return DoorStep.FinishFast;
            return DoorStep.None;
        }

        // After the planned steps were executed on the Door objects.
        public void Applied()
        {
            byte slow = 0;
            for (int d = 0; d < VehicleDoors.Count; d++)
            {
                var st = Plan(d);
                bool keep = st == DoorStep.OpenSlow || st == DoorStep.CloseSlow || (st == DoorStep.None && VehicleDoors.IsSlow(AppliedSlow, d) && VehicleDoors.IsSlow(Committed, d));
                if (keep) slow |= (byte)(1 << (2 + d));
            }
            Local = (byte)(Committed & VehicleDoors.OpenBits);
            AppliedSlow = slow;
            Fresh = false;
        }

        // The local slow swing of a door ended (reached its state): forget it, a later fast-finish commit is then moot.
        public void SlowEnded(int door) { AppliedSlow = (byte)(AppliedSlow & ~(1 << (2 + door))); }
    }

    // M4.3.1 passenger interaction rules (pure; the plugin supplies what the original PlayerScript.Update interaction ray
    // sees and the car geometry). Original pipeline (Cecil IL, PlayerScript.Update IL_4662..IL_7f5a): ONE ray
    // Physics.Raycast(cam.position, cam.forward, 2.25, PlayerScript.mask), gated by PMB.enabled, !isRunning, timeScale > 0,
    // !attacking, !isCarrying, !closeUp, canInteract, PCB.enabled, !hideHands, !callIncoming, !pickpocket, !opening_slowly,
    // !pickingUp, !isHiding; the hit transform's TAG selects the branch (NativeTags). Passenger entry is only offered when
    // that ray selects no original branch.
    public enum InteractTarget : byte { None, CarBody, Obstacle, Native, CarBox, Medkit }
    public enum FootAction : byte { None, Original, RequestPassenger }
    public enum SeatAction : byte { None, OpenStorage, Exit, OutOfReach }

    public struct FootProbe
    {
        public bool Gates;            // the original interaction gates above hold and the player is on foot
        public InteractTarget Ray;    // classification of the original ray's hit
        public bool CarEligible;      // nearest car accepts a passenger request (other player drives, seat free, car stopped)
        public float Reach;           // horizontal distance from the player's feet to the car's box (car space), m
        public float FeetHeight;      // player's feet above the car's own ground level (wheel contact), m
        public bool LineOfSight;      // nothing solid (wall, other object) between the player's eyes and the nearest point of the car
        public bool WasShown;         // the entry prompt was shown on the previous evaluation (hysteresis, same for prompt and E)
    }

    public struct SeatProbe
    {
        public bool Gates;            // seated >= 1 s, game not paused, no inventory/pause UI open or just closed
        public bool FirstPerson;      // the original interaction ray (main camera) only exists in first person
        public InteractTarget Ray;    // classification of that ray's hit from the passenger's eyes
    }

    public static class PassengerInteraction
    {
        public const float RayLength = 2.25f;      // PlayerScript.Update interaction ray (IL ldc.r4 2.25)
        public const float EntryReach = 1.2f;      // m from the car's collider box: standing next to it
        // feet level relative to the car's ground level (wheel contact): the ground the car stands on (kerbs, slopes), not the
        // roof (~1.5 m), not another floor. M4.3.1 measured from the body box bottom, which sits ~0.58 m above the ground
        // (0.6.1 logs: feet -0.56..-0.60 against a -0.6 limit) -> entries failed on slightly lower ground (root cause).
        public const float GroundBelow = 0.5f, GroundAbove = 0.6f;
        public const float ExitClearance = 0.45f;  // capsule radius 0.35 + 0.1 m from the car's collider box
        public const float CentreSeat = 0.15f;     // |seat x| below this = centre seat (Ranger rear bench)

        // Every tag PlayerScript.Update compares against the hit transform (IL string literals before String.op_Equality).
        static readonly HashSet<string> NativeTags = new HashSet<string>(StringComparer.Ordinal) {
            "Pickupable", "DiamondMarker", "PlanCube", "NPC", "SecurityCam", "Policeman", "Guard", "WindowEnter", "SidewalkNPC",
            "Door", "BuyGarage", "ElevatorButtonOpen", "Tracker", "Cat", "Payphone", "DrinkBottle", "Food", "ExitMapDoor", "BuyVan",
            "Intercom", "EasterEgg", "VanDoor", "CMS_Car", "CMS_Edit", "Doorbell", "GarageSwitch", "PlantEvidence", "Plakat",
            "BailDoor", "ReadNote", "ResetCar", "Codename", "ParcelLocker", "SleepGasFill", "Lock1", "MailBox_Retrieve", "MailBox",
            "DigHole", "CheckCameras", "ElectricLock", "Security_Panel", "CarPDAHack", "Ivy_Climb", "Vent", "Gutter",
            "CourierCarDoor", "WeldDoor", "Car_Alarm", "CarDoorLocked", "SteeringWheel", "OtherCarCables", "Blocked", "CarLift",
            "ATM", "Jewelery_Table", "CardboardTrigger", "HackLaptopTrigger", "DismantleCar", "PutOnLift", "CarSuspension",
            "GateSwitch", "LightSwitch", "FuseSwitch", "Bed", "Computer", "CarBox", "Medkit", "HiddenItem", "HeistBoard",
            "HideoutRadio", "HidingSpot", "SitCar", "GetOutCar", "EnterOtherCar", "Shop_Interact", "Safe", "Brick", "Window" };

        public static bool IsNativeTag(string tag) { return tag != null && NativeTags.Contains(tag); }

        // Classify one ray hit. carBox/medkit: the hit collider carries that original component (Medkit is untagged while its
        // original rule hides it, IL Medkit.Update); ofCar: the collider belongs to the passenger-eligible car.
        public static InteractTarget Classify(bool hit, string transformTag, bool carBox, bool medkit, bool ofCar)
        {
            if (!hit) return InteractTarget.None;
            if (carBox) return InteractTarget.CarBox;
            if (medkit) return InteractTarget.Medkit;
            if (IsNativeTag(transformTag)) return InteractTarget.Native;
            return ofCar ? InteractTarget.CarBody : InteractTarget.Obstacle;
        }

        public const float Hysteresis = 0.1f;      // m: a shown prompt stays until 1.3 m (no flicker at the edge); E then still enters
        public static bool NearCar(float reach, float feetHeight) { return NearCar(reach, feetHeight, false); }
        public static bool NearCar(float reach, float feetHeight, bool wasShown)
        {
            float slack = wasShown ? Hysteresis : 0f;
            return reach <= EntryReach + slack && feetHeight >= -GroundBelow - slack && feetHeight <= GroundAbove + slack;
        }

        // On foot, E pressed. M4.3.2a: the camera ray only decides whether an ORIGINAL interaction owns this press; passenger
        // entry itself is a vehicle-relative accessibility test (next to the car's box, on its ground level, clear line from
        // the eyes to the car) - no aiming. M4.3.1 also required the ray to hit the car body or the camera to face the car, and
        // treated any other hit (the ground, a wall behind the player) as an obstacle -> entry failed at many angles.
        public static FootAction OnFoot(FootProbe p)
        {
            if (!p.Gates) return FootAction.None;
            if (p.Ray == InteractTarget.Native || p.Ray == InteractTarget.CarBox || p.Ray == InteractTarget.Medkit) return FootAction.Original;
            if (!p.CarEligible || !NearCar(p.Reach, p.FeetHeight, p.WasShown) || !p.LineOfSight) return FootAction.None;
            return FootAction.RequestPassenger;
        }

        // Car-local exit spots for a passenger seat: beside the seat (same z), just outside the car's collider box. A side seat
        // (Shadowhawk associateSitPos x = +0.44) exits on its own side; a centre seat (Ranger rear bench x = -0.01) on the
        // driver's side, i.e. behind the driver door (user requirement). 'mirrored' = the opposite side at the same z.
        public static void ExitSpots(Vec3 seat, float driverSideX, float boxMinX, float boxMaxX, out Vec3 primary, out Vec3 mirrored)
        {
            float side = Math.Abs(seat.X) >= CentreSeat ? Math.Sign(seat.X) : (driverSideX <= 0f ? -1f : 1f);
            float left = boxMinX - ExitClearance, right = boxMaxX + ExitClearance;
            primary = new Vec3(side < 0 ? left : right, seat.Y, seat.Z);
            mirrored = new Vec3(side < 0 ? right : left, seat.Y, seat.Z);
        }

        // Local passenger's own body (visual-only associate model): only while seated in third person (first person would put
        // the head into the camera; the first-person arms/legs stay the game's own).
        public static bool SelfBodyVisible(bool seated, bool thirdPerson, bool seatAnchorValid) { return seated && thirdPerson && seatAnchorValid; }

        // Car storage from a passenger seat: M4.3.1 host only; M4.3.2b both roles once the shared, host-leased CarBox is active
        // (the client then sees the host's contents; its item moves stay blocked - see CarBoxSync).
        public static bool SeatStorageAllowed(bool isHost, bool sharedLease) { return isHost || sharedLease; }

        // Seated passenger, E pressed: the car's storage box (first person, like the original driver) opens the original
        // storage; the medkit is out of reach from a passenger seat; everything else gets out.
        public static SeatAction Seated(SeatProbe p)
        {
            if (!p.Gates) return SeatAction.None;
            if (p.FirstPerson && p.Ray == InteractTarget.CarBox) return SeatAction.OpenStorage;
            if (p.FirstPerson && p.Ray == InteractTarget.Medkit) return SeatAction.OutOfReach;
            return SeatAction.Exit;
        }
    }
}

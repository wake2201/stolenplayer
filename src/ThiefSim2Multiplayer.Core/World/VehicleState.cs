using System;
using System.IO;
using ThiefSim2Multiplayer.Core.Networking;

namespace ThiefSim2Multiplayer.Core.World
{
    // Actor IDs are scoped by the authenticated session and registry SceneEpoch.
    public sealed class VehicleState
    {
        public ulong OccupancyRevision = 1;
        public ulong AuthorityGeneration = 1;
        public float Fuel = 60;
        public byte Driver, Passenger, PendingDriver, PendingPassenger;
        public bool PassengerSeat;
        public bool ExitingDriver, ExitingPassenger;
        public bool EngineRunning;
        public bool Exiting(SeatRole seat) => seat==SeatRole.Driver ? ExitingDriver : seat==SeatRole.Passenger && ExitingPassenger;
        public float X, Y, Z, Qx, Qy, Qz, Qw = 1, Vx, Vy, Vz;
        public float Ax,Ay,Az;
        public byte Occupant(SeatRole seat) => seat == SeatRole.Driver ? Driver : seat == SeatRole.Passenger ? Passenger : (byte)0;
        public byte Pending(SeatRole seat) => seat == SeatRole.Driver ? PendingDriver : seat == SeatRole.Passenger ? PendingPassenger : (byte)0;
        public bool HasActor(byte actor) => actor != 0 && (Driver == actor || Passenger == actor || PendingDriver == actor || PendingPassenger == actor);
        public SeatRole AvailableSeat(bool passengerCloser)
        {
            bool driverFree=Driver==0 && PendingDriver==0 && !ExitingDriver;
            bool passengerFree=PassengerSeat && Passenger==0 && PendingPassenger==0 && !ExitingPassenger;
            if(driverFree && passengerFree) return passengerCloser ? SeatRole.Passenger : SeatRole.Driver;
            return passengerFree ? SeatRole.Passenger : driverFree ? SeatRole.Driver : SeatRole.None;
        }
        public bool Valid()
        {
            if (OccupancyRevision == 0 || AuthorityGeneration == 0 || !Protocol.Finite(Fuel) || Math.Abs(Fuel)>10000 || Driver > 2 || Passenger > 2 || PendingDriver > 2 || PendingPassenger > 2 ||
                !PassengerSeat && (Passenger != 0 || PendingPassenger != 0) || Driver != 0 && PendingDriver != 0 || Passenger != 0 && PendingPassenger != 0 || ExitingDriver && Driver==0 || ExitingPassenger && Passenger==0) return false;
            byte[] actors = { Driver, Passenger, PendingDriver, PendingPassenger };
            for (int i = 0; i < actors.Length; i++) for (int j = i+1; j < actors.Length; j++) if (actors[i] != 0 && actors[i] == actors[j]) return false;
            float[] values = { X, Y, Z, Qx, Qy, Qz, Qw, Vx, Vy, Vz, Ax,Ay,Az };
            foreach (float f in values) if (!Protocol.Finite(f)) return false;
            float norm = Qx*Qx + Qy*Qy + Qz*Qz + Qw*Qw;
            return Math.Abs(X) <= 100000 && Math.Abs(Y) <= 100000 && Math.Abs(Z) <= 100000 && norm > .98f && norm < 1.02f && Math.Abs(Vx) <= 200 && Math.Abs(Vy) <= 200 && Math.Abs(Vz) <= 200 && Math.Abs(Ax)<=100 && Math.Abs(Ay)<=100 && Math.Abs(Az)<=100;
        }
        public byte[] Encode()
        {
            if (!Valid()) throw new ArgumentException("Invalid vehicle state.");
            using (var s = new MemoryStream()) using (var w = new BinaryWriter(s)) {
                w.Write((byte)3); w.Write(OccupancyRevision); w.Write(Driver); w.Write(Passenger); w.Write(PendingDriver); w.Write(PendingPassenger); w.Write(PassengerSeat); w.Write((byte)((ExitingDriver ? 1 : 0)|(ExitingPassenger ? 2 : 0)|(EngineRunning ? 4 : 0)));
                foreach (float f in new[] { X,Y,Z,Qx,Qy,Qz,Qw,Vx,Vy,Vz }) w.Write(f); w.Write(AuthorityGeneration); w.Write(Fuel); w.Write(Ax); w.Write(Ay); w.Write(Az); return s.ToArray();
            }
        }
        public static bool TryDecode(byte[] bytes, out VehicleState state)
        {
            state = null; if (bytes == null || bytes.Length != 79 || bytes[0] != 3 || bytes[13] > 1 || bytes[14]>7) return false;
            using (var r = new BinaryReader(new MemoryStream(bytes, false))) {
                r.ReadByte(); var v = new VehicleState { OccupancyRevision = r.ReadUInt64(), Driver = r.ReadByte(), Passenger = r.ReadByte(), PendingDriver = r.ReadByte(), PendingPassenger = r.ReadByte(), PassengerSeat = r.ReadBoolean() }; byte flags=r.ReadByte(); v.ExitingDriver=(flags&1)!=0; v.ExitingPassenger=(flags&2)!=0; v.EngineRunning=(flags&4)!=0;
                v.X=r.ReadSingle(); v.Y=r.ReadSingle(); v.Z=r.ReadSingle(); v.Qx=r.ReadSingle(); v.Qy=r.ReadSingle(); v.Qz=r.ReadSingle(); v.Qw=r.ReadSingle(); v.Vx=r.ReadSingle(); v.Vy=r.ReadSingle(); v.Vz=r.ReadSingle();
                v.AuthorityGeneration=r.ReadUInt64(); v.Fuel=r.ReadSingle();
                v.Ax=r.ReadSingle(); v.Ay=r.ReadSingle(); v.Az=r.ReadSingle();
                if (!v.Valid()) return false; state = v; return true;
            }
        }
    }
    public interface IVehicleAdapter : INetworkEntityAdapter { VehicleState State { get; set; } }
    public sealed class ManagedVehicleAdapter : IVehicleAdapter
    {
        public VehicleState State { get; set; } = new VehicleState { PassengerSeat = true };
        public EntityTypeId EntityType => EntityTypeId.Vehicle;
        public bool IsAlive { get; private set; } = true;
        public byte[] CaptureState() => State.Encode();
        public bool ValidateState(byte[] payload) { VehicleState v; return VehicleState.TryDecode(payload, out v); }
        public void ApplyState(byte[] payload) { VehicleState v; if (!VehicleState.TryDecode(payload, out v)) throw new ArgumentException(); State = v; }
        public void Release() { IsAlive = false; }
    }
}

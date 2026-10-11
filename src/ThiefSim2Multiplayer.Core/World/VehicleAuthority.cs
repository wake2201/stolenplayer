using System;
using System.Collections.Generic;
using ThiefSim2Multiplayer.Core.Networking;

namespace ThiefSim2Multiplayer.Core.World
{
    public sealed class VehicleAuthority
    {
        private readonly ulong[] highWater = new ulong[3];
        private readonly Dictionary<EntityRef,double> reservations = new Dictionary<EntityRef,double>();
        private readonly Dictionary<EntityRef,DriverInput> inputs = new Dictionary<EntityRef,DriverInput>();
        private readonly Dictionary<EntityRef,double> inputTimes = new Dictionary<EntityRef,double>();
        private sealed class MotionLease { public ulong Generation; public uint Sequence,Tick; public double Seen; public float FuelUsed,Speed,PreviousSpeed; public bool Received; }
        private readonly Dictionary<EntityRef,MotionLease> motionLeases = new Dictionary<EntityRef,MotionLease>();
        private readonly double[] rateWindow = new double[3]; private readonly int[] rateCount = new int[3];
        public void Reset() { Array.Clear(highWater,0,3); Array.Clear(rateCount,0,3); Array.Clear(rateWindow,0,3); reservations.Clear(); inputs.Clear(); inputTimes.Clear(); motionLeases.Clear(); }
        public SeatResult Handle(NetworkEntityRegistry registry, SeatRequest q, ulong session, byte authenticatedActor, bool ready, double now, Func<byte,EntityRef,SeatRole,SeatAction,EntityRef,bool> eligible)
        {
            if (!VehicleProtocol.Valid(q) || q.Actor != authenticatedActor) return SeatResult.WrongActor;
            if (q.SessionId != session) return SeatResult.WrongSession;
            if (q.Epoch != registry.SceneEpoch) return SeatResult.WrongEpoch;
            if (!registry.IsHost || !ready) return SeatResult.NotReady;
            if (q.RequestId <= highWater[q.Actor]) return SeatResult.Duplicate;
            highWater[q.Actor] = q.RequestId;
            if (now-rateWindow[q.Actor] >= 1) { rateWindow[q.Actor]=now; rateCount[q.Actor]=0; }
            if (++rateCount[q.Actor] > 30) return SeatResult.RateLimited;
            var a = registry.GetBinding(q.Vehicle) as IVehicleAdapter;
            if (a == null || !a.IsAlive || q.Seat == SeatRole.Passenger && !a.State.PassengerSeat) return SeatResult.Unavailable;
            var v=a.State;
            byte oldDriver=v.Driver;
            // Exit repeats with a fresh ID are also a no-op, without a revision increment.
            if ((q.Action==SeatAction.Exit || q.Action==SeatAction.ExitConfirm) && !v.HasActor(q.Actor)) return SeatResult.Exited;
            if (q.Action==SeatAction.Exit && v.Exiting(q.Seat) && v.Occupant(q.Seat)==q.Actor) return SeatResult.Exiting;
            if (q.OccupancyRevision != v.OccupancyRevision) return SeatResult.StaleRevision;
            if (v.OccupancyRevision == ulong.MaxValue) return SeatResult.Unavailable;
            if (q.Action==SeatAction.Enter) {
                if (v.Occupant(q.Seat)!=0 || v.Pending(q.Seat)!=0) return SeatResult.SeatTaken;
                foreach (var record in registry.Capture(EntityTypeId.Vehicle)) { VehicleState other; if (!record.Tombstone && VehicleState.TryDecode(record.Payload,out other) && other.HasActor(q.Actor)) return SeatResult.AlreadySeated; }
                if (eligible==null || !eligible(q.Actor,q.Vehicle,q.Seat,q.Action,q.Door)) return SeatResult.Ineligible;
                if (q.Seat==SeatRole.Driver) v.PendingDriver=q.Actor; else v.PendingPassenger=q.Actor;
                reservations[q.Vehicle]=now+5;
            } else if (q.Action==SeatAction.Confirm) {
                if (v.Pending(q.Seat)!=q.Actor || eligible==null || !eligible(q.Actor,q.Vehicle,q.Seat,q.Action,q.Door)) return SeatResult.Ineligible;
                if (q.Seat==SeatRole.Driver) { v.Driver=q.Actor; v.PendingDriver=0; } else { v.Passenger=q.Actor; v.PendingPassenger=0; }
                if (v.PendingDriver==0 && v.PendingPassenger==0) reservations.Remove(q.Vehicle);
            } else {
                if (v.Occupant(q.Seat)!=q.Actor && v.Pending(q.Seat)!=q.Actor) return SeatResult.WrongActor;
                if (eligible==null || !eligible(q.Actor,q.Vehicle,q.Seat,q.Action,q.Door)) return SeatResult.Ineligible;
                if (q.Action==SeatAction.Exit && v.Occupant(q.Seat)==q.Actor) {
                    if(q.Seat==SeatRole.Driver) v.ExitingDriver=true; else v.ExitingPassenger=true;
                    reservations[q.Vehicle]=now+5;
                } else {
                    if(q.Action==SeatAction.ExitConfirm && !v.Exiting(q.Seat)) return SeatResult.Ineligible;
                    if (q.Seat==SeatRole.Driver) { v.Driver=v.PendingDriver=0; v.ExitingDriver=false; } else { v.Passenger=v.PendingPassenger=0; v.ExitingPassenger=false; }
                }
                inputs.Remove(q.Vehicle); inputTimes.Remove(q.Vehicle);
                if(v.PendingDriver==0 && v.PendingPassenger==0 && !v.ExitingDriver && !v.ExitingPassenger) reservations.Remove(q.Vehicle);
            }
            if(oldDriver!=v.Driver) { AdvanceAuthority(v); motionLeases.Remove(q.Vehicle); }
            v.OccupancyRevision++; a.State=v; registry.CommitState(q.Vehicle);
            return q.Action==SeatAction.Enter ? SeatResult.Reserved : q.Action==SeatAction.Confirm ? SeatResult.Confirmed : v.Exiting(q.Seat) ? SeatResult.Exiting : SeatResult.Exited;
        }
        public void RemoveActor(NetworkEntityRegistry registry,byte actor)
        {
            if (!registry.IsHost || actor<1 || actor>2) return;
            foreach (var record in registry.Capture(EntityTypeId.Vehicle)) {
                var a=registry.GetBinding(record.Entity) as IVehicleAdapter; if (a==null || !a.State.HasActor(actor)) continue;
                var v=a.State; if (v.Driver==actor) {v.Driver=0; AdvanceAuthority(v); motionLeases.Remove(record.Entity);} if (v.Passenger==actor) v.Passenger=0; if (v.PendingDriver==actor) v.PendingDriver=0; if (v.PendingPassenger==actor) v.PendingPassenger=0;
                if(v.Driver==0) v.ExitingDriver=false; if(v.Passenger==0) v.ExitingPassenger=false;
                if (v.OccupancyRevision<ulong.MaxValue) v.OccupancyRevision++; a.State=v; registry.CommitState(record.Entity); inputs.Remove(record.Entity); inputTimes.Remove(record.Entity);
                if (v.PendingDriver==0 && v.PendingPassenger==0 && !v.ExitingDriver && !v.ExitingPassenger) reservations.Remove(record.Entity);
            }
            highWater[actor]=0;
        }
        public void Tick(NetworkEntityRegistry registry,double now)
        {
            if (!registry.IsHost) return;
            foreach (var key in new List<EntityRef>(reservations.Keys)) if (now>=reservations[key]) {
                reservations.Remove(key); var a=registry.GetBinding(key) as IVehicleAdapter; if (a==null) continue;
                if(a.State.ExitingDriver) {a.State.Driver=0; AdvanceAuthority(a.State); motionLeases.Remove(key);} if(a.State.ExitingPassenger) a.State.Passenger=0;
                a.State.ExitingDriver=a.State.ExitingPassenger=false;
                a.State.PendingDriver=a.State.PendingPassenger=0; if (a.State.OccupancyRevision<ulong.MaxValue) a.State.OccupancyRevision++; registry.CommitState(key);
            }
        }
        private static void AdvanceAuthority(VehicleState state)
        { if(state.AuthorityGeneration==ulong.MaxValue) throw new InvalidOperationException("Vehicle authority exhausted."); state.AuthorityGeneration++; if(state.Driver==0) state.EngineRunning=false; }
        // Passenger revisions never change the driver's simulation generation.
        // A wide displacement envelope rejects teleports without policing normal collisions.
        public bool AcceptMotion(NetworkEntityRegistry registry,VehicleMotion q,ulong session,byte actor,bool ready,double now,out bool severe)
        {
            severe=false;
            if(!registry.IsHost || !ready || !q.Valid || actor==0 || q.Actor!=actor || q.SessionId!=session || q.Epoch!=registry.SceneEpoch) return false;
            var a=registry.GetBinding(q.Vehicle) as IVehicleAdapter;
            if(a==null || !a.IsAlive || a.State.Driver!=actor || a.State.AuthorityGeneration!=q.AuthorityGeneration) return false;
            motionLeases.TryGetValue(q.Vehicle,out var old);
            bool same=old!=null && old.Generation==q.AuthorityGeneration && old.Received;
            if(same && (!Protocol.IsNewer(q.Sequence,old.Sequence) || unchecked((int)(q.Tick-old.Tick))<=0)) return false;
            double elapsed=same ? Math.Min(2,Math.Max(.05,Math.Max(now-old.Seen,unchecked((uint)(q.Tick-old.Tick))*.001))) : .05;
            double speed=Math.Sqrt(a.State.Vx*a.State.Vx+a.State.Vy*a.State.Vy+a.State.Vz*a.State.Vz);
            double envelope=8+Math.Max(speed,Math.Sqrt(q.Vx*q.Vx+q.Vy*q.Vy+q.Vz*q.Vz))*elapsed+40*elapsed*elapsed;
            double dx=q.X-a.State.X,dy=q.Y-a.State.Y,dz=q.Z-a.State.Z;
            if(dx*dx+dy*dy+dz*dz>envelope*envelope || Math.Abs(q.FuelUsed-(same ? old.FuelUsed : 0))>3+4*elapsed) {severe=true;return false;}
            if(old==null) motionLeases[q.Vehicle]=old=new MotionLease();
            old.PreviousSpeed=same ? old.Speed : (float)speed; old.Speed=(float)Math.Sqrt(q.Vx*q.Vx+q.Vy*q.Vy+q.Vz*q.Vz);
            old.Generation=q.AuthorityGeneration; old.Sequence=q.Sequence; old.Tick=q.Tick; old.Seen=now; old.FuelUsed=q.FuelUsed; old.Received=true;
            q.Apply(a.State); return true;
        }
        public void RecoverMotion(NetworkEntityRegistry registry,EntityRef vehicle,double now)
        {
            var a=registry.GetBinding(vehicle) as IVehicleAdapter; if(!registry.IsHost || a==null || a.State.Driver==0) return;
            AdvanceAuthority(a.State); a.State.Vx=a.State.Vy=a.State.Vz=a.State.Ax=a.State.Ay=a.State.Az=0;
            motionLeases[vehicle]=new MotionLease {Generation=a.State.AuthorityGeneration,Seen=now};
            registry.CommitState(vehicle);
        }
        public bool RecentMotion(NetworkEntityRegistry registry,EntityRef vehicle,byte actor,ulong generation,double now)
        {
            var a=registry.GetBinding(vehicle) as IVehicleAdapter;
            return a!=null && a.State.Driver==actor && a.State.AuthorityGeneration==generation && motionLeases.TryGetValue(vehicle,out var lease) && lease.Received && lease.Generation==generation && now-lease.Seen<=.75;
        }
        public float RecentImpactSpeed(EntityRef vehicle) => motionLeases.TryGetValue(vehicle,out var lease) ? Math.Max(lease.Speed,lease.PreviousSpeed) : 0;
        public bool AcceptInput(NetworkEntityRegistry registry,DriverInput q,ulong session,byte authenticatedActor,bool ready,double now)
        {
            if (!registry.IsHost || !ready || !VehicleProtocol.Valid(q) || q.Actor!=authenticatedActor || q.SessionId!=session || q.Epoch!=registry.SceneEpoch) return false;
            var a=registry.GetBinding(q.Vehicle) as IVehicleAdapter;
            if (a==null || !a.IsAlive || a.State.Driver!=q.Actor || a.State.OccupancyRevision!=q.OccupancyRevision || a.State.ExitingDriver || a.State.ExitingPassenger) return false;
            DriverInput old; if (inputs.TryGetValue(q.Vehicle,out old) && old.Actor==q.Actor && old.OccupancyRevision==q.OccupancyRevision && !Protocol.IsNewer(q.Sequence,old.Sequence)) return false;
            inputs[q.Vehicle]=q; inputTimes[q.Vehicle]=now; return true;
        }
        public DriverInput Latest(EntityRef vehicle,double now)
        { DriverInput q; double t; return inputs.TryGetValue(vehicle,out q) && inputTimes.TryGetValue(vehicle,out t) && now-t<=.35 ? q : null; }
        public static bool BothConfirmed(NetworkEntityRegistry registry)
        {
            foreach (var record in registry.Capture(EntityTypeId.Vehicle)) { VehicleState v;
                if (record.Type!=EntityTypeId.Vehicle || record.Tombstone || !VehicleState.TryDecode(record.Payload,out v)) continue;
                if (v.PendingDriver!=0 || v.PendingPassenger!=0 || v.ExitingDriver || v.ExitingPassenger) return false;
                var binding=registry.GetBinding(record.Entity);
                if (v.Driver!=0 && v.Passenger!=0 && v.Driver!=v.Passenger && binding!=null && binding.IsAlive) return true;
            }
            return false;
        }
    }
}

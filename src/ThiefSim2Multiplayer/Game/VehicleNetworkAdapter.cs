using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using ThiefSim2Multiplayer.Core.World;
using ThiefSim2Multiplayer.Core.Networking;

namespace ThiefSim2Multiplayer.Game
{
    internal sealed class VehicleNetworkAdapter : UnityNetworkEntityAdapter, IVehicleAdapter, IVehicleMotionAdapter
    {
        internal readonly Component Car, Drive;
        internal readonly Rigidbody Body;
        internal readonly Transform DriverSeat, PassengerSeat, Exit;
        private Component[] nativeDoors = Array.Empty<Component>();
        private readonly HashSet<Component> rearPassengerDoors = new HashSet<Component>();
        internal Component[] NativeDoors => nativeDoors;
        internal bool HasRearPassengerDoor => rearPassengerDoors.Count != 0;
        internal bool IsRearPassengerDoor(Component door) => door && rearPassengerDoors.Contains(door);
        internal bool IsNativeFrontEntryDoor(Component door) => door &&
            (GameAccess.Get(Car,"car_door_L") as Component==door || GameAccess.Get(Car,"car_door_R") as Component==door);
        private readonly bool originalKinematic, inputEnabled;
        private readonly Behaviour input;
        private readonly byte localActor;
        private readonly bool host;
        private ulong appliedAuthority;
        private byte appliedDriver;
        private bool authorityApplied, simulating;
        private readonly object save;
        private readonly float originalFuel;
        private float fuelUsed, acceptedFuelUsed;
        internal float NativeFuel => (float)GameAccess.Get(save,"gasTankLeft");
        internal void ObserveFuel(float before) {fuelUsed+=before-NativeFuel;}
        internal bool Simulates => VehicleMultiplayerPolicy.LocalPeerSimulates(host,localActor,State.Driver);
        private readonly Action readNativeInput;
        internal void ReadNativeInput() => readNativeInput();
        private readonly Func<float> speed;
        internal float Speed => speed();
        internal float MotionSpeed => Simulates ? Body.velocity.magnitude : new Vector3(State.Vx,State.Vy,State.Vz).magnitude;
        internal EntityRef Entity;
        internal ulong PresentationKey;
        public VehicleState State { get; set; }
        private Vector3 target;
        private Quaternion rotation;
        private bool received;
        private readonly VehicleMotionBuffer motion=new VehicleMotionBuffer();
        private VehicleMotion lastSentMotion;
        private bool hasSentMotion;
        private double lastMotionAt;
        internal bool MotionChanged(VehicleMotion m,double now)
        {
            bool changed=!hasSentMotion || now-lastMotionAt>=1 ||
                m.X!=lastSentMotion.X || m.Y!=lastSentMotion.Y || m.Z!=lastSentMotion.Z ||
                m.Qx!=lastSentMotion.Qx || m.Qy!=lastSentMotion.Qy || m.Qz!=lastSentMotion.Qz || m.Qw!=lastSentMotion.Qw ||
                m.Vx!=lastSentMotion.Vx || m.Vy!=lastSentMotion.Vy || m.Vz!=lastSentMotion.Vz || m.Ax!=lastSentMotion.Ax || m.Ay!=lastSentMotion.Ay || m.Az!=lastSentMotion.Az ||
                m.EngineRunning!=lastSentMotion.EngineRunning || m.Fuel!=lastSentMotion.Fuel || m.FuelUsed!=lastSentMotion.FuelUsed || m.Steer!=lastSentMotion.Steer || m.Throttle!=lastSentMotion.Throttle || m.Brake!=lastSentMotion.Brake || m.Handbrake!=lastSentMotion.Handbrake;
            if(changed) {lastSentMotion=m;lastMotionAt=now;hasSentMotion=true;} return changed;
        }
        private readonly RigidbodyInterpolation originalInterpolation;
        private Component remoteAlarm;
        private bool remoteAlarmRemoved;
        internal bool RemoteControlled { get; private set; }
        private bool storageTagOwned;
        internal bool StorageAvailable(bool engine)
        {
            var door=GameAccess.Get(Car,"car_door_R") as Component;
            return !engine && (GameAccess.Bool(Car,"is_sitting_inside") || State.Driver!=0 || State.Passenger!=0 || door && GameAccess.Bool(door,"isOpen"));
        }
        internal void RefreshStorageTag(bool available)
        {
            var box=GameAccess.Get(Car,"car_box_script") as Component;
            if(!box) return;
            string tag=available ? "CarBox" : "Untagged";
            if(box.gameObject.tag!=tag) {storageTagOwned=true; box.gameObject.tag=tag;}
        }
        internal void RemoteDriving(bool running)
        {
            RemoteControlled=true;
            GameAccess.Set(Car,"engine_running",State.Driver!=0 && running);
        }
        internal void RemoteEngine(bool running)
        {
            // Native RunCar removes this child Rigidbody before moving. Running that
            // coroutine would take over the Host passenger, so mirror only this
            // verified vehicle-side lifetime behavior and its native ExitCar default.
            if(running && !remoteAlarmRemoved) {
                var alarm=GameAccess.Get(Car,"alarm_script") as Component;
                var alarmBody=alarm ? alarm.GetComponent<Rigidbody>() : null;
                if(alarmBody) { remoteAlarm=alarm; remoteAlarmRemoved=true; UnityEngine.Object.Destroy(alarmBody); }
            } else if(!running && remoteAlarmRemoved) {
                if(remoteAlarm && !remoteAlarm.GetComponent<Rigidbody>()) remoteAlarm.gameObject.AddComponent<Rigidbody>().isKinematic=true;
                remoteAlarm=null; remoteAlarmRemoved=false;
            }
        }
        internal VehicleNetworkAdapter(Component car,byte actor=1,bool isHost=true) : base(car)
        {
            localActor=actor; host=isHost;
            Car=car; Body=car.GetComponent<Rigidbody>(); Drive=car.GetComponent(Type.GetType("EVP.VehicleController, Assembly-CSharp",true));
            input=(Behaviour)car.GetComponent(Type.GetType("EVP.VehicleStandardInput, Assembly-CSharp",true));
            DriverSeat=GameAccess.Get(car,"sit_transform") as Transform; PassengerSeat=RearPassengerSeat.Resolve(car); Exit=GameAccess.Get(car,"leave_car_transform") as Transform;
            if (!Body || !Drive || !input || !DriverSeat || !Exit || !DriverSeat.IsChildOf(car.transform) || !Exit.IsChildOf(car.transform)) throw new ArgumentException("Native vehicle contract missing.");
            readNativeInput=(Action)Delegate.CreateDelegate(typeof(Action),input,input.GetType().GetMethod("FixedUpdate",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic));
            speed=(Func<float>)Delegate.CreateDelegate(typeof(Func<float>),Drive,Drive.GetType().GetProperty("speed").GetGetMethod());
            if (!PassengerSeat || !PassengerSeat.IsChildOf(car.transform) || Vector3.Distance(PassengerSeat.position,DriverSeat.position)<.25f || Vector3.Distance(PassengerSeat.position,DriverSeat.position)>3f || Vector3.Dot(PassengerSeat.forward,DriverSeat.forward)<.5f) PassengerSeat=null;
            var nativeDoorType=Type.GetType("Door, Assembly-CSharp",true);
            nativeDoors=car.GetComponentsInChildren(nativeDoorType,true).Cast<Component>().ToArray();
            if(PassengerSeat) foreach(var door in nativeDoors) {
                if(!door || !door.gameObject.activeInHierarchy || IsNativeFrontEntryDoor(door)) continue;
                var colliders=door.GetComponents<Collider>().Where(c=>c && c.enabled && c.gameObject.activeInHierarchy).ToArray();
                if(colliders.Length==0) continue;
                var delta=door.transform.position-PassengerSeat.position;
                float side=Mathf.Abs(Vector3.Dot(delta,Car.transform.right.normalized));
                float row=Mathf.Abs(Vector3.Dot(delta,DriverSeat.forward.normalized));
                float vertical=Mathf.Abs(Vector3.Dot(delta,Car.transform.up.normalized));
                float clearance=colliders.Min(c=>(c.ClosestPoint(PassengerSeat.position)-PassengerSeat.position).magnitude);
                if(VehicleDoorPolicy.IsRearPassengerDoor(true,false,true,side,row,vertical,clearance)) rearPassengerDoors.Add(door);
            }
            if(rearPassengerDoors.Count==0) PassengerSeat=null;
            originalKinematic=Body.isKinematic; inputEnabled=input.enabled; originalInterpolation=Body.interpolation;
            var player=GameAccess.Get(car,"PS"); save=GameAccess.Get(player,"saveGameScript"); originalFuel=(float)GameAccess.Get(save,"gasTankLeft");
            State=new VehicleState { PassengerSeat=PassengerSeat }; SampleMotion();
            PresentationKey=VehiclePresentation.Identity(car);
        }
        public override EntityTypeId EntityType => EntityTypeId.Vehicle;
        // Entity existence is independent of occupancy, activation, camera and seat references.
        public override bool IsAlive => base.IsAlive && Car && Car.gameObject.scene.isLoaded;
        internal bool NativeDestroyed => !Car;
        internal bool Operational => IsAlive && Car.gameObject.activeInHierarchy && Body && Drive && DriverSeat && Exit && (!State.PassengerSeat || PassengerSeat);
        internal void SampleMotion()
        {
            if (!Car || !Body) return;
            if(authorityApplied && !Simulates) {if(host) State.Fuel=NativeFuel; return;} // Never replace accepted state with the delayed rendered replica.
            var p=Car.transform.position; var q=Car.transform.rotation; var v=Body.velocity;
            State.X=p.x; State.Y=p.y; State.Z=p.z; State.Qx=q.x; State.Qy=q.y; State.Qz=q.z; State.Qw=q.w; State.Vx=v.x; State.Vy=v.y; State.Vz=v.z;
            var angular=Body.angularVelocity; State.Ax=angular.x; State.Ay=angular.y; State.Az=angular.z;
            State.EngineRunning=GameAccess.Bool(Car,"engine_running");
            State.Fuel=(float)GameAccess.Get(save,"gasTankLeft");
        }
        public override byte[] CaptureState() => State.Encode();
        public override bool ValidateState(byte[] bytes) { VehicleState v; return VehicleState.TryDecode(bytes,out v) && v.PassengerSeat==(bool)PassengerSeat; }
        public override void ApplyState(byte[] bytes)
        {
            VehicleState v; if (!ValidateState(bytes) || !VehicleState.TryDecode(bytes,out v)) throw new ArgumentException("Vehicle capability/state mismatch.");
            bool recovery=authorityApplied && appliedDriver==localActor && v.Driver==localActor && appliedAuthority!=v.AuthorityGeneration;
            State=v;
            if (!received) { target=new Vector3(v.X,v.Y,v.Z); rotation=new Quaternion(v.Qx,v.Qy,v.Qz,v.Qw); Body.position=target; Body.rotation=rotation; }
            received=true; SyncAuthority(recovery);
        }
        public void ApplyMotion(VehicleMotion m,double receivedAt)
        {
            SyncAuthority();
            if(Simulates || m.AuthorityGeneration!=State.AuthorityGeneration || m.Actor!=State.Driver) return;
            received=true; motion.Add(m,receivedAt);
            if(host && State.Driver!=0) {GameAccess.Set(save,"gasTankLeft",NativeFuel-(m.FuelUsed-acceptedFuelUsed)); acceptedFuelUsed=m.FuelUsed; State.Fuel=NativeFuel;}
            else if(!SharedVehicleController.LocalDriverSimulating()) GameAccess.Set(save,"gasTankLeft",m.Fuel);
            RemoteDriving(m.EngineRunning); RemoteEngine(State.Driver!=0);
            // Replica audio/gauges consume native presentation fields, never forces.
            GameAccess.Set(Drive,"steerInput",m.Steer); GameAccess.Set(Drive,"throttleInput",m.Throttle); GameAccess.Set(Drive,"brakeInput",m.Brake); GameAccess.Set(Drive,"handbrakeInput",m.Handbrake);
            GameAccess.Set(Drive,"m_speed",Vector3.Dot(new Vector3(m.Vx,m.Vy,m.Vz),new Quaternion(m.Qx,m.Qy,m.Qz,m.Qw)*Vector3.forward));
        }
        internal VehicleMotion Motion()
        {
            SampleMotion(); var a=Body.angularVelocity;
            return new VehicleMotion {Vehicle=Entity,Actor=State.Driver,AuthorityGeneration=State.AuthorityGeneration,X=State.X,Y=State.Y,Z=State.Z,Qx=State.Qx,Qy=State.Qy,Qz=State.Qz,Qw=State.Qw,Vx=State.Vx,Vy=State.Vy,Vz=State.Vz,Ax=a.x,Ay=a.y,Az=a.z,Fuel=State.Fuel,FuelUsed=fuelUsed,EngineRunning=State.EngineRunning,Steer=(float)GameAccess.Get(Drive,"steerInput"),Throttle=(float)GameAccess.Get(Drive,"throttleInput"),Brake=(float)GameAccess.Get(Drive,"brakeInput"),Handbrake=(float)GameAccess.Get(Drive,"handbrakeInput")};
        }
        internal void SyncAuthority(bool recovery=false)
        {
            bool owns=Simulates;
            bool changed=!authorityApplied || appliedAuthority!=State.AuthorityGeneration || appliedDriver!=State.Driver || simulating!=owns;
            if(!changed && !recovery) return;
            bool wasReplica=authorityApplied && !simulating;
            motion.Clear(); hasSentMotion=false;
            fuelUsed=acceptedFuelUsed=0;
            if(owns && (wasReplica || recovery)) {
                Body.isKinematic=true; Body.position=new Vector3(State.X,State.Y,State.Z); Body.rotation=new Quaternion(State.Qx,State.Qy,State.Qz,State.Qw);
                Body.isKinematic=!(State.Driver==localActor || !originalKinematic || State.AuthorityGeneration>1);
                if(!Body.isKinematic) {Body.velocity=new Vector3(State.Vx,State.Vy,State.Vz); Body.angularVelocity=new Vector3(State.Ax,State.Ay,State.Az);}
                GameAccess.Set(save,"gasTankLeft",State.Fuel);
            }
            if(owns) {
                input.enabled=inputEnabled; Body.interpolation=originalInterpolation;
                if(State.Driver==localActor) {Body.isKinematic=false; remoteAlarm=null; remoteAlarmRemoved=false;}
                if(State.Driver==0 && RemoteControlled) {RemoteDriving(false); RemoteEngine(false); GameAccess.Set(Drive,"throttleInput",0f); GameAccess.Set(Drive,"brakeInput",1f); GameAccess.Set(Drive,"handbrakeInput",1f);}
            } else {Body.isKinematic=true; Body.interpolation=RigidbodyInterpolation.None; input.enabled=false; if(!host && !SharedVehicleController.LocalDriverSimulating()) GameAccess.Set(save,"gasTankLeft",State.Fuel); RemoteDriving(State.EngineRunning); RemoteEngine(State.Driver!=0);}
            appliedAuthority=State.AuthorityGeneration; appliedDriver=State.Driver; authorityApplied=true; simulating=owns;
        }
        internal void Follow(double now)
        {
            if (!Operational) return;
            SyncAuthority();
            if(Simulates || !received) return;
            Body.isKinematic=true; Body.interpolation=RigidbodyInterpolation.None; input.enabled=false;
            VehicleMotion from,to; float t;
            if(!motion.SampleAt(now,out from,out to,out t)) return;
            Body.position=Vector3.Lerp(new Vector3(from.X,from.Y,from.Z),new Vector3(to.X,to.Y,to.Z),t);
            Body.rotation=Quaternion.Slerp(new Quaternion(from.Qx,from.Qy,from.Qz,from.Qw),new Quaternion(to.Qx,to.Qy,to.Qz,to.Qw),t);
        }
        internal void Restore() {
            bool replica=received, remoteDriver=RemoteControlled;
            bool nativeDriver=Car && GameAccess.Bool(Car,"is_sitting_inside");
            if(!nativeDriver) RemoteEngine(false); else {remoteAlarm=null; remoteAlarmRemoved=false;}
            if(remoteDriver && Car && !nativeDriver) GameAccess.Set(Car,"engine_running",false);
            if (Body && (replica || remoteDriver) && !nativeDriver) Body.isKinematic=originalKinematic;
            if(Drive && remoteDriver && !nativeDriver) { GameAccess.Set(Drive,"throttleInput",0f); GameAccess.Set(Drive,"brakeInput",1f); GameAccess.Set(Drive,"handbrakeInput",1f); }
            if (input && replica) input.enabled=inputEnabled;
            if (Body && replica) Body.interpolation=originalInterpolation;
            if(!host && authorityApplied) GameAccess.Set(save,"gasTankLeft",originalFuel);
            motion.Clear(); received=false;
            hasSentMotion=false;
            RemoteControlled=false;
            authorityApplied=false;
            if(storageTagOwned && Car) {
                var box=GameAccess.Get(Car,"car_box_script") as Component; var door=GameAccess.Get(Car,"car_door_R") as Component;
                if(box) box.gameObject.tag=!GameAccess.Bool(Car,"engine_running") && (GameAccess.Bool(Car,"is_sitting_inside") || door && GameAccess.Bool(door,"isOpen")) ? "CarBox" : "Untagged";
            }
            storageTagOwned=false;
        }
        public override void Release() { AssertThread(); Restore(); base.Release(); }
    }
}

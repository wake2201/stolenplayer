using System;
using System.Collections;
using System.Linq;
using UnityEngine;
using ThiefSim2Multiplayer.Core;
using ThiefSim2Multiplayer.Core.Networking;
using ThiefSim2Multiplayer.Core.World;
using ThiefSim2Multiplayer.Game;

namespace ThiefSim2Multiplayer
{
    public sealed partial class Plugin
    {
        // PID/save-bound development only. One native game and a managed peer; never two-machine acceptance.
        private sealed class FixtureRecord : INetworkEntityAdapter
        {
            public EntityTypeId EntityType {get;}
            public bool IsAlive {get;private set;}=true;
            private byte[] state;
            internal FixtureRecord(EntityTypeId type,byte[] payload) { EntityType=type; state=payload; }
            public byte[] CaptureState() => (byte[])state.Clone();
            public bool ValidateState(byte[] payload) => payload!=null && payload.Length<=WorldLimits.MaxEntityPayload;
            public void ApplyState(byte[] payload) { state=(byte[])payload.Clone(); }
            public void Release() { IsAlive=false; }
        }
        private static INetworkEntityAdapter FixtureAdapter(EntityStateRecord record)
        {
            if(record.Type!=EntityTypeId.Vehicle) return new FixtureRecord(record.Type,record.Payload);
            var a=new ManagedVehicleAdapter(); a.ApplyState(record.Payload); return a;
        }
        internal static void MirrorFixture(NetworkEntityRegistry target,EntityStateRecord[] records)
        {
            foreach(var record in records.Where(r=>r.Entity.Id.Scope==EntityScope.Static && !r.Tombstone))
                if(target.Lookup(record.Entity)==null) target.RegisterStatic(StaticEntityDescriptor.ParseCanonical(record.Descriptor),FixtureAdapter(record),record.Ownership);
        }
        internal static int FixturePort()
        {
            using(var socket=new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork,System.Net.Sockets.SocketType.Dgram,System.Net.Sockets.ProtocolType.Udp)) {
                socket.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback,0)); return ((System.Net.IPEndPoint)socket.LocalEndPoint).Port;
            }
        }
        private IEnumerator NativeContinuityFixture()
        {
            if(network.Session.State!=SessionState.Offline) throw new InvalidOperationException("Native continuity fixture requires offline backed-up gameplay.");
            int checks=0; NetworkManager peer=null;
            var locator=new PlayerLocator(); var p=locator.Find();
            if(!p || GameAccess.Bool(p,"is_in_vehicle")) throw new InvalidOperationException("Native on-foot player required.");
            void Check(bool ok,string label) { if(!ok) throw new InvalidOperationException("Native continuity fixture FAIL: "+label); checks++; Trace("[TS2MP] NATIVE CONTINUITY PASS: "+label); }
            double deadline=network.Now+15;
            IEnumerator Await(Func<bool> ready,string phase,Action pump=null,int seconds=15) {
                deadline=network.Now+seconds;
                while(!ready()) {
                    try {
                        peer.Poll(); pump?.Invoke();
                        if(network.Now>deadline || network.Scenes.State==SceneTransitionState.Failed || peer.Scenes.State==SceneTransitionState.Failed)
                            throw new InvalidOperationException("Native continuity phase failed: "+phase+" native="+network.Scenes.State+"/"+network.World.State+" managed="+peer.Scenes.State+"/"+peer.World.State+" error="+network.Scenes.Error+"/"+network.LastError+"/"+peer.LastError);
                    } catch { network.Disconnect("Controlled fixture failed."); peer.Dispose(); throw; }
                    yield return null;
                }
                Check(true,phase);
            }
            WorldTimeState canonical=default(WorldTimeState);
            try {
                peer=new NetworkManager(network.BuildFingerprintForDiagnostics,Trace);
                peer.World.PrepareBaseline=s=>MirrorFixture(peer.World.Registry,s.Entities);
                peer.World.AdapterFactory=type=>new FixtureRecord(type,new byte[0]);
                peer.HostSceneRequested+=s=>peer.MarkSceneLoaded(s.SceneKey);
                Check(network.Host(FixturePort(),"NativeHost"),"production native Host starts");
                yield return null; yield return null;
                peer.Join("127.0.0.1",network.Port,"ManagedClient");
                yield return Await(()=>network.GameplayReady && peer.GameplayReady,"native Host readiness + managed baseline/time");
                var a=network.World.Registry.Capture().Select(r=>network.World.Registry.GetBinding(r.Entity)).OfType<VehicleNetworkAdapter>().First(v=>v.PassengerSeat);
                var origin=p.transform.position;
                var pose=new PlayerTransform {X=a.DriverSeat.position.x,Y=a.DriverSeat.position.y,Z=a.DriverSeat.position.z,State=new PlayerReplicationState {SeatEligibility=SeatEligibility.Eligible,EquippedItemId=-1}};
                void SendPose() { peer.SendState(pose); }
                SendPose(); yield return Await(()=>network.Telemetry.HasPose,"authenticated remote actor outside vehicle",SendPose);
                peer.RequestSeat(a.Entity,SeatRole.Driver,SeatAction.Enter);
                yield return Await(()=>a.State.PendingDriver==2 && (peer.World.Registry.GetBinding(a.Entity) as IVehicleAdapter)?.State.PendingDriver==2,"Client driver reservation",SendPose);
                pose.State.Vehicle=new VehicleOccupancyState {Seat=SeatRole.Driver,PresentationKey=a.PresentationKey}; pose.State.SeatEligibility=SeatEligibility.Ineligible;
                double nextConfirm=0;
                yield return Await(()=>a.State.Driver==2,"Client driver confirmed from authenticated seat pose",()=> {SendPose(); if(network.Now>=nextConfirm) {nextConfirm=network.Now+.3; peer.RequestSeat(a.Entity,SeatRole.Driver,SeatAction.Confirm);} });
                p.transform.position=a.Exit.position;
                var originalSeatValidator=network.ValidateSeat;
                network.ValidateSeat=(actor,vehicle,seat,action,door)=>seat==SeatRole.Passenger && action==SeatAction.Enter && actor==network.Session.LocalPlayerId
                    ? SharedVehicleController.Eligible(p) && Vector3.Distance(p.transform.position,a.Car.transform.position)<4
                    : originalSeatValidator(actor,vehicle,seat,action,door);
                network.RequestSeat(a.Entity,SeatRole.Passenger,SeatAction.Enter,new EntityRef(new NetworkEntityId(EntityScope.Static,0x7FFFFFFFFFFFFFFEUL),1));
                network.ValidateSeat=originalSeatValidator;
                yield return Await(()=>a.State.Passenger==1,"production original Host passenger attaches and confirms",SendPose);
                Check(!p.GetComponent<CharacterController>().enabled && GameAccess.Get(p,"MainCam") as Camera,"native Host passenger retains original camera");
                canonical=network.LastWorldTime;
                int hours=canonical.TotalHours;
                scenes.OpenPassengerSleep(p);
                var gm=GameAccess.Get(p,"GM") as Component; var pass=GameAccess.Get(gm,"passTimeScript") as Component;
                GameAccess.Set(pass,"currentHr",((int)canonical.Hour+1)%24);
                GameAccess.Call(pass,"PassStart");
                yield return Await(()=>network.Sleep.Phase==CooperativeSleepPhase.Running,"native sleep starts after managed acknowledgement",SendPose);
                yield return Await(()=>!network.Sleep.Frozen && !peer.Sleep.Frozen,"native accelerated sleep wake/result acknowledgement",SendPose,30);
                canonical=network.LastWorldTime;
                Check(canonical.TotalHours>hours && peer.LastWorldTime.TotalHours==canonical.TotalHours,"native hourly effects observed once and replicated");
                Check(network.GameplayReady && VehicleAuthority.BothConfirmed(network.World.Registry) && (GameAccess.Get(p,"MainCam") as Camera).enabled,"passenger sleep restores camera and confirmed occupancy");
                ulong token=network.Session.SessionId;
                GameAccess.Call(gm,"SelectHideout");
                yield return Await(()=>network.Scenes.Target.Map=="PlayerHideout" && network.GameplayReady && peer.GameplayReady,"native mission-to-hideout Host load and managed follower",null,120);
                p=locator.Find(); Check(p && GameAccess.Bool(p,"is_in_vehicle") && !p.GetComponent<CharacterController>().enabled && vehicles.LocalOccupancy.Seat==SeatRole.Passenger && network.Session.SessionId==token,"native Host hideout retains confirmed rear seat and connection");
                network.Disconnect("Controlled Host fixture phase complete."); peer.Dispose(); peer=null;
                yield return null;

                peer=new NetworkManager(network.BuildFingerprintForDiagnostics,Trace);
                peer.ValidateSeat=(actor,entity,seat,action,door)=>actor==1 || network.ValidateSeat(actor,entity,seat,action,door);
                peer.World.PrepareRegistry=()=> { network.World.PrepareRegistry?.Invoke(); MirrorFixture(peer.World.Registry,network.World.Registry.Capture()); };
                peer.CaptureTravelOccupants=target=> {
                    var occupied=peer.World.Registry.Capture().Where(r=>r.Type==EntityTypeId.Vehicle).Select(r=>peer.World.Registry.GetBinding(r.Entity)).OfType<IVehicleAdapter>().FirstOrDefault(v=>v.State.Driver!=0 || v.State.Passenger!=0);
                    return occupied==null ? target : new NetworkSceneDescriptor(target.Map,target.VehicleVariant,target.SecondVehicleAvailable,occupied.State.Driver,occupied.State.Passenger);
                };
                Check(peer.Host(FixturePort(),"ManagedHost"),"managed Host starts for native Client follower");
                var mission=new NetworkSceneDescriptor("MadisonSt"); peer.SetLocalScene(true,mission.SceneKey); peer.PublishStableScene(mission);
                network.Join("127.0.0.1",peer.Port,"NativeClient");
                void TimeBaseline() { peer.PublishTime(canonical); }
                yield return Await(()=>network.GameplayReady && peer.GameplayReady,"native Client automatically late-joins mission with fresh managers/time",TimeBaseline,120);
                p=locator.Find();
                Check(p && p.GetComponent<CharacterController>().enabled && !GameAccess.Bool(p,"is_in_vehicle"),"native mission late-join retains first person outside occupied seats");
                token=network.Session.SessionId;
                uint oldEpoch=network.World.Registry.SceneEpoch;
                peer.BeginSceneTransition(new NetworkSceneDescriptor("PlayerHideout"));
                Check(peer.World.Registry.SceneEpoch==oldEpoch,"native Client mission return denied without both occupants");
                var record=peer.World.Registry.Capture().First(r=>r.Type==EntityTypeId.Vehicle && ((IVehicleAdapter)peer.World.Registry.GetBinding(r.Entity)).State.PassengerSeat);
                var v=(IVehicleAdapter)peer.World.Registry.GetBinding(record.Entity);
                peer.RequestSeat(record.Entity,SeatRole.Driver,SeatAction.Enter); peer.RequestSeat(record.Entity,SeatRole.Driver,SeatAction.Confirm);
                yield return Await(()=>((IVehicleAdapter)network.World.Registry.GetBinding(record.Entity)).State.Driver==1,"managed driver explicit validated fixture occupancy",TimeBaseline);
                var native=(VehicleNetworkAdapter)network.World.Registry.GetBinding(record.Entity); p.transform.position=native.Exit.position;
                var originalPeerSeatValidator=peer.ValidateSeat;
                peer.ValidateSeat=(actor,vehicle,seat,action,door)=>actor==1 || seat==SeatRole.Passenger && action==SeatAction.Enter && vehicle.Equals(record.Entity) && door.Id.IsValid && door.Generation!=0 || network.ValidateSeat(actor,vehicle,seat,action,door);
                vehicles.RequestEnter(native,SeatRole.Passenger,new EntityRef(new NetworkEntityId(EntityScope.Static,0x7FFFFFFFFFFFFFFEUL),1));
                yield return Await(()=>v.State.PendingPassenger==2,"controlled fixture accepts the remote passenger reservation",TimeBaseline);
                peer.ValidateSeat=originalPeerSeatValidator;
                yield return Await(()=>v.State.Passenger==2,"production native Client passenger presentation confirms",TimeBaseline);
                vehicles.RequestExit(native,SeatRole.Passenger);
                yield return Await(()=>v.State.Passenger==0 && p.GetComponent<CharacterController>().enabled,"production native Client passenger exits and confirms restoration",TimeBaseline);
                peer.RequestSeat(record.Entity,SeatRole.Driver,SeatAction.Exit); peer.RequestSeat(record.Entity,SeatRole.Driver,SeatAction.ExitConfirm);
                yield return Await(()=>((IVehicleAdapter)network.World.Registry.GetBinding(record.Entity)).State.Driver==0,"managed driver releases seat before role reversal",TimeBaseline);
                // Separate the new interaction from the passenger-entry debounce.
                // A very fast localhost exit can otherwise make this one-shot fixture
                // request earlier than a normal next gameplay interaction.
                double nextEntry=network.Now+.45;
                while(network.Now<nextEntry) {peer.Poll(); TimeBaseline(); yield return null;}
                p.transform.position=native.Exit.position; vehicles.RequestEnter(native,SeatRole.Driver);
                yield return Await(()=>v.State.Driver==2,"production native Client driver enters and confirms",TimeBaseline);
                var camera=GameAccess.Get(p,"MainCam") as Camera; var inventory=GameAccess.Get(p,"invScript");
                native.Car.GetType().GetMethod("RunEngine").Invoke(native.Car,new object[] {true});
                p.GetType().GetMethod("DisableTownSelection").Invoke(p,new object[] {true});
                peer.RequestSeat(record.Entity,SeatRole.Passenger,SeatAction.Enter,new EntityRef(new NetworkEntityId(EntityScope.Static,0x7FFFFFFFFFFFFFFEUL),1)); peer.RequestSeat(record.Entity,SeatRole.Passenger,SeatAction.Confirm);
                yield return Await(()=>v.State.Passenger==1 && peer.Vehicles.Latest(record.Entity,peer.Now)?.Actor==2,"native Client input reaches managed Host with Host passenger",TimeBaseline);
                yield return new WaitForEndOfFrame();
                Check(native.Body.isKinematic && GameAccess.Get(p,"MainCam") as Camera==camera && GameAccess.Get(p,"invScript")==inventory,"native Client keeps original driver stack and suppresses competing EVP physics");
                var stableCar=native.Car; var stableEntity=native.Entity;
                vehicles.RequestExit(native,SeatRole.Driver);
                yield return Await(()=>v.State.Driver==0 && p.GetComponent<CharacterController>().enabled,"native Client driver exit restores original controller without closing scene",TimeBaseline);
                Check(native.Car==stableCar && network.World.Registry.GetBinding(stableEntity)==native && !network.World.Registry.Lookup(stableEntity).State.Tombstone && network.GameplayReady,"native Client exit retains persistent vehicle/entity and readiness");
                p.transform.position=native.Exit.position; vehicles.RequestEnter(native,SeatRole.Driver);
                yield return Await(()=>v.State.Driver==2,"native Client driver re-enters the same vehicle",TimeBaseline);
                peer.BeginSceneTransition(new NetworkSceneDescriptor("PlayerHideout")); peer.MarkSceneLoaded(peer.Scenes.Target.SceneKey);
                yield return Await(()=>network.GameplayReady && peer.GameplayReady,"native Client automatically returns to hideout without reconnect",TimeBaseline,120);
                p=locator.Find();
                Check(p && !p.GetComponent<CharacterController>().enabled && GameAccess.Bool(p,"is_in_vehicle") && vehicles.LocalOccupancy.Seat==SeatRole.Driver && network.Session.SessionId==token && network.World.Registry.SceneEpoch==oldEpoch+1,"native Client rebuilt confirmed driver seat in destination epoch");
                network.Disconnect("Controlled native reconnect.");
                yield return Await(()=>peer.Session.State==SessionState.Hosting,"managed Host clears departing Client seats");
                network.Join("127.0.0.1",peer.Port,"NativeClientReconnect");
                yield return Await(()=>network.GameplayReady && peer.GameplayReady,"native Client reconnects directly into stable hideout",TimeBaseline,120);
                p=locator.Find(); Check(p && p.GetComponent<CharacterController>().enabled && !GameAccess.Bool(p,"is_in_vehicle"),"hideout reconnect preserves original first person without stale seat");
                network.Disconnect("Controlled native fixture completed.");
                Trace("[TS2MP] NATIVE CONTINUITY COMPLETE checks="+checks+"; managed peer uses native fixture manifest; different profile/two-native-machine gate deferred");
            } finally { network.Disconnect("Controlled native fixture cleanup."); peer?.Dispose(); }
        }
        private IEnumerator NativeMissionPreparationFixture(byte vehicleVariant=0)
        {
            if(network.Session.State!=SessionState.Offline) throw new InvalidOperationException("Mission preparation requires offline backed-up gameplay.");
            NetworkManager peer=null; WorldTimeState canonical=default(WorldTimeState);
            IEnumerator Wait(Func<bool> ready,string phase) {
                double until=network.Now+120;
                while(!ready()) { peer.Poll(); if(canonical.Valid) peer.PublishTime(canonical); if(network.Now>until || network.Scenes.State==SceneTransitionState.Failed) throw new InvalidOperationException("Mission fixture preparation failed: "+phase+" "+network.LastError); yield return null; }
                Trace("[TS2MP] NATIVE MISSION PREPARATION PASS: "+phase);
            }
            try {
                peer=new NetworkManager(network.BuildFingerprintForDiagnostics,Trace);
                peer.World.AdapterFactory=type=>new FixtureRecord(type,new byte[0]); peer.World.PrepareBaseline=s=>MirrorFixture(peer.World.Registry,s.Entities); peer.HostSceneRequested+=s=>peer.MarkSceneLoaded(s.SceneKey);
                if(!network.Host(FixturePort(),"NativeClockSource")) throw new InvalidOperationException("Native clock source failed.");
                yield return null; yield return null; peer.Join("127.0.0.1",network.Port,"ManagedClockPeer");
                yield return Wait(()=>network.GameplayReady && peer.GameplayReady,"original native clock captured"); canonical=network.LastWorldTime;
                network.Disconnect("Mission preparation role switch."); peer.Dispose(); peer=null; yield return null;
                peer=new NetworkManager(network.BuildFingerprintForDiagnostics,Trace);
                peer.World.PrepareRegistry=()=> { network.World.PrepareRegistry?.Invoke(); MirrorFixture(peer.World.Registry,network.World.Registry.Capture()); };
                if(!peer.Host(FixturePort(),"ManagedMissionSource")) throw new InvalidOperationException("Managed mission source failed.");
                var target=new NetworkSceneDescriptor("MadisonSt",vehicleVariant,vehicleVariant==1); peer.SetLocalScene(true,target.SceneKey); peer.PublishStableScene(target); network.Join("127.0.0.1",peer.Port,"NativeMissionFixture");
                yield return Wait(()=>network.GameplayReady && peer.GameplayReady,"production automatic mission follow without preference map writes");
                network.Disconnect("Mission preparation complete.");
                Trace("[TS2MP] NATIVE MISSION PREPARATION COMPLETE; native MadisonSt ready for subsequent lifecycle fixture; managed source, not human gameplay");
            } finally {network.Disconnect("Mission preparation cleanup."); peer?.Dispose();}
        }
    }
}

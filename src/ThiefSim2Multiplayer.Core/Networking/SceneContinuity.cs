using System;
using System.IO;

namespace ThiefSim2Multiplayer.Core.Networking
{
    // Bounded semantic identity. MainScene owns managers/player; exactly one vanilla map is supported.
    public sealed class NetworkSceneDescriptor
    {
        private static readonly string[] maps = { "PlayerHideout", "PawnShop", "Auto Shop", "MadisonSt", "Hillville", "DrugFarm", "Warehouse", "BankScene", "Forestside Resort", "HauntedMansion", "Museum", "SwatRange", "Jail", "IntroHouse" };
        private static readonly int[] locations = { 0, 2, 3, 99, 97, 90, 91, 92, 94, 95, 96, 98, 93, -1 };
        public string Map { get; }
        public int Location { get; }
        public byte VehicleVariant {get;}
        public bool SecondVehicleAvailable {get;}
        // Confirmed actors only; slots are stable across native map instances. No Unity references.
        public byte FirstDriver {get;} public byte FirstPassenger {get;} public byte VanDriver {get;} public byte VanPassenger {get;}
        public bool HasTravelOccupants => FirstDriver != 0 || FirstPassenger != 0 || VanDriver != 0 || VanPassenger != 0;
        public string SceneKey => StringComparer.Ordinal.Compare("MainScene", Map) < 0 ? "MainScene|" + Map : Map + "|MainScene";
        public NetworkSceneDescriptor(string map, byte vehicleVariant=0, bool secondVehicleAvailable=false, byte firstDriver=0, byte firstPassenger=0, byte vanDriver=0, byte vanPassenger=0)
        {
            int i = Array.IndexOf(maps, map); byte[] actors = {firstDriver,firstPassenger,vanDriver,vanPassenger};
            if(i < 0 || vehicleVariant > 1 || !secondVehicleAvailable && vehicleVariant != 1 && (vanDriver != 0 || vanPassenger != 0)) throw new ArgumentException("Unsupported gameplay map/vehicle.");
            for(int a=0;a<actors.Length;a++) { if(actors[a]>2) throw new ArgumentException("Invalid travel actor."); for(int b=0;b<a;b++) if(actors[a]!=0 && actors[a]==actors[b]) throw new ArgumentException("Duplicate travel actor."); }
            Map=map; Location=locations[i]; VehicleVariant=vehicleVariant; SecondVehicleAvailable=secondVehicleAvailable;
            FirstDriver=firstDriver; FirstPassenger=firstPassenger; VanDriver=vanDriver; VanPassenger=vanPassenger;
        }
        public static NetworkSceneDescriptor FromLocation(int location)
        { int i = Array.IndexOf(locations, location); return i < 0 ? null : new NetworkSceneDescriptor(maps[i]); }
        public static bool TryFromKey(string key, out NetworkSceneDescriptor descriptor)
        { foreach (string map in maps) { var d = new NetworkSceneDescriptor(map); if (d.SceneKey == key) { descriptor = d; return true; } } descriptor = null; return false; }
    }
    public enum SceneTransitionState { WorldReady, HostTransitionRequested, TransitionAnnounced, LoadingScene, LocalSceneLoaded, RegistryRebuilding, WaitingForPeers, BaselineSynchronizing, Failed }
    public sealed class SceneContinuity
    {
        public ulong TransitionId { get; private set; }
        public uint Epoch { get; private set; }
        public NetworkSceneDescriptor Target { get; private set; }
        public SceneTransitionState State { get; private set; } = SceneTransitionState.WorldReady;
        public bool LocalLoaded { get; private set; }
        public bool PeerLoaded { get; private set; }
        public bool Active => Target != null;
        public bool BarrierOpen => !Active || (LocalLoaded && PeerLoaded && State != SceneTransitionState.Failed);
        public string Error { get; private set; } = "";
        public double TimeoutSeconds => State==SceneTransitionState.LoadingScene || State==SceneTransitionState.TransitionAnnounced ? 120 : State==SceneTransitionState.WaitingForPeers ? 60 : 45;
        public void Begin(ulong id, uint epoch, NetworkSceneDescriptor target)
        { if (id == 0 || epoch == 0 || target == null || (Active && (id <= TransitionId || epoch < Epoch))) throw new ArgumentException("Stale/invalid scene transition."); TransitionId = id; Epoch = epoch; Target = target; LocalLoaded = PeerLoaded = false; State = SceneTransitionState.TransitionAnnounced; Error = ""; }
        public void Loading() { if (Active && State != SceneTransitionState.Failed) State = SceneTransitionState.LoadingScene; }
        public bool LocalReady(string key)
        { if (!Active || key != Target.SceneKey || State == SceneTransitionState.Failed) return false; if(LocalLoaded) return true; LocalLoaded = true; State = PeerLoaded ? SceneTransitionState.BaselineSynchronizing : SceneTransitionState.WaitingForPeers; return true; }
        public bool PeerReady(ulong id, uint epoch, string key)
        { if (!Active || id != TransitionId || epoch != Epoch || key != Target.SceneKey || State == SceneTransitionState.Failed) return false; if(PeerLoaded) return true; PeerLoaded = true; if (LocalLoaded) State = SceneTransitionState.BaselineSynchronizing; return true; }
        public void Ready() { if (BarrierOpen && Active && State!=SceneTransitionState.WorldReady) { State = SceneTransitionState.WorldReady; if(Target.HasTravelOccupants) Target = new NetworkSceneDescriptor(Target.Map,Target.VehicleVariant,Target.SecondVehicleAvailable); } }
        public void Fail(string reason) { State = SceneTransitionState.Failed; LocalLoaded = PeerLoaded = false; Error = reason ?? "Transition failed"; }
        public void Clear() { TransitionId = 0; Epoch = 0; Target = null; LocalLoaded = PeerLoaded = false; State = SceneTransitionState.WorldReady; Error = ""; }
    }
    public sealed class SceneMessage
    { public byte Type; public ulong SessionId, TransitionId; public uint Epoch; public NetworkSceneDescriptor Target; }
    public static class SceneProtocol
    {
        public const byte Announce = 8, Loaded = 9, Failed = 10;
        public static byte[] Encode(SceneMessage m)
        {
            if (m == null || m.Type < Announce || m.Type > Failed || m.SessionId == 0 || m.TransitionId == 0 || m.Epoch == 0 || m.Target == null) throw new ArgumentException("Invalid scene envelope.");
            using (var s = new MemoryStream()) using (var w = new BinaryWriter(s)) { w.Write(m.Type); w.Write(m.SessionId); w.Write(m.TransitionId); w.Write(m.Epoch); w.Write(m.Target.VehicleVariant); w.Write(m.Target.SecondVehicleAvailable); w.Write((byte)m.Target.Map.Length); foreach (char c in m.Target.Map) w.Write((byte)c); w.Write(m.Target.FirstDriver); w.Write(m.Target.FirstPassenger); w.Write(m.Target.VanDriver); w.Write(m.Target.VanPassenger); return s.ToArray(); }
        }
        public static bool TryDecode(byte[] bytes, out SceneMessage message)
        {
            message = null; if (bytes == null || bytes.Length < 29 || bytes.Length > 64) return false;
            try { using (var s = new MemoryStream(bytes)) using (var r = new BinaryReader(s)) {
                var m = new SceneMessage { Type = r.ReadByte(), SessionId = r.ReadUInt64(), TransitionId = r.ReadUInt64(), Epoch = r.ReadUInt32() }; byte variant=r.ReadByte(), second=r.ReadByte(); int n = r.ReadByte();
                if (n != bytes.Length - 28 || variant>1 || second>1 || m.Type < Announce || m.Type > Failed || m.SessionId == 0 || m.TransitionId == 0 || m.Epoch == 0) return false;
                string map = System.Text.Encoding.ASCII.GetString(r.ReadBytes(n));
                m.Target = new NetworkSceneDescriptor(map,variant,second==1,r.ReadByte(),r.ReadByte(),r.ReadByte(),r.ReadByte()); message = m; return true;
            } } catch (ArgumentException) { return false; } catch (IOException) { return false; }
        }
    }
}

using System;
using System.IO;

namespace ThiefSim2Multiplayer.Core.Networking
{
    public struct WorldTimeState
    {
        public ulong SessionId, SleepId;
        public uint Epoch, Sequence;
        public float Hour;
        public int Year, Day, Weekday, Week, TotalHours, RentHours, DaysPlayed;
        public bool Valid => SessionId!=0 && Epoch!=0 && Sequence!=0 && Protocol.Finite(Hour) && Hour>=0 && Hour<24 &&
            Year>=0 && Year<=10000 && Day>=0 && Day<=366 && Weekday>=0 && Weekday<7 && Week>=0 && Week<=100000 && TotalHours>=0 && TotalHours<=10000000 && RentHours>=-10000000 && RentHours<=10000000 && DaysPlayed>=0 && DaysPlayed<=1000000;
    }
    public static class CooperativeTimeProtocol
    {
        public const byte Baseline=15, Begin=16, Wake=17, Ack=18, Resume=19;
        public static byte[] Encode(WorldTimeState s,byte type=Baseline)
        {
            if(!s.Valid || type<Baseline || type>Resume || type!=Baseline && s.SleepId==0) throw new ArgumentException("Invalid time envelope.");
            using(var m=new MemoryStream()) using(var w=new BinaryWriter(m)) { w.Write(type); w.Write(s.SessionId); w.Write(s.Epoch); w.Write(s.Sequence); w.Write(s.SleepId); w.Write(s.Hour); w.Write(s.Year); w.Write(s.Day); w.Write(s.Weekday); w.Write(s.Week); w.Write(s.TotalHours); w.Write(s.RentHours); w.Write(s.DaysPlayed); return m.ToArray(); }
        }
        public static bool Decode(byte[] b,out WorldTimeState s,out byte type)
        {
            s=default(WorldTimeState); type=0; if(b==null || b.Length!=57) return false;
            using(var r=new BinaryReader(new MemoryStream(b,false))) { type=r.ReadByte(); s=new WorldTimeState {SessionId=r.ReadUInt64(),Epoch=r.ReadUInt32(),Sequence=r.ReadUInt32(),SleepId=r.ReadUInt64(),Hour=r.ReadSingle(),Year=r.ReadInt32(),Day=r.ReadInt32(),Weekday=r.ReadInt32(),Week=r.ReadInt32(),TotalHours=r.ReadInt32(),RentHours=r.ReadInt32(),DaysPlayed=r.ReadInt32()}; }
            return type>=Baseline && type<=Resume && s.Valid && (type==Baseline || s.SleepId!=0);
        }
    }
    public enum CooperativeSleepPhase { Idle, Starting, Running, Waking, Failed }
    public sealed class CooperativeSleep
    {
        public CooperativeSleepPhase Phase {get;private set;}
        public ulong Id {get;private set;}
        public bool Frozen => Phase!=CooperativeSleepPhase.Idle;
        public void Begin(ulong id) { if(id==0 || id<=Id || Frozen) throw new ArgumentException("Invalid sleep start."); Id=id; Phase=CooperativeSleepPhase.Starting; }
        public bool Acknowledge(ulong id) { if(id!=Id) return false; if(Phase==CooperativeSleepPhase.Starting) Phase=CooperativeSleepPhase.Running; else if(Phase==CooperativeSleepPhase.Waking) Phase=CooperativeSleepPhase.Idle; else return false; return true; }
        public bool Wake(ulong id) { if(id!=Id || Phase!=CooperativeSleepPhase.Running) return false; Phase=CooperativeSleepPhase.Waking; return true; }
        public void Fail() { Phase=CooperativeSleepPhase.Failed; }
        public void Clear() { Phase=CooperativeSleepPhase.Idle; Id=0; }
    }
}

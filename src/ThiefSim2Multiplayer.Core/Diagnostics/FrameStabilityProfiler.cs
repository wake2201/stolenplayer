using System;
using System.Diagnostics;
using System.Text;

namespace ThiefSim2Multiplayer.Core.Diagnostics
{
    public enum RuntimePhase { JoinHandshake, WorldDescriptorResolution, NativeSceneInitialization, RegistryRebuild, BaselineCapture, BaselineSerialization, BaselineTransfer, BaselineApply, InventoryRebinding, NetworkAvatarConstruction, VehicleBinding, WorldReadyTransition, RuntimeNetworkProcessing }
    // Owner-thread fixed rings. Formatting/sorting happens only on explicit diagnostic export.
    public sealed class FrameStabilityProfiler
    {
        public readonly TimingWindow Frames=new TimingWindow();
        public readonly TimingWindow[] Phases=new TimingWindow[13];
        private readonly PhaseEvent[] timeline=new PhaseEvent[128];
        private readonly Spike[] spikes=new Spike[16];
        private readonly PhaseEvent[,] spikeTimeline=new PhaseEvent[16,16];
        private int nextPhase, nextSpike, frame, gc0, gc1, gc2;
        public bool Enabled;
        public double SpikeMilliseconds=100;
        public int SpikeCount {get;private set;}
        public int Gen0Collections {get;private set;}
        public int Gen1Collections {get;private set;}
        public int Gen2Collections {get;private set;}
        public FrameStabilityProfiler() { for(int i=0;i<Phases.Length;i++) Phases[i]=new TimingWindow(); Reset(); }
        private struct PhaseEvent { public RuntimePhase Phase; public double Milliseconds; public int Frame; }
        private struct Spike { public double Milliseconds; public int Frame, State, Stage, Packets, Queue, Gen0, Gen1, Gen2; public uint Epoch; public long ManagedBytes; }
        public void Reset() { Frames.Clear(); foreach(var p in Phases) p.Clear(); Array.Clear(timeline,0,timeline.Length); Array.Clear(spikes,0,spikes.Length); Array.Clear(spikeTimeline,0,spikeTimeline.Length); frame=nextPhase=nextSpike=SpikeCount=Gen0Collections=Gen1Collections=Gen2Collections=0; gc0=GC.CollectionCount(0); gc1=GC.CollectionCount(1); gc2=GC.CollectionCount(2); }
        public Scope Measure(RuntimePhase phase) => new Scope(this,phase);
        public readonly struct Scope : IDisposable
        {
            private readonly FrameStabilityProfiler owner; private readonly RuntimePhase phase; private readonly long start;
            internal Scope(FrameStabilityProfiler p,RuntimePhase value) { owner=p; phase=value; start=p!=null && p.Enabled ? Stopwatch.GetTimestamp() : 0; }
            public void Dispose() { if(start!=0) owner.Record(phase,(Stopwatch.GetTimestamp()-start)*1000.0/Stopwatch.Frequency); }
        }
        public void Record(RuntimePhase phase,double milliseconds)
        { if(!Enabled || (uint)phase>=Phases.Length || milliseconds<0 || double.IsNaN(milliseconds) || double.IsInfinity(milliseconds)) return; Phases[(int)phase].Add(milliseconds); timeline[nextPhase]=new PhaseEvent {Phase=phase,Milliseconds=milliseconds,Frame=frame}; nextPhase=(nextPhase+1)%timeline.Length; }
        public void Frame(double milliseconds,int state,uint epoch,int stage,int packets,int queue)
        {
            if(!Enabled) return; frame++; Frames.Add(milliseconds);
            int c0=GC.CollectionCount(0),c1=GC.CollectionCount(1),c2=GC.CollectionCount(2);
            int d0=c0-gc0,d1=c1-gc1,d2=c2-gc2; Gen0Collections+=d0; Gen1Collections+=d1; Gen2Collections+=d2; gc0=c0; gc1=c1; gc2=c2;
            if(milliseconds<SpikeMilliseconds) return;
            int slot=nextSpike++%spikes.Length; SpikeCount++;
            spikes[slot]=new Spike {Milliseconds=milliseconds,Frame=frame,State=state,Epoch=epoch,Stage=stage,Packets=packets,Queue=queue,Gen0=d0,Gen1=d1,Gen2=d2,ManagedBytes=GC.GetTotalMemory(false)};
            for(int i=0;i<16;i++) spikeTimeline[slot,i]=timeline[(nextPhase-16+i+timeline.Length*2)%timeline.Length];
        }
        public string Summary()
        {
            var b=new StringBuilder("FRAME_STABILITY ").Append(Frames.Summary()).Append("; spikes=").Append(SpikeCount).Append("; gc=").Append(Gen0Collections).Append('/').Append(Gen1Collections).Append('/').Append(Gen2Collections).Append("; allocationsPerFrame=UNAVAILABLE_MONO; managedBytes=live-heap-estimate");
            for(int i=0;i<Phases.Length;i++) b.AppendLine().Append((RuntimePhase)i).Append(' ').Append(Phases[i].Summary());
            for(int i=Math.Max(0,nextSpike-spikes.Length);i<nextSpike;i++) {
                int slot=i%spikes.Length; var s=spikes[slot];
                b.AppendLine().Append("SPIKE frame=").Append(s.Frame).Append(" ms=").Append(s.Milliseconds.ToString("F3",System.Globalization.CultureInfo.InvariantCulture)).Append(" state=").Append(s.State).Append(" epoch=").Append(s.Epoch).Append(" baselineStage=").Append(s.Stage).Append(" packets=").Append(s.Packets).Append(" queue=").Append(s.Queue).Append(" gc=").Append(s.Gen0).Append('/').Append(s.Gen1).Append('/').Append(s.Gen2).Append(" managedBytes=").Append(s.ManagedBytes);
                for(int k=0;k<16;k++) { var e=spikeTimeline[slot,k]; if(e.Milliseconds>0) b.Append(" | ").Append(e.Frame).Append(':').Append(e.Phase).Append('=').Append(e.Milliseconds.ToString("F3",System.Globalization.CultureInfo.InvariantCulture)); }
            }
            return b.ToString();
        }
    }
}

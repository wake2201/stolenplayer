using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace ThiefSim2Multiplayer.TransportHarness
{
    // Desktop test fixture only. A loopback link impairs real datagrams in BOTH directions,
    // including reliable control traffic. It is never packaged into the game plugin.
    public sealed class UdpConditionLink : IDisposable
    {
        private struct Pending { public byte[] Bytes; public IPEndPoint Destination; }
        private readonly UdpClient socket;
        private readonly IPEndPoint server;
        private readonly Thread worker;
        private readonly int latency, jitter, loss, duplication;
        private int stopping;
        private long received, forwarded, dropped, overflow;
        private string error = "";
        public int Port { get; }
        public long Received => Interlocked.Read(ref received);
        public long Forwarded => Interlocked.Read(ref forwarded);
        public long Dropped => Interlocked.Read(ref dropped);
        public long QueueOverflow => Interlocked.Read(ref overflow);
        private long duplicated;
        public long Duplicated => Interlocked.Read(ref duplicated);
        public string Error => Volatile.Read(ref error);
        public UdpConditionLink(int listenPort, int targetPort, int latencyMilliseconds, int jitterSpanMilliseconds, int lossPercent, int duplicationPercent = 0)
        {
            if (latencyMilliseconds < 0 || latencyMilliseconds > 1000 || jitterSpanMilliseconds < 0 || jitterSpanMilliseconds > 1000 || lossPercent < 0 || lossPercent > 50) throw new ArgumentOutOfRangeException();
            latency = latencyMilliseconds; jitter = jitterSpanMilliseconds; loss = lossPercent;
            if (duplicationPercent < 0 || duplicationPercent > 50) throw new ArgumentOutOfRangeException(); duplication = duplicationPercent;
            server = new IPEndPoint(IPAddress.Loopback,targetPort);
            socket = new UdpClient(new IPEndPoint(IPAddress.Loopback,listenPort)); Port = ((IPEndPoint)socket.Client.LocalEndPoint).Port;
            worker = new Thread(Run) { IsBackground = true, Name = "TS2MP test UDP link" }; worker.Start();
        }
        private void Run()
        {
            var random = new Random(1337); var clock = Stopwatch.StartNew();
            var pending = new PriorityQueue<Pending,double>(); IPEndPoint client = null;
            try
            {
                while (Volatile.Read(ref stopping) == 0)
                {
                    for (int i = 0; i < 128 && socket.Available > 0; i++)
                    {
                        IPEndPoint source = new IPEndPoint(IPAddress.Any,0); byte[] bytes;
                        // Windows reports ICMP port-unreachable as ConnectionReset after
                        // a harness endpoint closes. That is normal teardown, not a broken link.
                        try { bytes = socket.Receive(ref source); }
                        catch (SocketException closed) when (closed.SocketErrorCode == SocketError.ConnectionReset) { continue; }
                        Interlocked.Increment(ref received);
                        bool fromServer = source.Equals(server);
                        if (!fromServer && client == null) client = source;
                        if (bytes.Length > 4096 || (!fromServer && !source.Equals(client)) || (fromServer && client == null)) { Interlocked.Increment(ref dropped); continue; }
                        if (random.NextDouble()*100 < loss) { Interlocked.Increment(ref dropped); continue; }
                        if (pending.Count >= 512) { Interlocked.Increment(ref overflow); continue; }
                        double delay = Math.Max(0,latency+(random.NextDouble()-0.5)*jitter);
                        pending.Enqueue(new Pending { Bytes = bytes, Destination = fromServer ? client : server },clock.Elapsed.TotalMilliseconds+delay);
                        if (duplication > 0 && random.Next(100) < duplication && pending.Count < 512)
                        {
                            pending.Enqueue(new Pending { Bytes = bytes, Destination = fromServer ? client : server },clock.Elapsed.TotalMilliseconds+Math.Max(0,latency+(random.NextDouble()-0.5)*jitter));
                            Interlocked.Increment(ref duplicated);
                        }
                    }
                    for (int i = 0; i < 128 && pending.TryPeek(out var packet,out double due) && due <= clock.Elapsed.TotalMilliseconds; i++)
                    {
                        pending.Dequeue();
                        try { socket.Send(packet.Bytes,packet.Bytes.Length,packet.Destination); Interlocked.Increment(ref forwarded); }
                        catch (SocketException closed) when (closed.SocketErrorCode == SocketError.ConnectionReset) { Interlocked.Increment(ref dropped); }
                    }
                    Thread.Sleep(1);
                }
            }
            catch (Exception failure) { if (Volatile.Read(ref stopping) == 0) Volatile.Write(ref error,failure.ToString()); }
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref stopping,1) != 0) return;
            if (!worker.Join(2000)) throw new TimeoutException("Test UDP link worker did not stop.");
            socket.Dispose();
        }
    }
}

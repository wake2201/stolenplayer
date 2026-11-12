// N1 two-process smoke test: one process hosts, a second process joins, both with the join code from the process
// environment (TS2MP_JOIN_CODE, as the launcher passes it to the game). Used by tests/n1-smoke.ps1.
//   TS2MP.NetSmoke.exe host   <bind> <port> <logdir>
//   TS2MP.NetSmoke.exe client <host> <port> <logdir>
//   TS2MP.NetSmoke.exe newcode                      (prints a fresh join code for the test script)
// Exit 0 = handshake + one message each way + clean disconnect within 20 s.
using System;
using System.IO;
using System.Threading;
using LiteNetLib;
using LiteNetLib.Utils;
using TS2MP.Shared;

static class NetSmoke
{
    static int Main(string[] a)
    {
        if (a.Length == 1 && a[0] == "newcode") { Console.WriteLine(JoinAuth.Format(JoinAuth.NewCode())); return 0; }
        if (a.Length != 4) { Console.WriteLine("usage: host|client <address> <port> <logdir>"); return 2; }
        bool host = a[0] == "host";
        int port = int.Parse(a[2]);
        string raw = Environment.GetEnvironmentVariable(JoinAuth.EnvVar);
        Environment.SetEnvironmentVariable(JoinAuth.EnvVar, null);
        string code = JoinAuth.Normalize(raw);
        if (code == null) { Console.WriteLine("SMOKE_FAIL no valid join code in the environment"); return 3; }
        string inst = host ? "smokeh" : "smokec";
        var log = new InstanceLog(Path.Combine(a[3], (host ? "host" : "client") + ".log"), host ? "HOST" : "CLIENT", inst, host ? "smoke" : "join");
        using (var n = new SessionNode(host ? Role.Host : Role.Client, host ? "smoke" + Environment.TickCount : "join", inst, "1.27c", port, log))
        {
            n.JoinKey = JoinAuth.DeriveKey(code);
            if (host) n.BindAddress = a[1]; else { n.ConnectAddress = a[1]; n.ReconnectDelayMs = 300; }
            bool got = false, sent = false, sawConnect = false;
            n.MessageReceived += (p, t, r) => { if (t == (byte)MsgType.SceneInfo) got = true; };
            if (!n.Start()) { Console.WriteLine("SMOKE_FAIL start: " + n.State); return 4; }
            var end = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < end)
            {
                n.Tick();
                if (n.IsConnected)
                {
                    sawConnect = true;
                    if (!sent) { var w = new NetDataWriter(); w.Put((byte)MsgType.SceneInfo); w.Put(inst); n.Send(w, DeliveryMethod.ReliableOrdered, SessionNode.ChannelReliable); sent = true; }
                    if (got && !host) { Thread.Sleep(300); n.Tick(); Console.WriteLine("SMOKE_OK client connected to " + n.RemoteInstanceId + ", message exchanged"); return 0; }
                }
                else if (host && sawConnect && got) { Console.WriteLine("SMOKE_OK host authenticated " + n.AuthSuccesses + " client(s), message exchanged, client left"); return 0; }
                if (n.State == ConnState.Failed) { Console.WriteLine("SMOKE_FAIL " + n.FatalReason + " reject=" + n.LastReject); return 5; }
                Thread.Sleep(5);
            }
            Console.WriteLine("SMOKE_FAIL timeout state=" + n.State);
            return 6;
        }
    }
}

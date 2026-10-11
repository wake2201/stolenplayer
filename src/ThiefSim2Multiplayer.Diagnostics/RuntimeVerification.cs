using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;

namespace ThiefSim2Multiplayer.Diagnostics
{
    public sealed partial class Plugin
    {
        private static int prefixCalls, postfixCalls;
        private static void VerificationPrefix() { prefixCalls++; }
        private static void VerificationPostfix() { postfixCalls++; }

        private IEnumerator VerifyRuntime()
        {
            yield return null;
            Harmony verifier = new Harmony(Id + ".verification");
            GameObject temporary = null;
            try
            {
                Type type = Type.GetType("LootBox, Assembly-CSharp", true);
                MethodInfo method = type.GetMethod("Start", BindingFlags.NonPublic | BindingFlags.Instance);
                byte[] il = method.GetMethodBody().GetILAsByteArray();
                if (il.Length != 1 || il[0] != 0x2a) throw new InvalidOperationException("Empty verification method fingerprint changed.");
                prefixCalls = postfixCalls = 0;
                verifier.Patch(method,
                    new HarmonyMethod(typeof(Plugin).GetMethod("VerificationPrefix", BindingFlags.Static | BindingFlags.NonPublic)),
                    new HarmonyMethod(typeof(Plugin).GetMethod("VerificationPostfix", BindingFlags.Static | BindingFlags.NonPublic)));
                temporary = new GameObject("TS2MP_TemporaryDetourVerification");
                temporary.SetActive(false);
                Component instance = temporary.AddComponent(type);
                method.Invoke(instance, null);
                if (prefixCalls != 1 || postfixCalls != 1) throw new InvalidOperationException("Detour callbacks did not execute exactly once.");
                Emit("harmony_detour_verified", "target", "LootBox.Start", "prefix", "1", "postfix", "1");
                Logger.LogInfo("Harmless Harmony Prefix/Postfix detour VERIFIED.");
            }
            catch (Exception error) { Emit("harmony_detour_failed", "error", error.ToString()); Logger.LogWarning(error); }
            finally
            {
                verifier.UnpatchSelf();
                if (temporary) Destroy(temporary);
                Emit("harmony_verification_removed");
            }
            NetManager host = null, client = null;
            bool received = false;
            try
            {
                var hostListener = new EventBasedNetListener();
                var clientListener = new EventBasedNetListener();
                host = new NetManager(hostListener) { IPv6Enabled = false };
                client = new NetManager(clientListener) { IPv6Enabled = false };
                hostListener.ConnectionRequestEvent += request => request.AcceptIfKey("TS2MP-runtime-verification");
                clientListener.PeerConnectedEvent += peer => peer.Send(new byte[] { 1, 3, 5 }, DeliveryMethod.ReliableOrdered);
                hostListener.NetworkReceiveEvent += (peer, reader, channel, delivery) => {
                    try { received = reader.AvailableBytes == 3 && reader.GetByte() == 1 && reader.GetByte() == 3 && reader.GetByte() == 5; }
                    finally { reader.Recycle(); }
                };
                if (!host.Start(System.Net.IPAddress.Loopback, System.Net.IPAddress.IPv6None, 0) || !client.Start())
                    throw new InvalidOperationException("Loopback socket start failed.");
                client.Connect("127.0.0.1", host.LocalPort, "TS2MP-runtime-verification");
                Emit("transport_verification_started", "assembly", typeof(NetManager).Assembly.FullName);
            }
            catch (Exception error) { Emit("transport_verification_failed", "error", error.ToString()); }
            float deadline = Time.realtimeSinceStartup + 5;
            while (host != null && client != null && !received && Time.realtimeSinceStartup < deadline)
            {
                host.PollEvents(); client.PollEvents();
                yield return null;
            }
            if (host != null) host.Stop();
            if (client != null) client.Stop();
            Emit(received ? "transport_runtime_verified" : "transport_verification_failed", "result", received ? "reliable loopback packet received" : "timeout/start failure");
            Logger.LogInfo("LiteNetLib 1.3.5 Unity/net46 runtime loopback: " + received);
        }
    }
}

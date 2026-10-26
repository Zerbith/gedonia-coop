using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace GedoniaCoop
{
    // Moves opaque packets between this game and its peers. Endpoints are
    // whatever the transport uses to address a peer (IPEndPoint, SteamID as
    // ulong); Net only compares them with Equals.
    public interface ITransport : IDisposable
    {
        bool IsHost { get; }
        // Client only: where the host is, or null while still connecting.
        object HostEndpoint { get; }
        // One line for the log and the on-screen status.
        string Status { get; }
        void Tick();
        bool Send(object endpoint, byte[] data, bool reliable);
        void Receive(List<KeyValuePair<object, byte[]>> into);
    }

    // Plain UDP: needs an open port on the host (LAN, VPN or port forwarding).
    // "Reliable" sends are numbered, acknowledged and resent every 200 ms (up
    // to 10 s); duplicates are dropped. Delivery is not ordered - every
    // feature that cares about order numbers its own messages.
    // Raw packet: [0][payload] unreliable, [1][seq u32][payload] reliable, [2][seq u32] ack.
    public class UdpTransport : ITransport
    {
        class Outgoing { public byte[] Packet; public IPEndPoint To; public float NextSend, GiveUp; }
        class PeerIn { public readonly HashSet<uint> Seen = new HashSet<uint>(); public readonly Queue<uint> Order = new Queue<uint>(); }

        readonly UdpClient udp;
        readonly bool isHost;
        readonly IPEndPoint host;
        readonly Queue<KeyValuePair<object, byte[]>> inbox = new Queue<KeyValuePair<object, byte[]>>();
        readonly Thread receiver;
        volatile bool running = true;
        int overflow;

        uint nextSeq;
        readonly Dictionary<uint, Outgoing> pending = new Dictionary<uint, Outgoing>();
        readonly Dictionary<string, PeerIn> peersIn = new Dictionary<string, PeerIn>();
        public static float SimulatedLoss; // test only: share of raw packets dropped on send
        readonly System.Random rng = new System.Random();
        public int Resent, GaveUp, Duplicates, LostOnPurpose;

        UdpTransport(UdpClient udp, bool isHost, IPEndPoint host)
        {
            this.udp = udp;
            this.isHost = isHost;
            this.host = host;
            // Windows reports ICMP "port unreachable" as a reset on the next
            // Receive; turn that off so a client started before the host survives.
            try { udp.Client.IOControl(-1744830452, new byte[] { 0 }, null); } catch { }
            receiver = new Thread(ReceiveLoop);
            receiver.IsBackground = true;
            receiver.Name = "GedoniaCoop.Receive";
            receiver.Start();
        }

        public static UdpTransport Host(int port, IPAddress bind)
        {
            return new UdpTransport(new UdpClient(new IPEndPoint(bind, port)), true, null);
        }

        public static UdpTransport Connect(string hostName, int port)
        {
            IPAddress addr;
            if (!IPAddress.TryParse(hostName, out addr))
                addr = Dns.GetHostAddresses(hostName)[0];
            return new UdpTransport(new UdpClient(new IPEndPoint(IPAddress.Any, 0)), false, new IPEndPoint(addr, port));
        }

        public bool IsHost { get { return isHost; } }
        public object HostEndpoint { get { return host; } }
        public string Status
        {
            get { return isHost ? Lang.T("UDP host on port ", "UDP-Host auf Port ") + ((IPEndPoint)udp.Client.LocalEndPoint).Port : Lang.T("UDP client of ", "UDP-Verbindung zu ") + host; }
        }

        public string Stats
        {
            get { return "udp unacked=" + pending.Count + " resent=" + Resent + " gaveUp=" + GaveUp + " dups=" + Duplicates + (SimulatedLoss > 0f ? " lostOnPurpose=" + LostOnPurpose : ""); }
        }

        public void Tick()
        {
            float now = UnityEngine.Time.realtimeSinceStartup;
            List<uint> drop = null;
            foreach (KeyValuePair<uint, Outgoing> p in pending)
            {
                if (now >= p.Value.GiveUp) { if (drop == null) drop = new List<uint>(); drop.Add(p.Key); continue; }
                if (now < p.Value.NextSend) continue;
                p.Value.NextSend = now + 0.2f;
                Resent++;
                Raw(p.Value.Packet, p.Value.To);
            }
            if (drop != null) foreach (uint s in drop) { pending.Remove(s); GaveUp++; }
        }

        bool Raw(byte[] packet, IPEndPoint to)
        {
            if (SimulatedLoss > 0f && rng.NextDouble() < SimulatedLoss) { LostOnPurpose++; return true; }
            try { udp.Send(packet, packet.Length, to); return true; }
            catch (SocketException) { return false; }
        }

        void ReceiveLoop()
        {
            while (running)
            {
                try
                {
                    IPEndPoint from = new IPEndPoint(IPAddress.Any, 0);
                    byte[] data = udp.Receive(ref from);
                    lock (inbox)
                    {
                        if (inbox.Count < 1024) inbox.Enqueue(new KeyValuePair<object, byte[]>(from, data));
                        else overflow++;
                    }
                }
                catch (ObjectDisposedException) { return; }
                catch (SocketException) { if (!running) return; }
            }
        }

        public bool Send(object endpoint, byte[] data, bool reliable)
        {
            IPEndPoint to = (IPEndPoint)endpoint;
            if (!reliable)
            {
                byte[] u = new byte[data.Length + 1];
                Buffer.BlockCopy(data, 0, u, 1, data.Length);
                return Raw(u, to);
            }
            uint seq = ++nextSeq;
            byte[] r = new byte[data.Length + 5];
            r[0] = 1;
            BitConverter.GetBytes(seq).CopyTo(r, 1);
            Buffer.BlockCopy(data, 0, r, 5, data.Length);
            float now = UnityEngine.Time.realtimeSinceStartup;
            pending[seq] = new Outgoing { Packet = r, To = to, NextSend = now + 0.2f, GiveUp = now + 10f };
            return Raw(r, to);
        }

        public void Receive(List<KeyValuePair<object, byte[]>> into)
        {
            List<KeyValuePair<object, byte[]>> raw = new List<KeyValuePair<object, byte[]>>();
            lock (inbox)
            {
                while (inbox.Count > 0) raw.Add(inbox.Dequeue());
            }
            foreach (KeyValuePair<object, byte[]> pkt in raw)
            {
                byte[] d = pkt.Value;
                IPEndPoint from = (IPEndPoint)pkt.Key;
                if (d.Length < 1) continue;
                if (d[0] == 0)
                {
                    byte[] payload = new byte[d.Length - 1];
                    Buffer.BlockCopy(d, 1, payload, 0, payload.Length);
                    into.Add(new KeyValuePair<object, byte[]>(from, payload));
                }
                else if (d[0] == 1 && d.Length >= 5)
                {
                    uint seq = BitConverter.ToUInt32(d, 1);
                    byte[] ack = new byte[5];
                    ack[0] = 2;
                    BitConverter.GetBytes(seq).CopyTo(ack, 1);
                    Raw(ack, from); // ack every copy: the first ack may have been lost
                    string key = from.ToString();
                    PeerIn p;
                    if (!peersIn.TryGetValue(key, out p)) peersIn[key] = p = new PeerIn();
                    if (!p.Seen.Add(seq)) { Duplicates++; continue; }
                    p.Order.Enqueue(seq);
                    if (p.Order.Count > 8192) p.Seen.Remove(p.Order.Dequeue());
                    byte[] payload = new byte[d.Length - 5];
                    Buffer.BlockCopy(d, 5, payload, 0, payload.Length);
                    into.Add(new KeyValuePair<object, byte[]>(from, payload));
                }
                else if (d[0] == 2 && d.Length >= 5)
                {
                    pending.Remove(BitConverter.ToUInt32(d, 1));
                }
            }
        }

        public void Dispose()
        {
            running = false;
            udp.Close();
        }
    }
}

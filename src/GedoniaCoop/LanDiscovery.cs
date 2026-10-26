using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace GedoniaCoop
{
    // Finding co-op games in the home network without Steam: a LAN host
    // answers "GCOOP?" broadcasts on DiscoveryPort with "GCOOP!<protocol>|<name>";
    // the join list broadcasts every two seconds and lists who answered.
    public class LanDiscovery : IDisposable
    {
        public const int GamePort = 7790, DiscoveryPort = 7791;
        const string Ask = "GCOOP?", Answer = "GCOOP!";

        public class Found { public string Address, Name; public int Protocol; public DateTime Seen; }

        readonly UdpClient udp;
        readonly Thread thread;
        volatile bool running = true;
        readonly bool responder;
        readonly string name;
        readonly Dictionary<string, Found> found = new Dictionary<string, Found>();

        LanDiscovery(UdpClient udp, bool responder, string name)
        {
            this.udp = udp;
            this.responder = responder;
            this.name = name;
            udp.EnableBroadcast = true;
            try { udp.Client.IOControl(-1744830452, new byte[] { 0 }, null); } catch { } // ignore ICMP resets
            thread = new Thread(Loop);
            thread.IsBackground = true;
            thread.Name = "GedoniaCoop.LanDiscovery";
            thread.Start();
        }

        // Host side: answer searches.
        public static LanDiscovery Respond(string playerName)
        {
            return new LanDiscovery(new UdpClient(new IPEndPoint(IPAddress.Any, DiscoveryPort)), true, playerName);
        }

        // Join side: search.
        public static LanDiscovery Search()
        {
            return new LanDiscovery(new UdpClient(new IPEndPoint(IPAddress.Any, 0)), false, "");
        }

        public void Probe()
        {
            byte[] ask = Encoding.UTF8.GetBytes(Ask);
            foreach (IPAddress b in Broadcasts())
                try { udp.Send(ask, ask.Length, new IPEndPoint(b, DiscoveryPort)); } catch { }
        }

        public List<Found> Games()
        {
            List<Found> list = new List<Found>();
            lock (found)
                foreach (Found f in found.Values)
                    if ((DateTime.UtcNow - f.Seen).TotalSeconds < 6) list.Add(f);
            return list;
        }

        void Loop()
        {
            while (running)
            {
                try
                {
                    IPEndPoint from = new IPEndPoint(IPAddress.Any, 0);
                    string text = Encoding.UTF8.GetString(udp.Receive(ref from));
                    if (responder && text == Ask)
                    {
                        byte[] reply = Encoding.UTF8.GetBytes(Answer + Net.ProtocolVersion + "|" + name);
                        udp.Send(reply, reply.Length, from);
                    }
                    else if (!responder && text.StartsWith(Answer))
                    {
                        string[] parts = text.Substring(Answer.Length).Split(new char[] { '|' }, 2);
                        int protocol;
                        int.TryParse(parts[0], out protocol);
                        lock (found)
                            found[from.Address.ToString()] = new Found { Address = from.Address.ToString(), Name = parts.Length > 1 ? parts[1] : "?", Protocol = protocol, Seen = DateTime.UtcNow };
                    }
                }
                catch (ObjectDisposedException) { return; }
                catch (SocketException) { if (!running) return; }
                catch (Exception) { }
            }
        }

        // 255.255.255.255 plus each network's own broadcast address (some
        // routers drop the global one).
        static List<IPAddress> Broadcasts()
        {
            List<IPAddress> list = new List<IPAddress> { IPAddress.Broadcast };
            foreach (UnicastIPAddressInformation u in LocalAddresses())
            {
                if (u.IPv4Mask == null) continue;
                byte[] a = u.Address.GetAddressBytes(), m = u.IPv4Mask.GetAddressBytes();
                for (int i = 0; i < 4; i++) a[i] = (byte)(a[i] | ~m[i]);
                list.Add(new IPAddress(a));
            }
            return list;
        }

        static IEnumerable<UnicastIPAddressInformation> LocalAddresses()
        {
            NetworkInterface[] nics;
            try { nics = NetworkInterface.GetAllNetworkInterfaces(); } catch { yield break; }
            foreach (NetworkInterface n in nics)
            {
                if (n.OperationalStatus != OperationalStatus.Up || n.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (UnicastIPAddressInformation u in n.GetIPProperties().UnicastAddresses)
                    if (u.Address.AddressFamily == AddressFamily.InterNetwork) yield return u;
            }
        }

        // For the status line: the host's addresses a friend could type in.
        public static string OwnAddresses()
        {
            List<string> list = new List<string>();
            foreach (UnicastIPAddressInformation u in LocalAddresses()) list.Add(u.Address.ToString());
            return list.Count > 0 ? string.Join(", ", list.ToArray()) : "?";
        }

        public void Dispose()
        {
            running = false;
            try { udp.Close(); } catch { }
        }
    }
}

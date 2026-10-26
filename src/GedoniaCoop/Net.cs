using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace GedoniaCoop
{
    // One player's snapshot, sent unreliably ~20 times a second.
    public class PlayerState
    {
        public int Id;
        public uint Seq;
        public string Name = "";
        public string Scene = "";
        public string World = "";
        public float X, Y, Z, Yaw;
        // Animator: current (or destination) state per layer, and every
        // non-trigger parameter in controller order. Triggers can't be read
        // back, so state hashes carry one-shot actions like attacks and rolls.
        public int[] LayerStates = new int[0];
        public float[] LayerTimes = new float[0];
        public float[] Params = new float[0];
        // Test checks: the sender's own "Speed" value and the clip with the
        // highest weight on layer 0 (shows which way a blend tree went).
        public float Speed;
        public string Clip = "";
        // Which weapon holders are shown (PlayerScript.ActivateWeaponModels):
        // bit 0 right hand, 1 left hand, 2 back, 3 bow arrow.
        public byte WeaponFlags;
        // Sender's horizontal travel speed, measured from its own position the
        // same way the puppet measures itself.
        public float TravelSpeed;
        public bool Dead;
        // Sneaking (PlayerScript.inStealth > 0): its torch gives no light then.
        public bool Stealth;
        // Inside a dungeon (its rooms are far away from the map, so the map
        // shows the last place outside instead).
        public bool InDungeon;
    }

    // Everything PlayerModelCustomization.RefreshPlayerModel needs. Sent about
    // once a second; Version bumps when the player changes gear or looks.
    public class Appearance
    {
        public int Id;
        public int Version;
        public int[] Models = new int[8]; // helmet, chest, pants, boots, gloves, cloak, weapon main, weapon secondary
        public int Hair, Beard, HairColor, Head, BodyType, SkinColor, FaceDetail, EyeColor;
        public bool IsMale;
        public List<string> AdditionalData = new List<string>();
        public string WeaponName = "", WeaponNameSecondary = "";
        // LocalPlayer.LookSignature of the sender's real model, refreshed on every send.
        public int Signature;
        // The sender's "show helmet" / "show cloak" options (PlayerPrefs on its PC).
        public bool ShowHelmet = true, ShowCloak = true;
    }

    // Co-op protocol on top of any ITransport. The host remembers every peer
    // that talks to it; a client only talks to the host. The host relays every
    // packet to the other clients, so this works for more than two players.
    public class Net : IDisposable
    {
        const ushort Magic = 0x4743; // "GC"
        const byte Version = 13;
        public const byte ProtocolVersion = Version;
        const byte MsgState = 1;
        const byte MsgAppearance = 2;

        // Generic messages (type >= 10): envelope carries reliability, sender
        // and target player id (0 = everyone); the payload is the feature's own.
        public const byte MsgPresence = 10, MsgOwn = 11, MsgEnemyStates = 12, MsgDamageRequest = 13,
            MsgPlayerHit = 14, MsgEnemyDeath = 15,
            MsgStoryIntent = 16, MsgStoryEvent = 17, MsgStoryAck = 18, MsgIntentAck = 19, MsgQuestState = 20,
            MsgLootLock = 21, MsgLootGrant = 22, MsgLootRelease = 23, MsgLootTake = 24,
            MsgWorldClock = 25, MsgTimeSkip = 26, MsgActorRoster = 27,
            MsgEventState = 28, MsgEventSpawn = 29, MsgEventObjective = 30, MsgEventComplete = 31, MsgEventUse = 32,
            MsgWorldState = 33, MsgProjectile = 34, MsgExpeditions = 35, MsgExpeditionStart = 36, MsgEventObject = 37, MsgEventObjSpawn = 38, MsgWorldStateRequest = 39, MsgTravellers = 40, MsgSounds = 41, MsgDialogue = 42;

        public struct Message
        {
            public byte Type;
            public int Sender, Target;
            public byte[] Payload;
        }

        readonly ITransport transport;
        readonly Dictionary<int, object> peers = new Dictionary<int, object>();
        readonly List<KeyValuePair<object, byte[]>> batch = new List<KeyValuePair<object, byte[]>>();

        public int Sent, Received, Dropped;
        public int LocalId;
        public byte OtherVersion; // protocol version of a peer whose packets we can't read (0 = none seen)

        public List<PlayerState> States = new List<PlayerState>();
        public List<Appearance> Appearances = new List<Appearance>();
        public List<Message> Messages = new List<Message>();

        public Net(ITransport transport)
        {
            this.transport = transport;
        }

        public bool IsHost { get { return transport.IsHost; } }
        public ITransport Transport { get { return transport; } }

        // Position is replaced 20 times a second, so it goes unreliable; the
        // appearance changes rarely and must arrive.
        public void SendState(PlayerState s) { Broadcast(EncodeState(s), false); }
        public void SendAppearance(Appearance a) { Broadcast(EncodeAppearance(a), true); }

        public void SendMessage(byte type, int target, byte[] payload, bool reliable)
        {
            byte[] data = new byte[14 + payload.Length];
            BitConverter.GetBytes(Magic).CopyTo(data, 0);
            data[2] = Version;
            data[3] = type;
            data[4] = (byte)(reliable ? 1 : 0);
            BitConverter.GetBytes(LocalId).CopyTo(data, 5);
            BitConverter.GetBytes(target).CopyTo(data, 9);
            data[13] = 0;
            payload.CopyTo(data, 14);
            // A host's message for one client could go to that client only, but
            // with 2-4 players broadcasting keeps the relay simple.
            Broadcast(data, reliable);
        }

        void Broadcast(byte[] data, bool reliable)
        {
            if (transport.IsHost)
            {
                foreach (object ep in peers.Values) SendRaw(data, ep, reliable);
            }
            else if (transport.HostEndpoint != null)
            {
                SendRaw(data, transport.HostEndpoint, reliable);
            }
        }

        // Drains received packets on the main thread into States/Appearances.
        public void Poll()
        {
            States.Clear();
            Appearances.Clear();
            Messages.Clear();
            transport.Tick();
            batch.Clear();
            transport.Receive(batch);
            foreach (KeyValuePair<object, byte[]> pkt in batch)
            {
                int sender;
                bool reliable;
                if (!Decode(pkt.Value, out sender, out reliable))
                {
                    Dropped++;
                    // Our magic but another protocol version: a different mod build.
                    byte[] d = pkt.Value;
                    if (d.Length >= 3 && BitConverter.ToUInt16(d, 0) == Magic && d[2] != Version && OtherVersion != d[2])
                    {
                        OtherVersion = d[2];
                        Plugin.Log.LogWarning("Packets from a different mod version (protocol " + d[2] + ", ours " + Version + ") are ignored");
                    }
                    continue;
                }
                Received++;
                if (transport.IsHost)
                {
                    peers[sender] = pkt.Key;
                    // Relay to every other peer so clients see each other.
                    foreach (KeyValuePair<int, object> p in peers)
                        if (p.Key != sender) SendRaw(pkt.Value, p.Value, reliable);
                }
            }
        }

        public void Forget(int id)
        {
            peers.Remove(id);
        }

        void SendRaw(byte[] data, object ep, bool reliable)
        {
            if (transport.Send(ep, data, reliable)) Sent++;
            else Dropped++;
        }

        static BinaryWriter Begin(MemoryStream ms, byte type)
        {
            BinaryWriter w = new BinaryWriter(ms);
            w.Write(Magic); w.Write(Version); w.Write(type);
            return w;
        }

        static byte[] EncodeState(PlayerState s)
        {
            using (MemoryStream ms = new MemoryStream(256))
            {
                BinaryWriter w = Begin(ms, MsgState);
                w.Write(s.Id); w.Write(s.Seq);
                w.Write(s.Name ?? ""); w.Write(s.Scene ?? ""); w.Write(s.World ?? "");
                w.Write(s.X); w.Write(s.Y); w.Write(s.Z); w.Write(s.Yaw);
                w.Write((byte)s.LayerStates.Length);
                for (int i = 0; i < s.LayerStates.Length; i++) { w.Write(s.LayerStates[i]); w.Write(s.LayerTimes[i]); }
                w.Write((byte)s.Params.Length);
                foreach (float f in s.Params) w.Write(f);
                w.Write(s.Speed); w.Write(s.Clip ?? ""); w.Write(s.WeaponFlags); w.Write(s.TravelSpeed); w.Write(s.Dead); w.Write(s.Stealth); w.Write(s.InDungeon);
                w.Flush();
                return ms.ToArray();
            }
        }

        static byte[] EncodeAppearance(Appearance a)
        {
            using (MemoryStream ms = new MemoryStream(256))
            {
                BinaryWriter w = Begin(ms, MsgAppearance);
                w.Write(a.Id); w.Write(a.Version);
                foreach (int m in a.Models) w.Write(m);
                w.Write(a.Hair); w.Write(a.Beard); w.Write(a.HairColor); w.Write(a.Head);
                w.Write(a.BodyType); w.Write(a.SkinColor); w.Write(a.FaceDetail); w.Write(a.EyeColor);
                w.Write(a.IsMale);
                w.Write((byte)Math.Min(a.AdditionalData.Count, 255));
                for (int i = 0; i < a.AdditionalData.Count && i < 255; i++) w.Write(a.AdditionalData[i] ?? "");
                w.Write(a.WeaponName ?? ""); w.Write(a.WeaponNameSecondary ?? "");
                w.Write(a.Signature); w.Write(a.ShowHelmet); w.Write(a.ShowCloak);
                w.Flush();
                return ms.ToArray();
            }
        }

        bool Decode(byte[] data, out int sender, out bool reliable)
        {
            sender = 0;
            reliable = data.Length > 3 && data[3] == MsgAppearance;
            if (data.Length >= 14 && data[3] >= 10)
            {
                if (BitConverter.ToUInt16(data, 0) != Magic || data[2] != Version) return false;
                reliable = data[4] != 0;
                sender = BitConverter.ToInt32(data, 5);
                int target = BitConverter.ToInt32(data, 9);
                if (sender != LocalId && (target == 0 || target == LocalId))
                {
                    byte[] payload = new byte[data.Length - 14];
                    Buffer.BlockCopy(data, 14, payload, 0, payload.Length);
                    Messages.Add(new Message { Type = data[3], Sender = sender, Target = target, Payload = payload });
                }
                return true;
            }
            try
            {
                using (BinaryReader r = new BinaryReader(new MemoryStream(data)))
                {
                    if (r.ReadUInt16() != Magic || r.ReadByte() != Version) return false;
                    byte type = r.ReadByte();
                    if (type == MsgState)
                    {
                        PlayerState s = new PlayerState();
                        s.Id = r.ReadInt32(); s.Seq = r.ReadUInt32();
                        s.Name = Plugin.CleanName(r.ReadString()); if (s.Name.Length == 0) s.Name = "Player"; s.Scene = r.ReadString(); s.World = r.ReadString();
                        s.X = r.ReadSingle(); s.Y = r.ReadSingle(); s.Z = r.ReadSingle(); s.Yaw = r.ReadSingle();
                        int layers = r.ReadByte();
                        s.LayerStates = new int[layers]; s.LayerTimes = new float[layers];
                        for (int i = 0; i < layers; i++) { s.LayerStates[i] = r.ReadInt32(); s.LayerTimes[i] = r.ReadSingle(); }
                        s.Params = new float[r.ReadByte()];
                        for (int i = 0; i < s.Params.Length; i++) s.Params[i] = r.ReadSingle();
                        s.Speed = r.ReadSingle(); s.Clip = r.ReadString(); s.WeaponFlags = r.ReadByte(); s.TravelSpeed = r.ReadSingle(); s.Dead = r.ReadBoolean(); s.Stealth = r.ReadBoolean(); s.InDungeon = r.ReadBoolean();
                        States.Add(s);
                        sender = s.Id;
                        return true;
                    }
                    if (type == MsgAppearance)
                    {
                        Appearance a = new Appearance();
                        a.Id = r.ReadInt32(); a.Version = r.ReadInt32();
                        for (int i = 0; i < a.Models.Length; i++) a.Models[i] = r.ReadInt32();
                        a.Hair = r.ReadInt32(); a.Beard = r.ReadInt32(); a.HairColor = r.ReadInt32(); a.Head = r.ReadInt32();
                        a.BodyType = r.ReadInt32(); a.SkinColor = r.ReadInt32(); a.FaceDetail = r.ReadInt32(); a.EyeColor = r.ReadInt32();
                        a.IsMale = r.ReadBoolean();
                        int n = r.ReadByte();
                        for (int i = 0; i < n; i++) a.AdditionalData.Add(r.ReadString());
                        a.WeaponName = r.ReadString(); a.WeaponNameSecondary = r.ReadString();
                        a.Signature = r.ReadInt32(); a.ShowHelmet = r.ReadBoolean(); a.ShowCloak = r.ReadBoolean();
                        Appearances.Add(a);
                        sender = a.Id;
                        return true;
                    }
                    return false;
                }
            }
            catch (EndOfStreamException) { return false; }
        }

        public void Dispose()
        {
            transport.Dispose();
        }
    }
}

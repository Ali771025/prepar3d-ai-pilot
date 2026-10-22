using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace P3DAIPilot.Bridge
{
    public enum MessageType : ushort { Hello=1, HelloAck, Heartbeat, Telemetry, Command, CommandAck, Status, Error, Capabilities, Reset, ControlAcquire, ControlRelease }
    public sealed class Message
    {
        public MessageType Type { get; set; }
        public ulong Sequence { get; set; }
        public long TimestampMs { get; set; }
        public byte[] Payload { get; set; } = new byte[0];
    }
    public static class Protocol
    {
        public const uint Magic = 0x50494150; // little-endian PAIP
        public const ushort Version = 1;
        public const int HeaderSize = 32, MaxPayload = 4096;
        public static long NowMs { get { return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); } }
        public static uint Checksum(byte[] header, byte[] payload)
        {
            uint crc = uint.MaxValue;
            for (int i=0; i<28+payload.Length; ++i)
            {
                crc ^= i<28 ? header[i] : payload[i-28];
                for (int bit=0; bit<8; ++bit) crc = (crc >> 1) ^ ((crc & 1)!=0 ? 0xEDB88320u : 0u);
            }
            return ~crc;
        }
        public static byte[] Encode(Message m)
        {
            if (m == null || m.Payload == null || m.Payload.Length > MaxPayload || !Enum.IsDefined(typeof(MessageType),m.Type))
                throw new InvalidDataException("Invalid message.");
            using (var stream = new MemoryStream()) using (var w = new BinaryWriter(stream))
            {
                w.Write(Magic); w.Write(Version); w.Write((ushort)m.Type); w.Write(m.Sequence);
                w.Write(m.TimestampMs); w.Write(m.Payload.Length); w.Write(0u); w.Write(m.Payload);
                byte[] result=stream.ToArray(); uint crc=Checksum(result,m.Payload);
                byte[] bytes=BitConverter.GetBytes(crc); Array.Copy(bytes,0,result,28,4); return result;
            }
        }
        public static Message Decode(byte[] bytes)
        {
            if (bytes==null || bytes.Length<HeaderSize) throw new InvalidDataException("Truncated header.");
            using (var r=new BinaryReader(new MemoryStream(bytes)))
            {
                if (r.ReadUInt32()!=Magic || r.ReadUInt16()!=Version) throw new InvalidDataException("Protocol mismatch.");
                var m=new Message {Type=(MessageType)r.ReadUInt16(),Sequence=r.ReadUInt64(),TimestampMs=r.ReadInt64()};
                int size=r.ReadInt32(); uint crc=r.ReadUInt32();
                if (size<0 || size>MaxPayload || bytes.Length!=HeaderSize+size || !Enum.IsDefined(typeof(MessageType),m.Type))
                    throw new InvalidDataException("Invalid framing.");
                m.Payload=r.ReadBytes(size); if (Checksum(bytes,m.Payload)!=crc) throw new InvalidDataException("CRC mismatch.");
                return m;
            }
        }
        private static async Task ReadExact(Stream stream, byte[] bytes, CancellationToken token)
        {
            int offset=0;
            while (offset<bytes.Length)
            {
                int count=await stream.ReadAsync(bytes,offset,bytes.Length-offset,token).ConfigureAwait(false);
                if (count==0) throw new EndOfStreamException("Bridge disconnected."); offset+=count;
            }
        }
        public static async Task<Message> ReadAsync(Stream stream, CancellationToken token)
        {
            var header=new byte[HeaderSize]; await ReadExact(stream,header,token).ConfigureAwait(false);
            int size=BitConverter.ToInt32(header,24);
            if (size<0 || size>MaxPayload) throw new InvalidDataException("Payload limit exceeded.");
            var bytes=new byte[HeaderSize+size]; Array.Copy(header,bytes,HeaderSize);
            var payload=new byte[size]; await ReadExact(stream,payload,token).ConfigureAwait(false);
            Array.Copy(payload,0,bytes,HeaderSize,size); return Decode(bytes);
        }
        public static byte[] Payload(Action<BinaryWriter> write)
        { using(var s=new MemoryStream()) using(var w=new BinaryWriter(s)) {write(w);return s.ToArray();} }
    }
}

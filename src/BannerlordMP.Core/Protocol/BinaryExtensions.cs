using System;
using System.Collections.Generic;
using System.IO;

namespace BannerlordMP.Core.Protocol
{
    internal static class BinaryExtensions
    {
        private const int MaxListLength = 1 << 20;

        public static void WriteList<T>(this BinaryWriter writer, List<T> items, Action<BinaryWriter, T> writeItem)
        {
            writer.Write(items.Count);
            foreach (var item in items)
                writeItem(writer, item);
        }

        public static List<T> ReadList<T>(this BinaryReader reader, Func<BinaryReader, T> readItem)
        {
            var count = reader.ReadInt32();
            if (count < 0 || count > MaxListLength)
                throw new InvalidDataException($"List length {count} out of range.");
            var items = new List<T>(count);
            for (var i = 0; i < count; i++)
                items.Add(readItem(reader));
            return items;
        }

        public static void WriteByteArray(this BinaryWriter writer, byte[] bytes)
        {
            bytes = bytes ?? Array.Empty<byte>();
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }

        public static byte[] ReadByteArray(this BinaryReader reader, int maxLength)
        {
            var length = reader.ReadInt32();
            if (length < 0 || length > maxLength)
                throw new InvalidDataException($"Byte array length {length} out of range.");
            var bytes = reader.ReadBytes(length);
            if (bytes.Length != length)
                throw new EndOfStreamException();
            return bytes;
        }

        public static void WriteCounters(this BinaryWriter writer, Dictionary<string, int> counters)
        {
            writer.Write(counters.Count);
            foreach (var pair in counters)
            {
                writer.WriteNullable(pair.Key);
                writer.Write(pair.Value);
            }
        }

        public static Dictionary<string, int> ReadCounters(this BinaryReader reader)
        {
            var count = reader.ReadInt32();
            if (count < 0 || count > MaxListLength)
                throw new InvalidDataException($"Counter count {count} out of range.");
            var counters = new Dictionary<string, int>(count);
            for (var i = 0; i < count; i++)
                counters[reader.ReadString()] = reader.ReadInt32();
            return counters;
        }

        public static void WriteNullable(this BinaryWriter writer, string value) => writer.Write(value ?? string.Empty);
    }
}

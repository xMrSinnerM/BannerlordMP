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

        public static void WriteNullable(this BinaryWriter writer, string value) => writer.Write(value ?? string.Empty);
    }
}

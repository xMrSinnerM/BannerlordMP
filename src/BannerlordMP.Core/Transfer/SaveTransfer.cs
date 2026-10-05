using System;
using System.IO;
using System.Security.Cryptography;

namespace BannerlordMP.Core.Transfer
{
    /// <summary>Splits a save file into chunks the transport can carry, paced by the caller.</summary>
    public sealed class SaveSender
    {
        public const int DefaultChunkSize = 48 * 1024;

        private readonly byte[] _data;
        private readonly int _chunkSize;

        public SaveSender(byte[] data, int chunkSize = DefaultChunkSize)
        {
            _data = data ?? throw new ArgumentNullException(nameof(data));
            _chunkSize = chunkSize;
            Hash = SaveReceiver.ComputeHash(data);
        }

        public long Size => _data.Length;
        public byte[] Hash { get; }
        public long Sent { get; private set; }
        public bool Done => Sent >= _data.Length;

        /// <summary>Returns the next chunk without advancing; call <see cref="Advance"/> once the transport accepted it.</summary>
        public (long Offset, byte[] Data) Peek()
        {
            var length = (int)Math.Min(_chunkSize, _data.Length - Sent);
            var chunk = new byte[length];
            Buffer.BlockCopy(_data, (int)Sent, chunk, 0, length);
            return (Sent, chunk);
        }

        public void Advance(int length) => Sent += length;
    }

    /// <summary>Reassembles a save from chunks and checks it arrived intact.</summary>
    public sealed class SaveReceiver
    {
        public const long MaxSize = 512L * 1024 * 1024;

        private readonly byte[] _data;
        private readonly byte[] _hash;

        public SaveReceiver(long size, byte[] hash)
        {
            if (size <= 0 || size > MaxSize)
                throw new InvalidDataException($"Save size {size} out of range.");
            _data = new byte[size];
            _hash = hash;
        }

        public long Size => _data.Length;
        public long Received { get; private set; }
        public bool Complete => Received == _data.Length;
        public float Progress => (float)Received / _data.Length;

        public void Add(long offset, byte[] chunk)
        {
            if (offset != Received)
                throw new InvalidDataException($"Chunk at {offset}, expected {Received}.");
            if (offset + chunk.Length > _data.Length)
                throw new InvalidDataException("Chunk past the end of the save.");
            Buffer.BlockCopy(chunk, 0, _data, (int)offset, chunk.Length);
            Received += chunk.Length;
        }

        /// <summary>The complete save, after verifying its hash.</summary>
        public byte[] GetVerifiedData()
        {
            if (!Complete)
                throw new InvalidOperationException("Save not fully received.");
            if (!Security.PasswordProof.FixedTimeEquals(ComputeHash(_data), _hash))
                throw new InvalidDataException("Save file is corrupt (hash mismatch).");
            return _data;
        }

        public static byte[] ComputeHash(byte[] data)
        {
            using (var sha = SHA256.Create())
                return sha.ComputeHash(data);
        }
    }
}

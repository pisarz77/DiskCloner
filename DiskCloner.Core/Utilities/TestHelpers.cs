using System;
using System.IO;
using System.Security.Cryptography;

namespace DiskCloner.Core.Utilities
{
    /// <summary>
    /// Small helpers to assist unit testing of hashing and padding logic.
    /// Kept simple and deterministic so tests can validate behavior without accessing disks.
    /// </summary>
    public static class TestHelpers
    {
        public static byte[] ComputeHashFromStream(Stream stream, HashAlgorithm hashAlgorithm, int bufferSize = 1024 * 1024)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            if (hashAlgorithm == null) throw new ArgumentNullException(nameof(hashAlgorithm));

            var buffer = new byte[bufferSize];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                hashAlgorithm.TransformBlock(buffer, 0, read, null, 0);
            }

            hashAlgorithm.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return hashAlgorithm.Hash ?? Array.Empty<byte>();
        }

        public static byte[] GetSectorPaddedBuffer(byte[] sourceBuffer, int bytesRead, int sectorSize)
        {
            if (sourceBuffer == null) throw new ArgumentNullException(nameof(sourceBuffer));
            if (bytesRead < 0 || bytesRead > sourceBuffer.Length) throw new ArgumentOutOfRangeException(nameof(bytesRead));
            if (sectorSize <= 0) throw new ArgumentOutOfRangeException(nameof(sectorSize));

            var bytesToWrite = ((bytesRead + sectorSize - 1) / sectorSize) * sectorSize;
            var outBuf = new byte[bytesToWrite];
            Array.Copy(sourceBuffer, 0, outBuf, 0, bytesRead);
            // remainder already zero-initialized
            return outBuf;
        }
    }
}

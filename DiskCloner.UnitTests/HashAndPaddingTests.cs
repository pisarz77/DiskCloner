using System;
using System.IO;
using System.Security.Cryptography;
using DiskCloner.Core.Utilities;
using Xunit;

namespace DiskCloner.UnitTests
{
    public class HashAndPaddingTests
    {
        [Fact]
        public void ComputeHashFromStream_MatchesDirectComputeHash()
        {
            var rnd = new Random(12345);
            var data = new byte[1024 * 10];
            rnd.NextBytes(data);

            using var ms = new MemoryStream(data);
            using var sha1 = SHA256.Create();
            var hashFromStream = TestHelpers.ComputeHashFromStream(ms, sha1, 4096);

            // compute directly
            using var sha2 = SHA256.Create();
            var directHash = sha2.ComputeHash(data);

            Assert.Equal(directHash, hashFromStream);
        }

        [Fact]
        public void GetSectorPaddedBuffer_PadsToSectorBoundary()
        {
            var rnd = new Random(42);
            var source = new byte[1000];
            rnd.NextBytes(source);

            int bytesRead = 1000;
            int sectorSize = 512;

            var padded = TestHelpers.GetSectorPaddedBuffer(source, bytesRead, sectorSize);

            Assert.Equal(1024, padded.Length); // 1000 -> 1024 (two sectors)
            for (int i = 0; i < bytesRead; i++)
            {
                Assert.Equal(source[i], padded[i]);
            }
            for (int i = bytesRead; i < padded.Length; i++)
            {
                Assert.Equal(0, padded[i]);
            }
        }

        [Fact]
        public void GetSectorPaddedBuffer_ZeroBytes_ReadsToZeroLengthSector()
        {
            var source = new byte[10];
            int bytesRead = 0;
            int sectorSize = 512;

            var padded = TestHelpers.GetSectorPaddedBuffer(source, bytesRead, sectorSize);
            Assert.Equal(0, padded.Length == 0 ? 0 : padded.Length); // bytesToWrite == 0
        }
    }
}

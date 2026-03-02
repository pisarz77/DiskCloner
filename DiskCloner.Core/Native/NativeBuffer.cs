using System;
using System.Runtime.InteropServices;

namespace DiskCloner.Core.Native
{
    internal sealed class NativeBuffer : IDisposable
    {
        public IntPtr Pointer { get; }
        public int Size { get; }

        // Small reusable zero page for efficient small-range zeroing (pads are typically < sector size)
        private static readonly byte[] _zeroPage = new byte[4096];

        public NativeBuffer(int size)
        {
            if (size <= 0) throw new ArgumentOutOfRangeException(nameof(size));
            Size = size;
            Pointer = Marshal.AllocHGlobal(size);
        }

        public void Zero(int offset, int count)
        {
            if (count <= 0) return;
            if (offset < 0 || offset + count > Size) throw new ArgumentOutOfRangeException();

            int remaining = count;
            int cur = offset;
            while (remaining > 0)
            {
                int chunk = Math.Min(_zeroPage.Length, remaining);
                Marshal.Copy(_zeroPage, 0, IntPtr.Add(Pointer, cur), chunk);
                remaining -= chunk;
                cur += chunk;
            }
        }

        public void Dispose()
        {
            if (Pointer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(Pointer);
            }
            GC.SuppressFinalize(this);
        }
    }
}

﻿using System.Runtime.CompilerServices;
using Unity.IO.LowLevel.Unsafe;

namespace Scipts.LowLevel
{
    static class ReadCommandBufferUtils
    {
        [MethodImpl(256)] //256 is the value of MethodImplOptions.AggresiveInlining
        public unsafe static ReadCommand GetCommand(void* buffer, long readSize, long offset)
        {
            var cmd = new ReadCommand();
            cmd.Buffer = buffer;
            cmd.Size = readSize;
            cmd.Offset = offset;

            return cmd;
        }
    }
}
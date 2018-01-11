// Graph Engine
// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE.md file in the project root for full license information.
//
using FanoutSearch.Protocols.TSL;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Trinity;
using System.Collections.Concurrent;
using Trinity.Core.Lib;
using Trinity.Network.Messaging;
using System.Threading;
using System.Runtime.CompilerServices;

namespace FanoutSearch
{
    unsafe class MessageDispatcher : IDisposable
    {
        static int s_ServerCount;
        static FanoutSearchModule s_Module;

        const int c_default_capacity  = 64;
        const int c_realloc_threshold = 1 << 20;
        const int c_realloc_step_size = 1 << 20;
        private byte*[] buffer;
        private int[] buf_offset;
        private int[] buf_capacity;
        private SpinLock[] buffer_locks;
        private int hop;
        private int transaction;
        private int serverCount;


        static MessageDispatcher()
        {
            s_ServerCount = Global.CloudStorage.PartitionCount;
            s_Module = Global.CloudStorage.GetCommunicationModule<FanoutSearchModule>();
        }

        public MessageDispatcher(int hop, int transaction)
        {
            this.hop = hop;
            this.transaction = transaction;
            this.serverCount = s_ServerCount;
            buffer = new byte*[serverCount];
            buf_offset = new int[serverCount];
            buf_capacity = new int[serverCount];
            buffer_locks = new SpinLock[serverCount];
            int default_size   = c_default_capacity * sizeof(long) + TrinityProtocol.MsgHeader + sizeof(int)*2;

            for (int i = 0; i < serverCount; ++i)
            {
                buffer[i] = (byte*)Memory.malloc((uint)default_size);
                buf_offset[i] = sizeof(int) * 2 + TrinityProtocol.MsgHeader;
                buf_capacity[i] = default_size;
                buffer_locks[i] = new SpinLock();
            }
        }

        public unsafe void addAugmentedPath(long* pathptr, int current_hop, long next_cell)
        {
            int slaveID = Global.CloudStorage.GetPartitionIdByCellId(next_cell);
            int path_size = sizeof(long) * (current_hop + 2);

            // not lock free, but easy to read through.
            bool _lock = false;
            try
            {
                buffer_locks[slaveID].Enter(ref _lock);
                long boffset = buf_offset[slaveID] + path_size;
                int  bnewcap = buf_capacity[slaveID];
                if (boffset >= int.MaxValue) return;
                while (boffset > bnewcap)
                {
                    if (bnewcap < c_realloc_threshold) bnewcap *= 2;
                    else bnewcap += c_realloc_step_size;
                    if (bnewcap <= 0) return;//overflow, abort path addition
                }

                buf_offset[slaveID] = (int)boffset;
                if (bnewcap != buf_capacity[slaveID])
                {
                    byte* new_buf = (byte*)Memory.malloc((uint)bnewcap);
                    Memory.memcpy(new_buf, buffer[slaveID], (uint)buf_capacity[slaveID]);
                    Memory.free(buffer[slaveID]);

                    buffer[slaveID] = new_buf;
                    buf_capacity[slaveID] = bnewcap;
                }

                long* add_ptr = (long*)(buffer[slaveID] + boffset - path_size);
                Memory.memcpy(add_ptr, pathptr, (uint)(current_hop + 1) * sizeof(long));
                add_ptr[current_hop + 1] = next_cell;
            }
            finally
            {
                if (_lock) buffer_locks[slaveID].Exit(useMemoryBarrier: true);
            }
        }

        public void Dispatch()
        {
            if (buffer == null)
                return;

            Parallel.For(0, serverCount, serverId =>
            {
                var bufferPtr = this.buffer[serverId];
                FillHeader(hop, transaction, buf_offset[serverId], bufferPtr);

                s_Module.FanoutSearch_impl_Send(serverId, bufferPtr, buf_offset[serverId]);
                Memory.free(bufferPtr);
            });

            buffer = null;
        }

        public static unsafe void DispatchOriginMessage(int serverId, int transaction, IEnumerable<FanoutPathDescriptor> origins)
        {
            int origin_cnt  = origins.Count();
            int msg_size    = (TrinityProtocol.MsgHeader + 3 * sizeof(int) + origin_cnt * sizeof(long));
            byte* bufferPtr = (byte*)Memory.malloc((uint)msg_size);

            byte* body = FillHeader(0, transaction, msg_size, bufferPtr);

            long* originPtr = (long*)(body + 2 * sizeof(int));
            foreach (var origin_desc in origins)
            {
                *originPtr++ = origin_desc.hop_0;
            }

            s_Module.FanoutSearch_impl_Send(serverId, bufferPtr, msg_size);
            Memory.free(bufferPtr);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe byte* FillHeader(int hop, int transaction, int msg_size, byte* bufferPtr)
        {
            byte* body = bufferPtr + TrinityProtocol.MsgHeader;

            *(bufferPtr + TrinityProtocol.MsgTypeOffset) = (byte)TrinityMessageType.ASYNC;
            *(ushort*)(bufferPtr + TrinityProtocol.MsgIdOffset) = (ushort)global::FanoutSearch.Protocols.TSL.TSL.CommunicationModule.FanoutSearch.AsynReqMessageType.FanoutSearch_impl;
            *(int*)bufferPtr = msg_size - sizeof(int);
            *(int*)(body) = hop;
            *(int*)(body + sizeof(int)) = transaction;
            return body;
        }

        public void Dispose()
        {
            if (buffer != null)
                Dispatch();
        }

    }
}
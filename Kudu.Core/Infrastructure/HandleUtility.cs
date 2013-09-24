﻿using System;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Kudu.Core.Infrastructure
{
    public static class HandleUtility
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct SystemHandleEntry
        {
            public int OwnerProcessId;
            public byte ObjectTypeNumber;
            public byte Flags;
            public ushort Handle;
            public IntPtr Object;
            public int GrantedAccess;
        }

        public static IEnumerable<HandleInfo> GetHandles(int processId)
        {            
            int length = 0x10000;
            IntPtr ptr = IntPtr.Zero;
            try
            {
                while (true)
                {
                    ptr = Marshal.AllocHGlobal(length);
                    int wantedLength;
                    var result = 
                        NativeMethods.NtQuerySystemInformation(
                        SYSTEM_INFORMATION_CLASS.SystemHandleInformation, ptr, length, out wantedLength);

                    if (result == NT_STATUS.STATUS_INFO_LENGTH_MISMATCH)
                    {
                        length = Math.Max(length, wantedLength);
                        Marshal.FreeHGlobal(ptr);
                        ptr = IntPtr.Zero;
                    }
                    else if (result == NT_STATUS.STATUS_SUCCESS)
                        break;
                    else
                        throw new Exception("Failed to retrieve system handle information.");
                }

                int handleCount = IntPtr.Size == 4 ? Marshal.ReadInt32(ptr) : (int)Marshal.ReadInt64(ptr);
                int offset = IntPtr.Size;
                int size = Marshal.SizeOf(typeof(SystemHandleEntry));
                for (int i = 0; i < handleCount; i++)
                {
                    var handleEntry = 
                        (SystemHandleEntry)Marshal.PtrToStructure(
                        (IntPtr)((int)ptr + offset), typeof(SystemHandleEntry));

                    if (handleEntry.OwnerProcessId == processId)
                    {
                        yield return new HandleInfo(
                            handleEntry.OwnerProcessId,
                            handleEntry.Handle,
                            handleEntry.GrantedAccess,
                            handleEntry.ObjectTypeNumber);
                    }

                    offset += size;
                }
            }
            finally
            {
                if (ptr != IntPtr.Zero)
                    Marshal.FreeHGlobal(ptr);
            }
        }
    }
}

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FastGithub.DomainResolve
{
    /// <summary>
    /// 进程Job
    /// 
    /// 把子进程加入一个设置了 JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE 的Job之后，
    /// 只要该Job的句柄被关闭(包括进程被强杀、崩溃导致句柄被系统回收)，
    /// 内核就会连同Job内的所有进程一起结束。
    /// 
    /// 这是唯一能覆盖"父进程被强制杀死"这种退出方式的清理手段：
    /// 依赖 Host 生命周期的 StopAsync 在强杀时根本不会执行。
    /// </summary>
    sealed class ProcessJob : IDisposable
    {
        private const int JobObjectExtendedLimitInformation = 9;
        private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;

        private IntPtr handle;

        private ProcessJob(IntPtr handle)
        {
            this.handle = handle;
        }

        /// <summary>
        /// 创建Job，失败时返回null(调用方应继续以原有方式运行)
        /// </summary>
        /// <returns></returns>
        public static ProcessJob? TryCreate()
        {
            var jobHandle = CreateJobObjectW(IntPtr.Zero, null);
            if (jobHandle == IntPtr.Zero)
            {
                return null;
            }

            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                }
            };

            var length = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            var pointer = Marshal.AllocHGlobal(length);
            try
            {
                Marshal.StructureToPtr(info, pointer, fDeleteOld: false);
                if (SetInformationJobObject(jobHandle, JobObjectExtendedLimitInformation, pointer, (uint)length) == false)
                {
                    CloseHandle(jobHandle);
                    return null;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(pointer);
            }

            return new ProcessJob(jobHandle);
        }

        /// <summary>
        /// 把进程加入Job
        /// </summary>
        /// <param name="process"></param>
        /// <returns></returns>
        public bool TryAssign(Process process)
        {
            if (this.handle == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                return AssignProcessToJobObject(this.handle, process.Handle);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 关闭Job句柄，Job内的进程会被内核结束
        /// </summary>
        public void Dispose()
        {
            var jobHandle = this.handle;
            this.handle = IntPtr.Zero;
            if (jobHandle != IntPtr.Zero)
            {
                CloseHandle(jobHandle);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr hJob, int jobObjectInfoClass, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);
    }
}

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PiWeb_Launcher.Services
{
    /// <summary>
    /// 把 Pi Web 服务进程树放进一个带 <c>KILL_ON_JOB_CLOSE</c> 标志的 Windows 作业对象:
    /// 作业句柄由本应用进程持有,当应用进程意外终止(崩溃、被任务管理器强杀、断电等)时,
    /// 内核自动关闭句柄并杀掉作业内全部进程;正常退出路径由 <see cref="PiWebService.Stop"/> 兜底。
    /// <para>
    /// 为什么单独成类(而不是像上游那样挂在服务类的 partial 里):这段是**纯平台互操作**,
    /// 与"启动/停止/日志"没有共同状态。分开之后 P/Invoke 的结构体声明不会再夹在业务代码中间,
    /// 服务类也少一个 <c>partial</c> 的理由。
    /// </para>
    /// <para>
    /// ⚠ 作业对象是**进程级**单份:同一时刻只该有一个服务进程,所以句柄挂在静态字段上。
    /// 启动新服务前先关掉旧句柄(旧进程已停止,它的 KillOnClose 不再有目标)。
    /// </para>
    /// </summary>
    internal static class ChildProcessJobObject
    {
        /// <summary>与服务进程树关联的作业对象句柄;未启用/启用失败时为 <see cref="IntPtr.Zero"/>。</summary>
        private static IntPtr _jobHandle;

        /// <summary>
        /// 把进程分配到新的"关闭即杀进程树"作业中,失败静默(只影响意外退出时的兜底清理)。
        /// 非 Windows 平台直接跳过:用户点停止与正常退出都走
        /// <c>Process.Kill(entireProcessTree: true)</c>(.NET 的 KillTree 本身跨平台);
        /// 仅"应用被强杀且来不及执行任何托管代码"这一场景在 macOS/Linux 无兜底
        /// (纯托管 API 无 setpgid,按约定不引入平台 P/Invoke)。
        /// </summary>
        public static void Assign(Process process)
        {
            // macOS/Linux 没有作业对象;P/Invoke kernel32 会抛 DllNotFoundException,必须早退
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            var job = NativeJob.CreateKillOnCloseJob();
            if (job == IntPtr.Zero || !NativeJob.AssignProcessToJob(job, process.Handle))
            {
                // 绑定失败不影响正常启停功能,仅失去意外退出时的自动清理能力
                if (job != IntPtr.Zero)
                {
                    NativeJob.Close(job);
                }

                AppLogService.Write("[Job] 将服务进程绑定到作业对象失败,意外退出时可能无法自动停止服务");
                return;
            }

            CloseJobHandle();
            _jobHandle = job;
        }

        /// <summary>关闭作业句柄(内核会杀掉作业内残留进程,若还有的话)。停止服务与应用退出时调用。</summary>
        public static void CloseJobHandle()
        {
            if (_jobHandle != IntPtr.Zero)
            {
                NativeJob.Close(_jobHandle);
                _jobHandle = IntPtr.Zero;
            }
        }

        /// <summary>Win32 Job Object 互操作。仅本类内使用。</summary>
        private static class NativeJob
        {
            private const int JobObjectExtendedLimitInformation = 9;
            private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

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

            // 必须整块 Sequential 布局:x64 对齐下与原生结构完全一致
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

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool SetInformationJobObject(
                IntPtr hJob,
                int JobObjectInformationClass,
                ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInformation,
                int cbJobObjectInformationLength);

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool CloseHandle(IntPtr hObject);

            /// <summary>创建设置了 KILL_ON_JOB_CLOSE 的作业对象并返回句柄;失败返回 IntPtr.Zero。</summary>
            public static IntPtr CreateKillOnCloseJob()
            {
                var job = CreateJobObjectW(IntPtr.Zero, null);
                if (job == IntPtr.Zero)
                {
                    return IntPtr.Zero;
                }

                var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
                if (!SetInformationJobObject(
                        job,
                        JobObjectExtendedLimitInformation,
                        ref info,
                        Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
                {
                    CloseHandle(job);
                    return IntPtr.Zero;
                }

                return job;
            }

            public static bool AssignProcessToJob(IntPtr job, IntPtr process) => AssignProcessToJobObject(job, process);

            public static void Close(IntPtr handle) => CloseHandle(handle);
        }
    }
}

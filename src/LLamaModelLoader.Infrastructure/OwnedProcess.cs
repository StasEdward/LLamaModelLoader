using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using LLamaModelLoader.Core;

namespace LLamaModelLoader.Infrastructure;

/// <summary>Creates a suspended Windows child, assigns its job, then resumes it.</summary>
public sealed class OwnedProcess : IDisposable
{
    public Process Process { get; private set; } = null!;
    public StreamReader Output { get; private set; } = null!;
    public StreamReader Error { get; private set; } = null!;
    private nint _job;
    private nint _nativeProcess;
    public int ExitCode
    {
        get { if (!GetExitCodeProcess(_nativeProcess, out var code)) Throw(); return unchecked((int)code); }
    }

    public static OwnedProcess Start(string executable, IReadOnlyList<string> arguments)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("This version supports Windows only.");
        var child = new OwnedProcess();
        nint outputRead = 0, outputWrite = 0, errorRead = 0, errorWrite = 0, input = 0, environment = 0;
        ProcessInformation pi = default;
        try
        {
            child._job = CreateJobObjectW(0, null);
            if (child._job == 0) Throw();
            var limits = new JobExtendedLimits();
            limits.Basic.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            if (!SetInformationJobObject(child._job, 9, ref limits, (uint)Marshal.SizeOf<JobExtendedLimits>())) Throw();
            var security = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Inherit = 1 };
            if (!CreatePipe(out outputRead, out outputWrite, ref security, 0) || !CreatePipe(out errorRead, out errorWrite, ref security, 0)) Throw();
            if (!SetHandleInformation(outputRead, 1, 0) || !SetHandleInformation(errorRead, 1, 0)) Throw();
            input = CreateFileW("NUL", 0x80000000, 3, ref security, 3, 0, 0);
            if (input == -1) Throw();
            var start = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>(), Flags = 0x100, Input = input, Output = outputWrite, Error = errorWrite };
            var variables = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
                .Where(x => !((string)x.Key).StartsWith("LLAMA_ARG_", StringComparison.OrdinalIgnoreCase))
                .Select(x => $"{x.Key}={x.Value}").Order(StringComparer.OrdinalIgnoreCase);
            environment = Marshal.StringToHGlobalUni(string.Join('\0', variables) + "\0\0");
            var command = new StringBuilder(string.Join(" ", new[] { executable }.Concat(arguments).Select(Quote)));
            // CREATE_SUSPENDED | CREATE_NO_WINDOW | CREATE_UNICODE_ENVIRONMENT | CREATE_NEW_PROCESS_GROUP
            if (!CreateProcessW(executable, command, 0, 0, true, 0x08000604, environment, Path.GetDirectoryName(executable), ref start, out pi)) Throw();
            if (!AssignProcessToJobObject(child._job, pi.Process)) Throw();
            child.Process = Process.GetProcessById((int)pi.ProcessId);
            child.Output = new StreamReader(new FileStream(new SafeFileHandle(outputRead, true), FileAccess.Read), Encoding.UTF8);
            outputRead = 0;
            child.Error = new StreamReader(new FileStream(new SafeFileHandle(errorRead, true), FileAccess.Read), Encoding.UTF8);
            errorRead = 0;
            if (ResumeThread(pi.Thread) == uint.MaxValue) Throw();
            child._nativeProcess = pi.Process;
            pi.Process = 0;
            return child;
        }
        catch
        {
            if (pi.Process != 0) TerminateProcess(pi.Process, 1);
            child.Dispose();
            throw;
        }
        finally
        {
            foreach (var handle in new[] { outputRead, outputWrite, errorRead, errorWrite, input, pi.Thread, pi.Process })
                if (handle != 0 && handle != -1) CloseHandle(handle);
            if (environment != 0) Marshal.FreeHGlobal(environment);
        }
    }

    public void Kill()
    {
        if (_job != 0 && !TerminateJobObject(_job, 1)) Throw();
    }

    public async Task WaitForExitAsync(CancellationToken token = default)
    {
        await Process.WaitForExitAsync(token);
        // Launcher executables may spawn the actual server. Wait for the whole job, not just its root.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (true)
        {
            if (!QueryInformationJobObject(_job, 1, out var accounting, (uint)Marshal.SizeOf<JobAccounting>(), 0)) Throw();
            if (accounting.ActiveProcesses == 0) return;
            await Task.Delay(25, timeout.Token);
        }
    }

    internal bool OwnsProcess(int pid)
    {
        var handle = OpenProcess(0x1000, false, pid);
        if (handle == 0) return false;
        try { return IsProcessInJob(handle, _job, out var owns) && owns; }
        finally { CloseHandle(handle); }
    }

    internal ProcessResources ReadResources()
    {
        if (!QueryInformationJobObject(_job, 1, out var accounting, (uint)Marshal.SizeOf<JobAccounting>(), 0)) Throw();
        // Include the actual server spawned by a launcher, not only the tiny launcher itself.
        const int capacity = 1024;
        var bytes = 8 + capacity * IntPtr.Size;
        var buffer = Marshal.AllocHGlobal(bytes);
        try
        {
            if (!QueryJobBuffer(_job, 3, buffer, (uint)bytes, 0)) Throw();
            var count = Math.Min(capacity, Marshal.ReadInt32(buffer, 4));
            long memory = 0; var sampled = 0;
            for (var i = 0; i < count; i++)
            {
                try
                {
                    using var member = Process.GetProcessById(checked((int)Marshal.ReadIntPtr(buffer, 8 + i * IntPtr.Size)));
                    memory += member.WorkingSet64; sampled++;
                }
                catch (ArgumentException) { } // A member may exit between enumeration and sampling.
            }
            return new(TimeSpan.FromTicks(accounting.UserTime + accounting.KernelTime), memory, sampled);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    public void Dispose()
    {
        if (_job != 0) { CloseHandle(_job); _job = 0; }
        Output?.Dispose(); Error?.Dispose(); Process?.Dispose();
        if (_nativeProcess != 0) { CloseHandle(_nativeProcess); _nativeProcess = 0; }
    }

    internal static string Quote(string value)
    {
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var c in value)
        {
            if (c == '\\') { slashes++; continue; }
            result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
            result.Append(c); slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    private static void Throw() => throw new Win32Exception(Marshal.GetLastWin32Error());
    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { public int Length; public nint Descriptor; public int Inherit; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo
    {
        public int Size; public nint Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XChars, YChars, Fill, Flags;
        public ushort Show, ReservedSize; public nint Reserved2, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public nint Process, Thread; public uint ProcessId, ThreadId; }
    [StructLayout(LayoutKind.Sequential)] private struct JobBasicLimits
    {
        public long ProcessTime, JobTime; public uint LimitFlags; public nuint MinWorkingSet, MaxWorkingSet;
        public uint ActiveProcessLimit; public nuint Affinity; public uint Priority, Scheduling;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong A, B, C, D, E, F; }
    [StructLayout(LayoutKind.Sequential)] private struct JobExtendedLimits
    {
        public JobBasicLimits Basic; public IoCounters Io; public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }
    [StructLayout(LayoutKind.Sequential)] private struct JobAccounting
    {
        public long UserTime, KernelTime, PeriodUserTime, PeriodKernelTime;
        public uint PageFaults, TotalProcesses, ActiveProcesses, TerminatedProcesses;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateJobObjectW(nint attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(nint job, int type, ref JobExtendedLimits info, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(nint job, nint process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateJobObject(nint job, uint code);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CreatePipe(out nint read, out nint write, ref SecurityAttributes attributes, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetHandleInformation(nint handle, uint mask, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateFileW(string path, uint access, uint share, ref SecurityAttributes security, uint creation, uint flags, nint template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcessW(string application, StringBuilder command, nint processAttributes, nint threadAttributes, bool inherit, uint flags, nint environment, string? directory, ref StartupInfo startup, out ProcessInformation info);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(nint thread);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(nint process, uint code);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool IsProcessInJob(nint process, nint job, out bool result);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(nint process, out uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool QueryInformationJobObject(nint job, int type, out JobAccounting info, uint length, nint returnLength);
    [DllImport("kernel32.dll", EntryPoint = "QueryInformationJobObject", SetLastError = true)] private static extern bool QueryJobBuffer(nint job, int type, nint info, uint length, nint returnLength);
}

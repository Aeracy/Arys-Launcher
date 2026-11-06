using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ArysLauncher;

public sealed record PhysicalMemory(ulong TotalBytes, ulong AvailableBytes)
{
    public double TotalGiB
    {
        get
        {
            return TotalBytes / 1073741824.0;
        }
    }

    public double AvailableGiB
    {
        get
        {
            return AvailableBytes / 1073741824.0;
        }
    }

    public double UsedGiB
    {
        get
        {
            return Math.Max(0, TotalGiB - AvailableGiB);
        }
    }
}

internal readonly record struct MemoryBudget(PhysicalMemory Memory, int MaximumMb)
{
    public double MaximumGiB => MaximumMb / 1024.0;
    public double GameGiB => Math.Min(MaximumGiB, Memory.AvailableGiB);
    public double RemainingGiB => Math.Max(0, Memory.AvailableGiB - GameGiB);
    public bool HasShortfall => MaximumGiB > Memory.AvailableGiB;

    public string Description
    {
        get
        {
            if (MaximumGiB > Memory.TotalGiB)
            {
                return "游戏内存上限超过本机物理内存，可调低上限。";
            }
            if (HasShortfall)
            {
                return "当前可用内存低于游戏上限。可调低上限或关闭其他程序。";
            }
            return "游戏按需使用内存，最多达到设定的上限。";
        }
    }
}

public static class MemoryAllocation
{
    public const int InitialHeapMb = 512;

    public static PhysicalMemory ReadPhysicalMemory()
    {
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref status))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        return new PhysicalMemory(status.TotalPhysical, Math.Min(status.AvailablePhysical, status.TotalPhysical));
    }

    public static int RecommendedMaximumMb(PhysicalMemory memory)
    {
        var preferred = memory.TotalGiB >= 14 ? 8192 : 6144;
        var reserve = Math.Max(1024, (int)(memory.TotalGiB * 1024 * 0.20));
        var ceiling = Math.Max(1024, (int)(memory.TotalGiB * 1024) - reserve);
        return Math.Min(preferred, ceiling / 512 * 512);
    }

    public static int MaximumMb(LauncherSettings settings, PhysicalMemory memory)
    {
        return settings.AutomaticMemoryAllocation ? RecommendedMaximumMb(memory) : settings.MaximumMemoryMb;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
}

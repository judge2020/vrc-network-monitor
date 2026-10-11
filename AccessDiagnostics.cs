using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace VrcNetworkMonitor;

public static class AccessDiagnostics
{
    [StructLayout(LayoutKind.Sequential)] private struct Luid { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] private struct TokenPrivileges { public uint Count; public Luid Luid; public uint Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct BasicInfo { public nint Reserved1, PebBaseAddress, Reserved2a, Reserved2b, UniqueProcessId, Reserved3; }
    [DllImport("kernel32.dll", SetLastError=true)] private static extern nint OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll")] private static extern nint GetCurrentProcess();
    [DllImport("advapi32.dll", SetLastError=true)] private static extern bool OpenProcessToken(nint process, uint access, out nint token);
    [DllImport("advapi32.dll", CharSet=CharSet.Unicode, SetLastError=true)] private static extern bool LookupPrivilegeValue(string? system, string name, out Luid luid);
    [DllImport("advapi32.dll", SetLastError=true)] private static extern bool AdjustTokenPrivileges(nint token, bool disableAll, ref TokenPrivileges state, uint length, nint previous, nint returned);
    [DllImport("psapi.dll", SetLastError=true)] private static extern bool EnumProcessModulesEx(nint process, [Out] nint[] modules, uint bytes, out uint needed, uint filter);
    [DllImport("psapi.dll", CharSet=CharSet.Unicode, SetLastError=true)] private static extern uint GetModuleFileNameEx(nint process, nint module, StringBuilder name, int size);
    [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(nint process, int infoClass, out BasicInfo info, int length, out int returned);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool ReadProcessMemory(nint process, nint address, byte[] bytes, nuint count, out nuint read);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern nint CreateToolhelp32Snapshot(uint flags, uint pid);

    public static object Run()
    {
        var results = new List<object>();
        using var identity = WindowsIdentity.GetCurrent();
        results.Add(new { Stage="Token", Elevated=new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator) });
        string debugStatus;
        if (OpenProcessToken(GetCurrentProcess(), 0x20 | 8, out nint token))
        {
            try
            {
                if (!LookupPrivilegeValue(null, "SeDebugPrivilege", out var luid)) debugStatus="Lookup failed: " + Marshal.GetLastWin32Error();
                else
                {
                    var state = new TokenPrivileges { Count=1, Luid=luid, Attributes=2 };
                    bool changed=AdjustTokenPrivileges(token, false, ref state, 0, 0, 0);
                    int error=Marshal.GetLastWin32Error(); debugStatus=$"Adjusted={changed}, Win32={error}";
                }
            }
            finally { CloseHandle(token); }
        }
        else debugStatus="Token open failed: " + Marshal.GetLastWin32Error();
        results.Add(new { Stage="SeDebugPrivilege", Result=debugStatus });
        var found=Process.GetProcessesByName("VRChat");
        if(found.Length != 1) { foreach(var p in found)p.Dispose(); results.Add(new {Stage="Select process", Count=found.Length}); return results; }
        using var process=found[0];
        foreach (uint access in new uint[]{0x1000, 0x10, 0x1010, 0x400, 0x410, 0x1410})
        {
            nint handle=OpenProcess(access,false,process.Id); int openError=Marshal.GetLastWin32Error();
            if(handle==0) { results.Add(new {Stage="OpenProcess", Access=$"0x{access:X}", Error=openError}); continue; }
            try
            {
                var modules=new nint[2048];
                bool enumerated=EnumProcessModulesEx(handle,modules,(uint)(modules.Length*IntPtr.Size),out uint needed,3);
                int enumError=Marshal.GetLastWin32Error();
                var matches=new List<object>();
                if(enumerated)
                {
                    foreach(var module in modules.Take((int)Math.Min(needed/8,(uint)modules.Length)))
                    {
                        var path=new StringBuilder(1024); GetModuleFileNameEx(handle,module,path,path.Capacity);
                        if(!Path.GetFileName(path.ToString()).Equals("GameAssembly.dll",StringComparison.OrdinalIgnoreCase))continue;
                        var mz=new byte[2];bool read=ReadProcessMemory(handle,module,mz,2,out nuint size);int error=Marshal.GetLastWin32Error();
                        matches.Add(new {Module="GameAssembly.dll", Base=$"0x{module:X}", Read=read, Bytes=(ulong)size, Error=error, Mz=Convert.ToHexString(mz)});
                    }
                }
                int ntStatus=NtQueryInformationProcess(handle,0,out var basic,Marshal.SizeOf<BasicInfo>(),out _);
                bool pebRead=false;int pebError=0;ulong pebBytes=0;
                // A tiny read of the process loader descriptor distinguishes query access from memory access.
                if(ntStatus==0 && basic.PebBaseAddress!=0)
                {
                    var bytes=new byte[32];pebRead=ReadProcessMemory(handle,basic.PebBaseAddress,bytes,(nuint)bytes.Length,out nuint count);pebError=Marshal.GetLastWin32Error();pebBytes=(ulong)count;
                }
                results.Add(new {Stage="Access comparison", Access=$"0x{access:X}", Enumerated=enumerated, EnumError=enumError, ModuleBytesNeeded=needed,
                    NtStatus=$"0x{ntStatus:X8}", PebRead=pebRead, PebError=pebError, PebBytes=pebBytes, Matches=matches});
            }
            finally {CloseHandle(handle);}
        }
        nint snapshot=CreateToolhelp32Snapshot(0x8|0x10,(uint)process.Id);int snapError=Marshal.GetLastWin32Error();
        results.Add(new {Stage="Toolhelp module snapshot", Success=snapshot!=new nint(-1), Error=snapError});
        if(snapshot!=new nint(-1))CloseHandle(snapshot);
        return results;
    }
}

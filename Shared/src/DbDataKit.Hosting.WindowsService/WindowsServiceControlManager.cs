using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;

namespace DbDataKit.Hosting.WindowsService;

/// <summary>
/// Real SCM access through advapi32. Replaces <c>sc.exe</c>: the password goes straight from a
/// <see cref="SecureString"/> to unmanaged memory to the API (and is zeroed), so it is never on a command line.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsServiceControlManager : IServiceControlManager
{
    private const uint ScManagerConnect = 0x1;
    private const uint ScManagerCreateService = 0x2;
    private const uint ServiceQueryConfig = 0x1;
    private const uint ServiceChangeConfig = 0x2;
    private const uint ServiceQueryStatus = 0x4;
    private const uint ServiceStart = 0x10;
    private const uint ServiceStop = 0x20;
    private const uint ServiceAllAccess = 0xF01FF;
    private const uint DeleteAccess = 0x10000;
    private const uint ServiceWin32OwnProcess = 0x10;
    private const uint NoChange = 0xFFFFFFFF;

    private const uint InfoDescription = 1;
    private const uint InfoFailureActions = 2;
    private const uint InfoDelayedAutoStart = 3;
    private const uint InfoFailureActionsFlag = 4;
    private const uint InfoSidInfo = 5;
    private const uint InfoRequiredPrivileges = 6;

    private const int ScActionNone = 0;
    private const int ScActionRestart = 1;

    public ServiceConfigSnapshot? GetConfig(string name)
    {
        using var scm = OpenManager(ScManagerConnect);
        var h = OpenServiceW(scm.Handle, name, ServiceQueryConfig);
        if (h == IntPtr.Zero)
        {
            var err = Marshal.GetLastWin32Error();
            if (err == 1060) return null;
            throw Fail(err, "open", name);
        }

        using var svc = new ScHandle(h);
        var cfg = QueryBuffer((buf, size, out needed) => QueryServiceConfigW(h, buf, size, out needed), "query config", name);
        try
        {
            var raw = Marshal.PtrToStructure<QueryServiceConfig>(cfg)!;
            var delayed = TryQuery2(h, InfoDelayedAutoStart, p => Marshal.ReadInt32(p) != 0);
            var sid = TryQuery2(h, InfoSidInfo, p => (ServiceSidType)Marshal.ReadInt32(p));
            var desc = TryQuery2(h, InfoDescription, p => Marshal.PtrToStringUni(Marshal.ReadIntPtr(p)));
            var restarts = TryQuery2(h, InfoFailureActions, p =>
            {
                var fa = Marshal.PtrToStructure<ServiceFailureActions>(p);
                var count = 0;
                for (var i = 0; i < fa.cActions; i++)
                {
                    var a = Marshal.PtrToStructure<ScAction>(fa.lpsaActions + (i * Marshal.SizeOf<ScAction>()));
                    if (a.Type == ScActionRestart) count++;
                }

                return count;
            });

            var start = raw.dwStartType switch
            {
                2 => delayed ? ServiceStartMode.AutomaticDelayed : ServiceStartMode.Automatic,
                3 => ServiceStartMode.Manual,
                4 => ServiceStartMode.Disabled,
                _ => ServiceStartMode.Manual,
            };

            return new ServiceConfigSnapshot
            {
                Name = name,
                DisplayName = Marshal.PtrToStringUni(raw.lpDisplayName),
                Description = desc,
                BinaryPathName = Marshal.PtrToStringUni(raw.lpBinaryPathName) ?? "",
                StartMode = start,
                AccountName = Marshal.PtrToStringUni(raw.lpServiceStartName),
                Dependencies = ReadMultiSz(raw.lpDependencies),
                FailureRestartActions = restarts,
                SidType = sid,
            };
        }
        finally
        {
            Marshal.FreeHGlobal(cfg);
        }
    }

    public void Create(ServiceInstallSpec spec, SecureString? password)
    {
        using var scm = OpenManager(ScManagerCreateService);
        var pw = password is null ? IntPtr.Zero : Marshal.SecureStringToGlobalAllocUnicode(password);
        try
        {
            var h = CreateServiceW(
                scm.Handle,
                spec.Name,
                spec.DisplayName ?? spec.Name,
                ServiceAllAccess,
                ServiceWin32OwnProcess,
                StartType(spec.StartMode),
                1,
                spec.CommandLine,
                null,
                IntPtr.Zero,
                DependenciesMultiSz(spec.Dependencies),
                spec.EffectiveAccount.ScmName,
                pw);
            if (h == IntPtr.Zero)
            {
                throw Fail(Marshal.GetLastWin32Error(), "create", spec.Name);
            }

            using var svc = new ScHandle(h);
            ApplyExtendedSettings(h, spec);
        }
        finally
        {
            if (pw != IntPtr.Zero) Marshal.ZeroFreeGlobalAllocUnicode(pw);
        }
    }

    public void Reconfigure(ServiceInstallSpec spec, SecureString? password)
    {
        using var scm = OpenManager(ScManagerConnect);
        var h = OpenServiceW(scm.Handle, spec.Name, ServiceChangeConfig | ServiceQueryConfig);
        if (h == IntPtr.Zero) throw Fail(Marshal.GetLastWin32Error(), "open", spec.Name);
        using var svc = new ScHandle(h);

        var pw = password is null ? IntPtr.Zero : Marshal.SecureStringToGlobalAllocUnicode(password);
        try
        {
            // Account change only when we have what it needs; null start name leaves the account as is.
            var account = spec.EffectiveAccount;
            var ok = ChangeServiceConfigW(
                h,
                NoChange,
                StartType(spec.StartMode),
                NoChange,
                spec.CommandLine,
                null,
                IntPtr.Zero,
                DependenciesMultiSz(spec.Dependencies),
                account.Kind == ServiceAccountKind.LocalSystem ? "LocalSystem" : account.Name,
                pw,
                spec.DisplayName ?? spec.Name);
            if (!ok) throw Fail(Marshal.GetLastWin32Error(), "reconfigure", spec.Name);
            ApplyExtendedSettings(h, spec);
        }
        finally
        {
            if (pw != IntPtr.Zero) Marshal.ZeroFreeGlobalAllocUnicode(pw);
        }
    }

    public void Delete(string name)
    {
        using var scm = OpenManager(ScManagerConnect);
        var h = OpenServiceW(scm.Handle, name, DeleteAccess);
        if (h == IntPtr.Zero) throw Fail(Marshal.GetLastWin32Error(), "open", name);
        using var svc = new ScHandle(h);
        if (!DeleteService(h)) throw Fail(Marshal.GetLastWin32Error(), "delete", name);
    }

    public void Start(string name)
    {
        using var scm = OpenManager(ScManagerConnect);
        var h = OpenServiceW(scm.Handle, name, ServiceStart);
        if (h == IntPtr.Zero) throw Fail(Marshal.GetLastWin32Error(), "open", name);
        using var svc = new ScHandle(h);
        if (!StartServiceW(h, 0, IntPtr.Zero))
        {
            var err = Marshal.GetLastWin32Error();
            if (err != 1056) throw Fail(err, "start", name);
        }
    }

    public void Stop(string name)
    {
        using var scm = OpenManager(ScManagerConnect);
        var h = OpenServiceW(scm.Handle, name, ServiceStop);
        if (h == IntPtr.Zero) throw Fail(Marshal.GetLastWin32Error(), "open", name);
        using var svc = new ScHandle(h);
        if (!ControlService(h, 1, out _))
        {
            var err = Marshal.GetLastWin32Error();
            if (err != 1062) throw Fail(err, "stop", name);
        }
    }

    public ServiceStatus QueryStatus(string name)
    {
        using var scm = OpenManager(ScManagerConnect);
        var h = OpenServiceW(scm.Handle, name, ServiceQueryStatus);
        if (h == IntPtr.Zero) throw Fail(Marshal.GetLastWin32Error(), "open", name);
        using var svc = new ScHandle(h);
        if (!QueryServiceStatusEx(h, 0, out var s, Marshal.SizeOf<ServiceStatusProcess>(), out _))
        {
            throw Fail(Marshal.GetLastWin32Error(), "query status", name);
        }

        return new ServiceStatus((ServiceRunState)s.dwCurrentState, (int)s.dwProcessId, (int)s.dwWin32ExitCode);
    }

    // ---- helpers ----

    private static uint StartType(ServiceStartMode mode) => mode switch
    {
        ServiceStartMode.Automatic or ServiceStartMode.AutomaticDelayed => 2,
        ServiceStartMode.Manual => 3,
        _ => 4,
    };

    private static string DependenciesMultiSz(IReadOnlyList<string> deps) =>
        deps.Count == 0 ? "\0" : string.Join('\0', deps) + "\0";

    private static void ApplyExtendedSettings(IntPtr h, ServiceInstallSpec spec)
    {
        if (spec.Description is not null)
        {
            var p = Marshal.StringToHGlobalUni(spec.Description);
            try { Set2(h, InfoDescription, new ServiceDescription { lpDescription = p }, spec.Name); }
            finally { Marshal.FreeHGlobal(p); }
        }

        SetFailureActions(h, spec);
        Set2(h, InfoDelayedAutoStart, new ServiceDelayedAutoStart { fDelayedAutostart = spec.StartMode == ServiceStartMode.AutomaticDelayed ? 1 : 0 }, spec.Name);
        Set2(h, InfoSidInfo, new ServiceSidInfo { dwServiceSidType = (uint)spec.SidType }, spec.Name);

        if (spec.RequiredPrivileges is { } privs)
        {
            var multi = privs.Count == 0 ? "\0" : string.Join('\0', privs) + "\0";
            var p = Marshal.StringToHGlobalUni(multi + "\0");
            try { Set2(h, InfoRequiredPrivileges, new ServiceRequiredPrivileges { pmszRequiredPrivileges = p }, spec.Name); }
            finally { Marshal.FreeHGlobal(p); }
        }
    }

    private static void SetFailureActions(IntPtr h, ServiceInstallSpec spec)
    {
        var delays = spec.Failure.RestartDelays;
        var size = Marshal.SizeOf<ScAction>();
        var actions = Marshal.AllocHGlobal(Math.Max(1, delays.Count) * size);
        try
        {
            for (var i = 0; i < delays.Count; i++)
            {
                Marshal.StructureToPtr(new ScAction { Type = ScActionRestart, Delay = (uint)delays[i].TotalMilliseconds }, actions + (i * size), false);
            }

            if (delays.Count == 0)
            {
                Marshal.StructureToPtr(new ScAction { Type = ScActionNone, Delay = 0 }, actions, false);
            }

            Set2(h, InfoFailureActions, new ServiceFailureActions
            {
                dwResetPeriod = (uint)spec.Failure.ResetPeriod.TotalSeconds,
                cActions = (uint)Math.Max(1, delays.Count),
                lpsaActions = actions,
            }, spec.Name);
            Set2(h, InfoFailureActionsFlag, new ServiceDelayedAutoStart { fDelayedAutostart = spec.Failure.ApplyToNonCrashFailures ? 1 : 0 }, spec.Name);
        }
        finally
        {
            Marshal.FreeHGlobal(actions);
        }
    }

    private static void Set2<T>(IntPtr h, uint level, T info, string name) where T : struct
    {
        var p = Marshal.AllocHGlobal(Marshal.SizeOf<T>());
        try
        {
            Marshal.StructureToPtr(info, p, false);
            if (!ChangeServiceConfig2W(h, level, p))
            {
                throw Fail(Marshal.GetLastWin32Error(), $"configure (level {level})", name);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(p);
        }
    }

    private static T TryQuery2<T>(IntPtr h, uint level, Func<IntPtr, T> read)
    {
        IntPtr buf = IntPtr.Zero;
        try
        {
            buf = QueryBuffer((b, size, out needed) => QueryServiceConfig2W(h, level, b, size, out needed), $"query level {level}", "");
            return read(buf);
        }
        catch (ServiceControlException)
        {
            return default!;
        }
        finally
        {
            if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
        }
    }


    private delegate bool QueryCall(IntPtr buffer, int size, out int needed);

    private static IntPtr QueryBuffer(QueryCall call, string op, string name)
    {
        call(IntPtr.Zero, 0, out var needed);
        if (needed <= 0) needed = 8192;
        var buf = Marshal.AllocHGlobal(needed);
        if (!call(buf, needed, out _))
        {
            var err = Marshal.GetLastWin32Error();
            Marshal.FreeHGlobal(buf);
            throw Fail(err, op, name);
        }

        return buf;
    }

    private static List<string> ReadMultiSz(IntPtr p)
    {
        var list = new List<string>();
        if (p == IntPtr.Zero) return list;
        while (true)
        {
            var s = Marshal.PtrToStringUni(p);
            if (string.IsNullOrEmpty(s)) break;
            list.Add(s);
            p += (s.Length + 1) * 2;
        }

        return list;
    }

    private static ScHandle OpenManager(uint access)
    {
        var h = OpenSCManagerW(null, null, access);
        if (h == IntPtr.Zero) throw Fail(Marshal.GetLastWin32Error(), "open manager", "");
        return new ScHandle(h);
    }

    private static ServiceControlException Fail(int code, string op, string name) =>
        new(ServiceControlException.Map(code), code, op, name);

    private sealed class ScHandle(IntPtr handle) : IDisposable
    {
        public IntPtr Handle { get; } = handle;

        public void Dispose() => CloseServiceHandle(Handle);
    }

    // ---- native ----

    [StructLayout(LayoutKind.Sequential)]
    private struct QueryServiceConfig
    {
        public uint dwServiceType;
        public uint dwStartType;
        public uint dwErrorControl;
        public IntPtr lpBinaryPathName;
        public IntPtr lpLoadOrderGroup;
        public uint dwTagId;
        public IntPtr lpDependencies;
        public IntPtr lpServiceStartName;
        public IntPtr lpDisplayName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        public uint dwServiceType;
        public uint dwCurrentState;
        public uint dwControlsAccepted;
        public uint dwWin32ExitCode;
        public uint dwServiceSpecificExitCode;
        public uint dwCheckPoint;
        public uint dwWaitHint;
        public uint dwProcessId;
        public uint dwServiceFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusPlain
    {
        public uint dwServiceType;
        public uint dwCurrentState;
        public uint dwControlsAccepted;
        public uint dwWin32ExitCode;
        public uint dwServiceSpecificExitCode;
        public uint dwCheckPoint;
        public uint dwWaitHint;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ScAction
    {
        public int Type;
        public uint Delay;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceFailureActions
    {
        public uint dwResetPeriod;
        public IntPtr lpRebootMsg;
        public IntPtr lpCommand;
        public uint cActions;
        public IntPtr lpsaActions;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceDescription
    {
        public IntPtr lpDescription;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceDelayedAutoStart
    {
        public int fDelayedAutostart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceSidInfo
    {
        public uint dwServiceSidType;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceRequiredPrivileges
    {
        public IntPtr pmszRequiredPrivileges;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManagerW(string? machineName, string? databaseName, uint access);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenServiceW(IntPtr hScManager, string serviceName, uint access);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateServiceW(
        IntPtr hScManager, string serviceName, string displayName, uint desiredAccess, uint serviceType, uint startType,
        uint errorControl, string binaryPathName, string? loadOrderGroup, IntPtr tagId, string? dependencies,
        string? serviceStartName, IntPtr password);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeServiceConfigW(
        IntPtr hService, uint serviceType, uint startType, uint errorControl, string? binaryPathName, string? loadOrderGroup,
        IntPtr tagId, string? dependencies, string? serviceStartName, IntPtr password, string? displayName);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeServiceConfig2W(IntPtr hService, uint infoLevel, IntPtr info);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceConfigW(IntPtr hService, IntPtr buffer, int bufSize, out int bytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceConfig2W(IntPtr hService, uint infoLevel, IntPtr buffer, int bufSize, out int bytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(IntPtr hService, int infoLevel, out ServiceStatusProcess status, int bufSize, out int bytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartServiceW(IntPtr hService, uint numArgs, IntPtr args);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ControlService(IntPtr hService, uint control, out ServiceStatusPlain status);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteService(IntPtr hService);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);
}

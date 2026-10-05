using GHelper.Helpers;
using NvAPIWrapper.GPU;
using NvAPIWrapper.Native;
using NvAPIWrapper.Native.GPU;
using NvAPIWrapper.Native.GPU.Structures;
using NvAPIWrapper.Native.Interfaces.GPU;
using System.Diagnostics;
using static NvAPIWrapper.Native.GPU.Structures.PerformanceStates20InfoV1;

namespace GHelper.Gpu.NVidia;

public class NvidiaGpuControl : IGpuControl
{

    public static int MaxCoreOffset = AppConfig.Get("max_gpu_core", 250);
    public static int MaxMemoryOffset = AppConfig.Get("max_gpu_memory", 500);

    public static int MinCoreOffset = AppConfig.Get("min_gpu_core", -250);
    public static int MinMemoryOffset = AppConfig.Get("min_gpu_memory", -500);

    public static int MinVoltage = AppConfig.Get("min_gpu_voltage", 400);
    public static int MaxVoltage = AppConfig.Get("max_gpu_voltage", 1300);

    public static int LowVoltageWarnThreshold = AppConfig.Get("low_gpu_voltage_warn", 500);

    public static int MinClockLimit = AppConfig.Get("min_gpu_clock", 400);
    public const int MaxClockLimit = 3000;

    private static PhysicalGPU? _internalGpu;

    public NvidiaGpuControl()
    {
        InvalidateStockCache();
        _internalGpu = GetInternalDiscreteGpu();
        if (IsValid)
        {
            if (FullName.Contains("5080") || FullName.Contains("5090"))
            {
                MaxCoreOffset = AppConfig.Get("max_gpu_core", 400);
                MaxMemoryOffset = AppConfig.Get("max_gpu_memory", 1000);
                Logger.WriteLine($"NVIDIA GPU: {FullName} ({MaxCoreOffset},{MaxMemoryOffset})");
            }
            if (FullName.Contains("5070 Ti") || FullName.Contains("4080") || FullName.Contains("4090"))
            {
                MaxCoreOffset = AppConfig.Get("max_gpu_core", 300);
                Logger.WriteLine($"NVIDIA GPU: {FullName} ({MaxCoreOffset},{MaxMemoryOffset})");
            }
        }
    }

    public bool IsValid => _internalGpu != null;

    public bool IsNvidia => IsValid;

    public string FullName => _internalGpu!.FullName;

    public int? _lastTemp;
    public int _lastTempTime = 0;

    private static bool verboseLog = false;

    private enum GpuState { Active, Asleep, Off }

    private GpuState _lastState = GpuState.Off;
    private long _lastStateTime = -StateCacheMs;
    private const int StateCacheMs = 500; 

    private GpuState GetGpuState()
    {
        if (!IsValid) return GpuState.Off;
        if (Environment.TickCount64 - _lastStateTime < StateCacheMs) return _lastState;
        try
        {
            var perfState = GPUApi.GetCurrentPerformanceState(_internalGpu!.Handle);
            if (verboseLog) Logger.WriteLine($"GPU: {perfState}");
            _lastState = GpuState.Active;
        }
        catch (Exception ex)
        {
            if (verboseLog) Logger.WriteLine($"GPU: {ex.Message}");
            _lastState = ex.Message == "NVAPI_GPU_NOT_POWERED" ? GpuState.Asleep : GpuState.Off;
        }
        _lastStateTime = Environment.TickCount64;
        return _lastState;
    }

    public int? ReadCurrentTemperature(bool log = false)
    {
        if (!IsValid) return null;

        var thermalSettings = GPUApi.GetThermalSettings(_internalGpu!.Handle);
        if (thermalSettings.Sensors is null) return null;

        IThermalSensor? gpuSensor = thermalSettings.Sensors
            .FirstOrDefault(s => s.Target == ThermalSettingsTarget.GPU);

        if (log || verboseLog) Logger.WriteLine($"GPU Temp: {gpuSensor?.CurrentTemperature}C");
        return gpuSensor?.CurrentTemperature;
    }

    private Task<int?>? _readTask;

    public int? GetCurrentTemperature()
    {
        if (!IsValid) return null;

        var state = GetGpuState();
        if (state == GpuState.Off) return null;

        if ((_readTask?.IsCompleted ?? true) && (state == GpuState.Active || ShouldRefresh()))
        {
            _readTask = Task.Run(() =>
            {
                var temp = ReadCurrentTemperature();
                if (temp is not null)
                {
                    _lastTemp = temp;
                    _lastTempTime = Environment.TickCount;
                }
                return temp;
            });
        }

        _readTask?.Wait(500);

        return _lastTemp;
    }

    private bool ShouldRefresh()
    {
        const int minInterval = 5_000;
        const int maxInterval = 120_000;
        const float deltaMin = 5f;
        const float deltaMax = 20f;

        if (_lastTemp is null) return true;

        var cpuTemp = (float)HardwareControl.GetCPUTemp();
        var delta = _lastTemp.Value - cpuTemp;

        if (delta < deltaMin) return false;

        var t = Math.Clamp((delta - deltaMin) / (deltaMax - deltaMin), 0f, 1f);
        var interval = (int)(maxInterval - t * (maxInterval - minInterval));

        var refresh = Environment.TickCount > _lastTempTime + interval;
        if (verboseLog) Logger.WriteLine($"GPU Temp Refresh Interval: {interval}ms {refresh}");

        return refresh;
    }

    public void Dispose()
    {
        _internalGpu = null;
    }

    private static readonly HashSet<string> _systemProcessNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "dwm", "csrss", "winlogon", "services", "lsass", "smss", "wininit",
        "svchost", "fontdrvhost", "igfxem", "igfxhk", "igfxext",
        "nvcontainer", "nvdisplay.container", "nvsettings", "nvspcaps64",
        "nvsphelper64", "nvwmi64", "nvcplui", "atieclxx", "atiesrxx",
        "explorer", "taskhostw", "sihost", "runtimebroker", "shellexperiencehost",
        "searchhost", "startmenuexperiencehost", "textinputhost",
        "applicationframehost", "systemsettings", "dllhost", "conhost",
        "audiodg", "ctfloader", "spoolsv", "wlanext", "msdtc",
    };

    public void KillGPUApps()
    {
        if (!IsValid) return;
        PhysicalGPU internalGpu = _internalGpu!;

        int currentPid = Process.GetCurrentProcess().Id;

        try
        {
            Process[] processes = internalGpu.GetActiveApplications();
            foreach (Process process in processes)
                try
                {
                    if (process.Id == currentPid) continue;
                    if (process.SessionId == 0) continue;
                    if (_systemProcessNames.Contains(process.ProcessName)) continue;

                    Logger.WriteLine("Kill:" + process.ProcessName);
                    ProcessHelper.KillByProcess(process);
                }
                catch (Exception ex)
                {
                    Logger.WriteLine(ex.Message);
                }
        }
        catch (Exception ex)
        {
            Logger.WriteLine(ex.Message);
        }

        //GeneralApi.RestartDisplayDriver();
    }


    public bool GetClocks(out int core, out int memory)
    {
        PhysicalGPU internalGpu = _internalGpu!;

        //Logger.WriteLine(internalGpu.FullName);
        //Logger.WriteLine(internalGpu.ArchitectInformation.ToString());

        try
        {
            var temp = ReadCurrentTemperature(true); // Force wake up GPU for clock reading

            IPerformanceStates20Info states = GPUApi.GetPerformanceStates20(internalGpu.Handle);
            core = states.Clocks[PerformanceStateId.P0_3DPerformance][0].FrequencyDeltaInkHz.DeltaValue / 1000;
            memory = states.Clocks[PerformanceStateId.P0_3DPerformance][1].FrequencyDeltaInkHz.DeltaValue / 1000;
            Logger.WriteLine($"GET GPU CLOCKS: {core}, {memory}");

            foreach (var delta in states.Voltages[PerformanceStateId.P0_3DPerformance])
            {
                Logger.WriteLine("GPU VOLT:" + delta.IsEditable + " - " + delta.ValueDeltaInMicroVolt.DeltaValue);
            }

            return true;

        }
        catch (Exception ex)
        {
            Logger.WriteLine("GET GPU CLOCKS:" + ex.Message);
            core = memory = 0;
            return false;
        }

    }


    private static bool RunPowershellCommand(string script, int timeoutMs = 0)
    {
        try
        {
            ProcessHelper.RunCMD("powershell", script, timeoutMs: timeoutMs);
            return true;
        }
        catch (Exception ex)
        {
            Logger.WriteLine(ex.ToString());
            return false;
        }

    }

    public static int SnapVoltage(int mv) => (int)Math.Round(mv / 5f, MidpointRounding.AwayFromZero) * 5;

    private static int? _stockVoltage;
    private static string? _stockGpuName;

    public static void InvalidateStockCache()
    {
        _stockVoltage = null;
        _stockGpuName = null;
    }

    // Stock (factory) P0 core voltage in mV, snapped to 5. -1 when unreadable.
    // Only successful reads are cached, so a failed read (e.g. dGPU asleep)
    // is retried on the next call instead of sticking at -1 for the session.
    // The cache is tied to the GPU name, so a GPU/driver change re-reads it.
    public int GetStockVoltage()
    {
        string? gpuName = null;
        try { gpuName = _internalGpu?.FullName; } catch { }
        if (_stockVoltage is not null && _stockVoltage.Value > 0 && _stockGpuName == gpuName) return _stockVoltage.Value;
        if (_stockGpuName != gpuName) _stockVoltage = null;

        int stock = -1;
        try
        {
            ReadCurrentTemperature(true); // wake GPU for P-state reading, like GetClocks
            IPerformanceStates20Info states = GPUApi.GetPerformanceStates20(_internalGpu!.Handle);
            var volts = states.Voltages[PerformanceStateId.P0_3DPerformance];
            foreach (var v in volts)
            {
                if (v.ValueInMicroVolt > 0 && (stock < 0 || v.IsEditable))
                {
                    stock = SnapVoltage((int)(v.ValueInMicroVolt / 1000));
                    if (v.IsEditable) break;
                }
            }
            Logger.WriteLine("GET GPU STOCK VOLTAGE: " + stock);
        }
        catch (Exception ex)
        {
            Logger.WriteLine("GET GPU STOCK VOLTAGE: " + ex.Message);
        }

        if (stock > 0)
        {
            _stockVoltage = stock;
            _stockGpuName = gpuName;
        }
        else
        {
            _stockVoltage = null;
        }
        return stock;
    }

    // Live voltage right now in mV, snapped to 5. -1 when unreadable.
    public int GetLiveVoltage()
    {
        try
        {
            int mv = (int)(GPUApi.GetCurrentVoltage(_internalGpu!.Handle).ValueInMicroVolt / 1000);
            return mv > 0 ? SnapVoltage(mv) : -1;
        }
        catch (Exception ex)
        {
            Logger.WriteLine("GET GPU LIVE VOLTAGE: " + ex.Message);
            return -1;
        }
    }

    // Current voltage lock straight from the driver: (locked, mV).
    // (false, -1) when unlocked, when the read fails (e.g. missing privilege),
    // or when the value is outside the voltage range (a clock cap mirrors into
    // this same struct with MHz-scale values, and unlocked it reports live voltage).
    // LockMode is the authority, not the value.
    public (bool locked, int mv) GetVoltageLock()
    {
        try
        {
            var data = GPUApi.GetClockBoostLock(_internalGpu!.Handle);
            var locks = data.ClockBoostLocks;
            if (locks is null || locks.Length == 0) return (false, -1);
            if (!locks.Any(l => l.ClockDomain == PublicClockDomain.Graphics)) return (false, -1);
            var entry = locks.First(l => l.ClockDomain == PublicClockDomain.Graphics);
            if (entry.LockMode != ClockLockMode.Manual) return (false, -1);
            int mv = SnapVoltage((int)(entry.VoltageInMicroV / 1000));
            if (mv < MinVoltage || mv > MaxVoltage) return (false, -1);
            Logger.WriteLine("GET GPU VOLTAGE LOCK: " + mv);
            return (true, mv);
        }
        catch (Exception ex)
        {
            Logger.WriteLine("GET GPU VOLTAGE LOCK: " + ex.Message);
            return (false, -1);
        }
    }

    // NOTE: the driver's boost-lock struct is shared between the clock cap (nvidia-smi -lgc)
    // and the voltage lock: a cap at or below the voltage max is indistinguishable from a
    // voltage lock of the same number, and unlocked it reports live voltage. So the clock
    // slider never reads it back, and the voltage slider only trusts a read-back value that
    // is in range, in Manual mode, and doesn't match the configured clock cap (see InitGPU).
    private static string? _lastLimitCmd; // last nvidia-smi clock command, null = unknown
    private static int _lastVoltage = -1; // last applied voltage lock in mV, 0 = unlocked, -1 = unknown

    // Return codes: 1 = applied, 0 = noop, -1 = failed, -2 = needs admin (permission).
    // Only -2 should trigger a UAC relaunch; -1 is logged and ignored.
    public const int NeedsAdmin = -2;

    private static bool IsPermissionError(Exception ex)
    {
        string msg = ex.Message ?? "";
        if (msg.Contains("access", StringComparison.OrdinalIgnoreCase)) return true;
        if (msg.Contains("privilege", StringComparison.OrdinalIgnoreCase)) return true;
        if (msg.Contains("permission", StringComparison.OrdinalIgnoreCase)) return true;
        if (msg.Contains("admin", StringComparison.OrdinalIgnoreCase)) return true;
        if (msg.Contains("NOT_SUPPORTED", StringComparison.OrdinalIgnoreCase)) return true;
        if (msg.Contains("NVAPI_NO_PERMISSION", StringComparison.OrdinalIgnoreCase)) return true;
        const int E_ACCESSDENIED = unchecked((int)0x80070005);
        if (ex.HResult == E_ACCESSDENIED) return true;
        return false;
    }

    private static int ToNeedsAdminOrFail(Exception ex)
    {
        Logger.WriteLine("GPU NVAPI: " + ex.Message);
        if (ProcessHelper.IsUserAdministrator()) return -1;
        return IsPermissionError(ex) ? NeedsAdmin : -1;
    }

    public int SetMaxGPUClock(int clock)
    {

        if (clock < MinClockLimit || clock >= MaxClockLimit) clock = 0;

        string cmd = clock > 0 ? $"nvidia-smi -lgc 0,{clock}" : $"nvidia-smi -rgc";

        if (_lastLimitCmd == cmd) return 0;
        _lastLimitCmd = cmd;

        Logger.WriteLine("GPU LIMIT: " + cmd);
        RunPowershellCommand(cmd);
        return 1;


    }


    public int SetVoltage(int voltage, bool force = false)
    {

        int stock = GetStockVoltage();

        // Stock selection (or out of range) means true default: unlock, don't pin a value.
        if ((stock > 0 && voltage == stock) || voltage < MinVoltage || voltage > MaxVoltage) voltage = 0;

        if (!force && _lastVoltage == voltage) return 0;
        if (voltage > 1000) Logger.WriteLine($"SET GPU VOLTAGE: {voltage} mV is above the commonly tested 500-1000 mV range (slider max {MaxVoltage} mV), proceed with caution!");
        if (voltage > 0 && voltage < LowVoltageWarnThreshold) Logger.WriteLine($"SET GPU VOLTAGE: {voltage} mV is below the commonly tested 500-1000 mV range and may be unstable on some silicon, stress-test before keeping it!");

        PhysicalGPU internalGpu = _internalGpu!;
        try
        {
            PrivateClockBoostLockV2 data = voltage > 0
                ? new PrivateClockBoostLockV2(new[] { new PrivateClockBoostLockV2.ClockBoostLock(PublicClockDomain.Graphics, ClockLockMode.Manual, (uint)(voltage * 1000)) })
                : new PrivateClockBoostLockV2(new[] { new PrivateClockBoostLockV2.ClockBoostLock(PublicClockDomain.Graphics, ClockLockMode.None, 0) });

            Logger.WriteLine($"SET GPU VOLTAGE: {voltage}");
            GPUApi.SetClockBoostLock(internalGpu.Handle, data);

            if (voltage == 0 && _lastVoltage != 0)
            {
                // Stale driver locks (e.g. from nvidia-smi caps mirroring into the boost-lock
                // struct) are not always cleared by the NVAPI unlock: reset locked clocks too.
                // Runs before any clock cap is (re-)applied by the caller.
                RunPowershellCommand($"nvidia-smi -rgc");
                _lastLimitCmd = $"nvidia-smi -rgc";
            }
        }
        catch (Exception ex)
        {
            Logger.WriteLine("SET GPU VOLTAGE: " + ex.Message);
            return ToNeedsAdminOrFail(ex);
        }

        _lastVoltage = voltage;
        return 1;

    }

    public static void RestartNvContainer()
    {
        if (!ProcessHelper.IsUserAdministrator()) return;
        RunPowershellCommand(@"Restart-Service -Name 'NvContainerLocalSystem' -Force", 30000);
    }

    public static void RestartNVService()
    {
        if (!ProcessHelper.IsUserAdministrator()) return;
        RunPowershellCommand(@"Restart-Service -Name 'NVDisplay.ContainerLocalSystem' -Force", 30000);
        RunPowershellCommand(@"Restart-Service -Name 'NvContainerLocalSystem' -Force", 30000);
    }

    public static void StopNVService()
    {
        if (!ProcessHelper.IsUserAdministrator()) return;
        RunPowershellCommand(@"Stop-Service -Name 'NvContainerLocalSystem' -Force", 30000);
        RunPowershellCommand(@"Stop-Service -Name 'NVDisplay.ContainerLocalSystem' -Force", 30000);
    }

    public int SetClocks(int core, int memory)
    {

        if (core < MinCoreOffset || core > MaxCoreOffset) return 0;
        if (memory < MinMemoryOffset || memory > MaxMemoryOffset) return 0;

        GetClocks(out int currentCore, out int currentMemory);

        // Nothing to set
        if (Math.Abs(core - currentCore) < 5 && Math.Abs(memory - currentMemory) < 5) return 0;

        PhysicalGPU internalGpu = _internalGpu!;

        var coreClock = new PerformanceStates20ClockEntryV1(PublicClockDomain.Graphics, new PerformanceStates20ParameterDelta(core * 1000));
        var memoryClock = new PerformanceStates20ClockEntryV1(PublicClockDomain.Memory, new PerformanceStates20ParameterDelta(memory * 1000));
        //var voltageEntry = new PerformanceStates20BaseVoltageEntryV1(PerformanceVoltageDomain.Core, new PerformanceStates20ParameterDelta(voltage));

        PerformanceStates20ClockEntryV1[] clocks = { coreClock, memoryClock };
        PerformanceStates20BaseVoltageEntryV1[] voltages = { };

        PerformanceState20[] performanceStates = { new PerformanceState20(PerformanceStateId.P0_3DPerformance, clocks, voltages) };

        var overclock = new PerformanceStates20InfoV1(performanceStates, 2, 0);

        try
        {
            Logger.WriteLine($"SET GPU CLOCKS: {core}, {memory}");
            GPUApi.SetPerformanceStates20(internalGpu.Handle, overclock);
        }
        catch (Exception ex)
        {
            Logger.WriteLine("SET GPU CLOCKS: " + ex.Message);
            return ToNeedsAdminOrFail(ex);
        }

        return 1;
    }

    private static PhysicalGPU? GetInternalDiscreteGpu()
    {
        try
        {
            return PhysicalGPU
                .GetPhysicalGPUs()
                .FirstOrDefault(gpu => gpu.SystemType == SystemType.Laptop);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex.Message);
            return null;
        }
    }


    public int? GetGpuUse()
    {
        if (!IsValid) return null;
         if (GetGpuState() != GpuState.Active) return null;

        PhysicalGPU internalGpu = _internalGpu!;
        IUtilizationDomainInfo? gpuUsage = GPUApi.GetUsages(internalGpu.Handle).GPU;

        return (int?)gpuUsage?.Percentage;

    }


    public float? GetGpuPower()
    {
        if (!IsValid) return null;
        var state = GetGpuState();
        if (state == GpuState.Off)
        {
            NvmlHelper.Shutdown();
            return null;
        }
        if (state != GpuState.Active) return 0f;
        return NvmlHelper.GetGpuPower() ?? 0f;
    }

    public (long usedMb, long totalMb)? GetVramInfo()
    {
        if (!IsValid) return null;
        if (GetGpuState() != GpuState.Active) return null;
        return NvmlHelper.GetMemoryInfo();
    }

}

using GHelper.Helpers;
using System.Runtime.InteropServices;
using static GHelper.Gpu.AMD.Adl2.NativeMethods;

namespace GHelper.Gpu.AMD;

// Reference: https://github.com/GPUOpen-LibrariesAndSDKs/display-library/blob/master/Sample-Managed/Program.cs
public class AmdGpuControl : IGpuControl
{
    private bool _isReady;
    private nint _adlContextHandle;

    private readonly ADLAdapterInfo _internalDiscreteAdapter;
    private readonly ADLAdapterInfo? _iGPU;
    private const int SceUnsupported = -1;
    private int? _sceDisplayIndex;

    public bool IsNvidia => false;

    public string FullName => _internalDiscreteAdapter!.AdapterName;

    private ADLAdapterInfo? FindByType(ADLAsicFamilyType type = ADLAsicFamilyType.Discrete)
    {
        ADL2_Adapter_NumberOfAdapters_Get(_adlContextHandle, out int numberOfAdapters);
        if (numberOfAdapters <= 0)
            return null;

        ADLAdapterInfoArray osAdapterInfoData = new();
        int osAdapterInfoDataSize = Marshal.SizeOf(osAdapterInfoData);
        nint AdapterBuffer = Marshal.AllocCoTaskMem(osAdapterInfoDataSize);
        try
        {
            Marshal.StructureToPtr(osAdapterInfoData, AdapterBuffer, false);
            if (ADL2_Adapter_AdapterInfo_Get(_adlContextHandle, AdapterBuffer, osAdapterInfoDataSize) != Adl2.ADL_SUCCESS)
                return null;

            osAdapterInfoData = (ADLAdapterInfoArray)Marshal.PtrToStructure(AdapterBuffer, osAdapterInfoData.GetType())!;
        }
        finally
        {
            Marshal.FreeCoTaskMem(AdapterBuffer);
        }

        const int amdVendorId = 1002;

        // Determine which GPU is internal discrete AMD GPU
        ADLAdapterInfo internalDiscreteAdapter =
            osAdapterInfoData.ADLAdapterInfo
                .FirstOrDefault(adapter =>
                {
                    if (adapter.Exist == 0 || adapter.Present == 0)
                        return false;

                    if (adapter.VendorID != amdVendorId)
                        return false;

                    if (ADL2_Adapter_ASICFamilyType_Get(_adlContextHandle, adapter.AdapterIndex, out ADLAsicFamilyType asicFamilyType, out int asicFamilyTypeValids) != Adl2.ADL_SUCCESS)
                        return false;

                    asicFamilyType = (ADLAsicFamilyType)((int)asicFamilyType & asicFamilyTypeValids);

                    return (asicFamilyType & type) != 0;
                });

        if (internalDiscreteAdapter.Exist == 0)
            return null;

        return internalDiscreteAdapter;

    }

    public AmdGpuControl()
    {
        if ((AppConfig.NoGpu() && !AppConfig.IsAMDiGPU()) || !Adl2.Load()) return;

        try
        {
            if (Adl2.ADL2_Main_Control_Create(1, out _adlContextHandle) != Adl2.ADL_SUCCESS) return;
        } catch (Exception ex)
        {
            Logger.WriteLine(ex.Message);
            return;
        }

        ADLAdapterInfo? internalDiscreteAdapter = FindByType(ADLAsicFamilyType.Discrete)
            ?? FindByType(ADLAsicFamilyType.Integrated);

        if (internalDiscreteAdapter is not null)
        {
            _internalDiscreteAdapter = (ADLAdapterInfo)internalDiscreteAdapter;
            _isReady = true;
        }

        _iGPU = FindByType(ADLAsicFamilyType.Integrated);

    }

    public bool IsValid => _isReady && _adlContextHandle != nint.Zero;

    public int? GetCurrentTemperature()
    {
        if (!IsValid)
            return null;

        if (!GetPMLog(out ADLPMLogDataOutput adlpmLogDataOutput))
            return GetLegacy().temp;

        ADLSingleSensorData temperatureSensor = adlpmLogDataOutput.Sensors[(int)ADLSensorType.PMLOG_TEMPERATURE_EDGE];
        if (temperatureSensor.Supported == 0)
            return GetLegacy().temp;

        return temperatureSensor.Value;
    }


    private ADLPMLogDataOutput _pmLog;
    private bool _pmLogValid;
    private long _pmLogTime = -PMLogCacheMs;
    private const int PMLogCacheMs = 500;

    private bool GetPMLog(out ADLPMLogDataOutput log)
    {
        if (Environment.TickCount64 - _pmLogTime >= PMLogCacheMs)
        {
            _pmLogValid = ADL2_New_QueryPMLogData_Get(_adlContextHandle, _internalDiscreteAdapter.AdapterIndex, out _pmLog) == Adl2.ADL_SUCCESS;
            _pmLogTime = Environment.TickCount64;
        }
        log = _pmLog;
        return _pmLogValid;
    }

    public int? GetGpuUse()
    {
        if (!IsValid) return null;

        if (!GetPMLog(out ADLPMLogDataOutput adlpmLogDataOutput))
            return GetLegacy().use;

        ADLSingleSensorData gpuUsage = adlpmLogDataOutput.Sensors[(int)ADLSensorType.PMLOG_INFO_ACTIVITY_GFX];
        if (gpuUsage.Supported == 0)
            return GetLegacy().use;

        return gpuUsage.Value;
    }

    private long _totalVramMB; // total VRAM is static — cached on first successful query (0 = not yet)

    public (long usedMb, long totalMb)? GetVramInfo()
    {
        ADLAdapterInfo? adapter = IsValid ? _internalDiscreteAdapter : _iGPU;
        if (adapter is null) return null;
        int index = adapter.Value.AdapterIndex;

        if (_totalVramMB <= 0)
        {
            if (ADL2_Adapter_MemoryInfo2_Get(_adlContextHandle, index, out ADLMemoryInfo2 mem) != Adl2.ADL_SUCCESS)
                return null;
            _totalVramMB = mem.iMemorySize / (1024 * 1024);
            if (_totalVramMB <= 0) return null;
        }

        if (ADL2_Adapter_DedicatedVRAMUsage_Get(_adlContextHandle, index, out int usedMB) != Adl2.ADL_SUCCESS)
            return null;

        return (usedMB, _totalVramMB);
    }

    public int? GetiGpuUse()
    {
        if (_adlContextHandle == nint.Zero || _iGPU == null) return null;
        if (ADL2_New_QueryPMLogData_Get(_adlContextHandle, ((ADLAdapterInfo)_iGPU).AdapterIndex, out ADLPMLogDataOutput adlpmLogDataOutput) != Adl2.ADL_SUCCESS) return null;

        ADLSingleSensorData gpuUsage = adlpmLogDataOutput.Sensors[(int)ADLSensorType.PMLOG_INFO_ACTIVITY_GFX];
        if (gpuUsage.Supported == 0) return null;

        return gpuUsage.Value;

    }

    private ADLPMLogDataOutput _pmLogiGpu;
    private bool _pmLogiGpuValid;
    private long _pmLogiGpuTime = -PMLogCacheMs;

    private bool GetPMLogiGpu(out ADLPMLogDataOutput log)
    {
        log = default;
        if (_adlContextHandle == nint.Zero || _iGPU == null) return false;
        if (Environment.TickCount64 - _pmLogiGpuTime >= PMLogCacheMs)
        {
            _pmLogiGpuValid = ADL2_New_QueryPMLogData_Get(_adlContextHandle, ((ADLAdapterInfo)_iGPU).AdapterIndex, out _pmLogiGpu) == Adl2.ADL_SUCCESS;
            _pmLogiGpuTime = Environment.TickCount64;
        }
        log = _pmLogiGpu;
        return _pmLogiGpuValid;
    }

    private static int? Sensor(ADLPMLogDataOutput log, ADLSensorType type)
    {
        ADLSingleSensorData sensor = log.Sensors[(int)type];
        return sensor.Supported != 0 ? sensor.Value : null;
    }

    public (int? temp, int? use, int? gfxPower, int? cpuPower, int? asicPower) GetiGpuSensors()
    {
        if (!GetPMLogiGpu(out ADLPMLogDataOutput log)) return default;

        return (Sensor(log, ADLSensorType.PMLOG_TEMPERATURE_EDGE)
                ?? Sensor(log, ADLSensorType.PMLOG_TEMPERATURE_GFX)
                ?? Sensor(log, ADLSensorType.PMLOG_TEMPERATURE_SOC),
                Sensor(log, ADLSensorType.PMLOG_INFO_ACTIVITY_GFX),
                Sensor(log, ADLSensorType.PMLOG_GFX_POWER),
                Sensor(log, ADLSensorType.PMLOG_CPU_POWER),
                Sensor(log, ADLSensorType.PMLOG_ASIC_POWER));
    }

    public float? GetGpuPower()
    {
        if (!IsValid) return null;
        if (!GetPMLog(out ADLPMLogDataOutput adlpmLogDataOutput)) return GetLegacy().power;

        foreach (var sensorType in new[] { ADLSensorType.PMLOG_ASIC_POWER, ADLSensorType.PMLOG_GFX_POWER, ADLSensorType.PMLOG_BOARD_POWER })
        {
            ADLSingleSensorData sensor = adlpmLogDataOutput.Sensors[(int)sensorType];
            if (sensor.Supported != 0 && sensor.Value > 0)
                return sensor.Value;
        }

        return GetLegacy().power;
    }

    private bool? _oldGpu;
    private (int? temp, int? use, float? power) _legacy;
    private long _legacyTick = -PMLogCacheMs;

    private (int? temp, int? use, float? power) GetLegacy()
    {
        _oldGpu ??= ADL2_Overdrive_Caps(_adlContextHandle, _internalDiscreteAdapter.AdapterIndex, out _, out _, out int v) == Adl2.ADL_SUCCESS && v < 8;
        if (_oldGpu == false) return default;
        if (Environment.TickCount64 - _legacyTick < PMLogCacheMs) return _legacy;

        int idx = _internalDiscreteAdapter.AdapterIndex;
        (int? temp, int? use, float? power) data = default;
        try
        {
            if (ADL2_OverdriveN_Temperature_Get(_adlContextHandle, idx, 1, out int t) == Adl2.ADL_SUCCESS)
            {
                if (t > 1000) t /= 1000;
                if (t > 0 && t < 125) data.temp = t;
            }
            if (ADL2_OverdriveN_PerformanceStatus_Get(_adlContextHandle, idx, out ADLODNPerformanceStatus st) == Adl2.ADL_SUCCESS
                && st.iGPUActivityPercent >= 0 && st.iGPUActivityPercent <= 100)
                data.use = st.iGPUActivityPercent;
            if (ADL2_Overdrive6_CurrentPower_Get(_adlContextHandle, idx, 0, out int p) == Adl2.ADL_SUCCESS)
            {
                float watts = p / 256f;
                if (watts > 0 && watts < 1000) data.power = watts;
            }
        }
        catch (EntryPointNotFoundException) { _oldGpu = false; }

        _legacyTick = Environment.TickCount64;
        return _legacy = data;
    }

    // Used by ROG Ally (iGPU-only) for auto-TDP logic - queries the integrated GPU adapter
    public int GetiGpuPower()
    {
        if (_adlContextHandle == nint.Zero || _iGPU == null) return 0;
        if (ADL2_New_QueryPMLogData_Get(_adlContextHandle, ((ADLAdapterInfo)_iGPU).AdapterIndex, out ADLPMLogDataOutput adlpmLogDataOutput) != Adl2.ADL_SUCCESS) return 0;

        ADLSingleSensorData gpuUsage = adlpmLogDataOutput.Sensors[(int)ADLSensorType.PMLOG_ASIC_POWER];
        if (gpuUsage.Supported == 0) return 0;

        return gpuUsage.Value;
    }


    public bool SetVariBright(int enabled)
    {
        if (_adlContextHandle == nint.Zero) return false;

        ADLAdapterInfo? iGPU = FindByType(ADLAsicFamilyType.Integrated);
        if (iGPU is null) return false;

        return ADL2_Adapter_VariBrightEnable_Set(_adlContextHandle, ((ADLAdapterInfo)iGPU).AdapterIndex, enabled) == Adl2.ADL_SUCCESS;

    }

    public bool GetVariBright(out int supported, out int enabled)
    {
        supported = enabled = -1;

        if (_adlContextHandle == nint.Zero) return false;

        ADLAdapterInfo? iGPU = FindByType(ADLAsicFamilyType.Integrated);
        if (iGPU is null) return false;

        if (ADL2_Adapter_VariBright_Caps(_adlContextHandle, ((ADLAdapterInfo)iGPU).AdapterIndex, out int supportedOut, out int enabledOut, out int version) != Adl2.ADL_SUCCESS)
            return false;

        supported = supportedOut;
        enabled = enabledOut;

        return true;
    }

    private int? FindSceDisplay(int adapterIndex)
    {
        if (ADL2_Display_DisplayInfo_Get(_adlContextHandle, adapterIndex, out int count, out nint buffer, 0) != Adl2.ADL_SUCCESS) return null;

        try
        {
            int size = Marshal.SizeOf<ADLDisplayInfo>();
            for (int i = 0; i < count; i++)
            {
                var display = Marshal.PtrToStructure<ADLDisplayInfo>(buffer + i * size);
                if ((display.DisplayInfoValue & 1) == 0) continue;
                if (ADL2_Display_SCE_State_Get(_adlContextHandle, adapterIndex, display.DisplayID.DisplayLogicalIndex, out _, out _) == Adl2.ADL_SUCCESS) return display.DisplayID.DisplayLogicalIndex;
            }
        }
        finally
        {
            Adl2.FreeMemory(buffer);
        }

        return SceUnsupported;
    }

    public bool GetOledPowerOptimization()
    {
        if (_adlContextHandle == nint.Zero || _iGPU is null) return false;

        int adapterIndex = ((ADLAdapterInfo)_iGPU).AdapterIndex;

        try
        {
            _sceDisplayIndex ??= FindSceDisplay(adapterIndex);
            if (_sceDisplayIndex is null or SceUnsupported) return false;
            if (ADL2_Display_SCE_State_Get(_adlContextHandle, adapterIndex, (int)_sceDisplayIndex, out int state, out _) != Adl2.ADL_SUCCESS) return false;
            return state > 1;
        }
        catch
        {
            _sceDisplayIndex = SceUnsupported;
            Logger.WriteLine("No AMD OLED Power Optimization support");
            return false;
        }
    }

    public bool DisableOledPowerOptimization()
    {
        if (_sceDisplayIndex is null or SceUnsupported) return false;

        try
        {
            return ADL2_Display_SCE_State_Set(_adlContextHandle, ((ADLAdapterInfo)_iGPU).AdapterIndex, (int)_sceDisplayIndex, 1) == Adl2.ADL_SUCCESS;
        }
        catch
        {
            Logger.WriteLine("Can't disable AMD OLED Power Optimization");
            return false;
        }
    }

    public void StartFPS()
    {
        if (_adlContextHandle == nint.Zero || _iGPU == null) return;
        ADL2_Adapter_FrameMetrics_Start(_adlContextHandle, ((ADLAdapterInfo)_iGPU).AdapterIndex, 0);
    }

    public void StopFPS()
    {
        if (_adlContextHandle == nint.Zero || _iGPU == null) return;
        ADL2_Adapter_FrameMetrics_Stop(_adlContextHandle, ((ADLAdapterInfo)_iGPU).AdapterIndex, 0);
    }

    public float GetFPS()
    {
        if (_adlContextHandle == nint.Zero || _iGPU == null) return 0;
        float fps;
        if (ADL2_Adapter_FrameMetrics_Get(_adlContextHandle, ((ADLAdapterInfo)_iGPU).AdapterIndex, 0, out fps) != Adl2.ADL_SUCCESS) return 0;
        return fps;
    }

    public int GetFPSLimit()
    {
        if (_adlContextHandle == nint.Zero || _iGPU == null) return -1;
        ADLFPSSettingsOutput settings;
        if (ADL2_FPS_Settings_Get(_adlContextHandle, ((ADLAdapterInfo)_iGPU).AdapterIndex, out settings) != Adl2.ADL_SUCCESS) return -1;

        Logger.WriteLine($"FPS Limit: {settings.ulACFPSCurrent}");

        return settings.ulACFPSCurrent;
    }

    public int SetFPSLimit(int limit)
    {
        if (_adlContextHandle == nint.Zero || _iGPU == null) return -1;

        ADLFPSSettingsInput settings = new ADLFPSSettingsInput();

        settings.ulACFPSCurrent = limit;
        settings.ulDCFPSCurrent = limit;
        settings.bGlobalSettings = 1;

        if (ADL2_FPS_Settings_Set(_adlContextHandle, ((ADLAdapterInfo)_iGPU).AdapterIndex, settings) != Adl2.ADL_SUCCESS) return 0;

        return 1;
    }

    private int GetDisplayAdapterIndex()
    {
        return _iGPU is not null ? ((ADLAdapterInfo)_iGPU).AdapterIndex : _internalDiscreteAdapter.AdapterIndex;
    }

    // All AMD adapter indices that can own the internal panel. iGPU first
    // (hybrid mode), then discrete (MUX Ultimate / external on dGPU).
    private int[] GetFreeSyncAdapterIndices()
    {
        if (!IsValid) return Array.Empty<int>();
        int preferred = GetDisplayAdapterIndex();
        int discrete = _internalDiscreteAdapter.AdapterIndex;
        if (discrete == preferred) return new[] { preferred };
        return new[] { preferred, discrete };
    }

    private List<int> GetFreeSyncCandidateIndices(int adapterIndex, int numDisplays)
    {
        var list = new List<int>();
        for (int i = 0; i < numDisplays; i++) list.Add(i);

        // ADL display indices are not always 0..N-1 for eDP/internal panels
        // (MUX, disconnected entries). Also probe logical indices from DisplayInfo.
        try
        {
            if (ADL2_Display_DisplayInfo_Get(_adlContextHandle, adapterIndex, out int count, out nint buffer, 0) == Adl2.ADL_SUCCESS)
            {
                try
                {
                    int size = Marshal.SizeOf<ADLDisplayInfo>();
                    for (int i = 0; i < count; i++)
                    {
                        var info = Marshal.PtrToStructure<ADLDisplayInfo>(buffer + i * size);
                        int logical = info.DisplayID.DisplayLogicalIndex;
                        if (!list.Contains(logical)) list.Add(logical);
                    }
                }
                finally
                {
                    Adl2.FreeMemory(buffer);
                }
            }
        }
        catch { }
        return list;
    }

    private bool TryGetDisplayOnAdapter(int adapterIndex, out int displayIndex, out int onCurrent, out int onDefault, out int onMin, out int onMax)
    {
        displayIndex = -1;
        onCurrent = onDefault = onMin = onMax = -1;

        int firstCapable = -1;
        int firstCurrent = -1, firstDefault = -1, firstMin = -1, firstMax = -1;
        int onDisplay = -1;
        int onCur = -1, onDef = -1, onMi = -1, onMa = -1;

        if (Adl2.NativeMethods.ADL2_Display_NumberOfDisplays_Get(_adlContextHandle, adapterIndex, out int numDisplays) != Adl2.ADL_SUCCESS)
        {
            Logger.WriteLine($"FreeSync probe: adapter {adapterIndex} numDisplays failed");
            return false;
        }

        foreach (int i in GetFreeSyncCandidateIndices(adapterIndex, numDisplays))
        {
            if (Adl2.NativeMethods.ADL2_Display_FreeSync_Cap(_adlContextHandle, adapterIndex, i, out ADLFreeSyncCap caps) != Adl2.ADL_SUCCESS)
                continue;

            if (Adl2.NativeMethods.ADL2_Display_FreeSyncState_Get(
                _adlContextHandle,
                adapterIndex,
                i,
                out int current,
                out int def,
                out int min,
                out int max) != Adl2.ADL_SUCCESS)
            {
                continue;
            }

            // caps.iCaps == 0 means no FreeSync support at all (ADL_FREESYNC_CAP_*).
            // (No per-probe logging here: VisualiseScreen polls this often.)

            if (caps.iCaps == 0)
                continue;

            if (firstCapable < 0)
            {
                firstCapable = i;
                firstCurrent = current; firstDefault = def; firstMin = min; firstMax = max;
            }

            // Prefer a display that is currently ON so we agree with Adrenalin
            // when several capable displays exist (internal + external).
            if (onDisplay < 0 && IsFreeSyncOn(current))
            {
                onDisplay = i;
                onCur = current; onDef = def; onMi = min; onMa = max;
            }
        }

        if (onDisplay >= 0)
        {
            displayIndex = onDisplay;
            onCurrent = onCur; onDefault = onDef; onMin = onMi; onMax = onMa;
            return true;
        }

        if (firstCapable >= 0)
        {
            displayIndex = firstCapable;
            onCurrent = firstCurrent; onDefault = firstDefault; onMin = firstMin; onMax = firstMax;
            return true;
        }

        return false;
    }

    private bool TryGetFreeSyncDisplay(out int adapterIndex, out int displayIndex, out int current, out int defaultState, out int minRefreshRateInMicroHz, out int maxRefreshRateInMicroHz)
    {
        adapterIndex = displayIndex = -1;
        current = defaultState = minRefreshRateInMicroHz = maxRefreshRateInMicroHz = -1;
        if (!IsValid) return false;

        // Fallback when nothing is ON: first capable display found.
        int fbAdapter = -1, fbDisplay = -1, fbCur = -1, fbDef = -1, fbMin = -1, fbMax = -1;

        foreach (int idx in GetFreeSyncAdapterIndices())
        {
            if (TryGetDisplayOnAdapter(idx, out int d, out int cur, out int def, out int min, out int max))
            {
                if (IsFreeSyncOn(cur))
                {
                    adapterIndex = idx; displayIndex = d;
                    current = cur; defaultState = def; minRefreshRateInMicroHz = min; maxRefreshRateInMicroHz = max;
                    return true;
                }
                if (fbAdapter < 0)
                {
                    fbAdapter = idx; fbDisplay = d; fbCur = cur; fbDef = def; fbMin = min; fbMax = max;
                }
            }
        }

        if (fbAdapter >= 0)
        {
            adapterIndex = fbAdapter; displayIndex = fbDisplay;
            current = fbCur; defaultState = fbDef; minRefreshRateInMicroHz = fbMin; maxRefreshRateInMicroHz = fbMax;
            return true;
        }

        Logger.WriteLine("FreeSync probe: no capable display on any AMD adapter");
        return false;
    }

    private bool TryGetDisplayIndex(out int displayIndex)
    {
        bool ok = TryGetFreeSyncDisplay(out _, out displayIndex, out _, out _, out _, out _);
        if (!ok) displayIndex = -1;
        return ok;
    }

    // ADL use-case bitmask (ADL SDK: ADL_FREESYNC_USECASE_*): the setting is
    // not a boolean. AMD Software enables all use cases; passing plain 1
    // only enables the static-screen case and Adrenalin keeps reporting Off.
    private const int FreeSyncUsecaseStatic = 0x1;
    private const int FreeSyncUsecaseVideo = 0x2;
    private const int FreeSyncUsecaseGaming = 0x4;
    private const int FreeSyncUsecaseAll = FreeSyncUsecaseStatic | FreeSyncUsecaseVideo | FreeSyncUsecaseGaming;

    public static bool IsFreeSyncOn(int current) => (current & FreeSyncUsecaseAll) != 0;

    public bool TryGetFreeSyncState(out int current, out int defaultState, out int minRefreshRateInMicroHz, out int maxRefreshRateInMicroHz, out int displayIndex)
    {
        current = defaultState = minRefreshRateInMicroHz = maxRefreshRateInMicroHz = -1;
        displayIndex = -1;

        if (!IsValid) return false;
        return TryGetFreeSyncDisplay(out _, out displayIndex, out current, out defaultState, out minRefreshRateInMicroHz, out maxRefreshRateInMicroHz);
    }

    public bool SetFreeSync(bool enabled)
    {
        if (!IsValid) return false;

        // Apply to every capable display on every AMD adapter so internal
        // (iGPU or dGPU in MUX Ultimate) and external panels stay in sync
        // with Adrenalin instead of only the first display found.
        int setting = enabled ? FreeSyncUsecaseAll : 0;
        bool anyTried = false;
        bool anyOk = false;

        foreach (int adapterIndex in GetFreeSyncAdapterIndices())
        {
            if (Adl2.NativeMethods.ADL2_Display_NumberOfDisplays_Get(_adlContextHandle, adapterIndex, out int numDisplays) != Adl2.ADL_SUCCESS)
                continue;

            foreach (int i in GetFreeSyncCandidateIndices(adapterIndex, numDisplays))
            {
                if (Adl2.NativeMethods.ADL2_Display_FreeSync_Cap(_adlContextHandle, adapterIndex, i, out ADLFreeSyncCap caps) != Adl2.ADL_SUCCESS)
                    continue;
                if (caps.iCaps == 0)
                    continue;
                if (Adl2.NativeMethods.ADL2_Display_FreeSyncState_Get(
                    _adlContextHandle, adapterIndex, i,
                    out int current, out _, out _, out int maxRefresh) != Adl2.ADL_SUCCESS)
                    continue;

                int refreshRateInMicroHz = enabled ? maxRefresh : 0;
                int result = Adl2.NativeMethods.ADL2_Display_FreeSyncState_Set(
                    _adlContextHandle,
                    adapterIndex,
                    i,
                    setting,
                    refreshRateInMicroHz);
                Logger.WriteLine($"SET FREESYNC: adapter {adapterIndex} display {i}: {current} -> {setting} (rc={result})");
                anyTried = true;
                if (result == Adl2.ADL_SUCCESS) anyOk = true;
            }
        }

        return anyTried && anyOk;
    }

    public ADLODNPerformanceLevels? GetGPUClocks()
    {
        if (!IsValid) return null;

        ADLODNPerformanceLevels performanceLevels = new();
        ADL2_OverdriveN_SystemClocks_Get(_adlContextHandle, _internalDiscreteAdapter.AdapterIndex, ref performanceLevels);

        return performanceLevels;
    }

    public void KillGPUApps()
    {

        if (!IsValid) return;

        nint appInfoPtr = nint.Zero;
        int appCount = 0;

        try
        {
            // Get switchable graphics applications information
            var result = ADL2_SwitchableGraphics_Applications_Get(_adlContextHandle, 2, out appCount, out appInfoPtr);
            if (result != 0)
            {
                throw new Exception("Failed to get switchable graphics applications. Error code: " + result);
            }

            // Convert the application data pointers to an array of structs
            var appInfoArray = new ADLSGApplicationInfo[appCount];
            nint currentPtr = appInfoPtr;

            for (int i = 0; i < appCount; i++)
            {
                appInfoArray[i] = Marshal.PtrToStructure<ADLSGApplicationInfo>(currentPtr);
                currentPtr = nint.Add(currentPtr, Marshal.SizeOf<ADLSGApplicationInfo>());
            }

            var appNames = new List<string>();

            for (int i = 0; i < appCount; i++)
            {
                if (appInfoArray[i].iGPUAffinity == 1)
                {
                    Logger.WriteLine(appInfoArray[i].strFileName + ":" + appInfoArray[i].iGPUAffinity + "(" + appInfoArray[i].timeStamp + ")");
                    appNames.Add(Path.GetFileNameWithoutExtension(appInfoArray[i].strFileName));
                }
            }

            List<string> immune = new() { "svchost", "system", "ntoskrnl", "csrss", "winlogon", "wininit", "smss" };

            foreach (string kill in appNames)
                if (!immune.Contains(kill.ToLower()))
                    ProcessHelper.KillByName(kill);


        }
        catch (Exception ex)
        {
            Logger.WriteLine(ex.Message);
        }
        finally
        {
            // Clean up resources
            if (appInfoPtr != nint.Zero)
            {
                Marshal.FreeCoTaskMem(appInfoPtr);
            }

        }
    }


    private void ReleaseUnmanagedResources()
    {
        if (_adlContextHandle != nint.Zero)
        {
            ADL2_Main_Control_Destroy(_adlContextHandle);
            _adlContextHandle = nint.Zero;
            _isReady = false;
        }
    }

    public void Dispose()
    {
        ReleaseUnmanagedResources();
        GC.SuppressFinalize(this);
    }

    ~AmdGpuControl()
    {
        ReleaseUnmanagedResources();
    }
}

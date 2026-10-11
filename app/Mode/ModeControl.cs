using GHelper.Gpu.NVidia;
using GHelper.Helpers;
using GHelper.USB;
using PawnIO;
using System.Collections.Concurrent;

namespace GHelper.Mode
{
    public class ModeControl
    {

        static SettingsForm settings = Program.settingsForm;

        private static bool customFans = false;
        private static int customPower = 0;

        private int _cpuUV = 0;
        private int _igpuUV = 0;
        private int _cpuTemp = CpuInfo.DefaultTemp;
        private bool _ryzenPower = false;

        private static RyzenSmuService? _smu;
        private static readonly object _smuLock = new();
        private static bool _loggedNoGpuBoost;
        private static bool _loggedNoGpuTemp;
        private static bool _loggedVoltageCrashGuard;

        private static RyzenSmuService? GetSmu()
        {
            lock (_smuLock)
            {
                if (_smu != null && _smu.IsInitialized) return _smu;
                _smu?.Dispose();
                _smu = new RyzenSmuService();
                if (!_smu.Initialize(System.Reflection.Assembly.GetExecutingAssembly()))
                {
                    _smu.Dispose();
                    _smu = null;
                }
                else
                {
                    Logger.WriteLine($"SMU Init: {_smu.CpuCodeName} ({_smu.Family}), SMU v{_smu.SmuVersion >> 16}.{(_smu.SmuVersion >> 8) & 0xFF}.{_smu.SmuVersion & 0xFF}");
                }
                return _smu;
            }
        }

        public static bool IsPawnAvailable()  => GetSmu() != null;
        public static bool IsPawnInstalled()   => RyzenSmuService.IsPawnInstalled();

        static System.Timers.Timer? reapplyTimer;
        // Rails sag for a few seconds around an AC/DC transition; a limit write
        // landing in that window can trip a machine check, so reasserts hold
        // off after a power-source change.
        static DateTime lastPowerChange = DateTime.MinValue;
        static int ReapplySettleSeconds => AppConfig.Get("reapply_settle", 15);
        // Live PM-table values vs configured watts/°C: rounding noise only.
        const float DriftTolerance = 1f;
        static System.Timers.Timer modeToggleTimer = default!;
        static CancellationTokenSource _modeCts = new();
        static Task _modeTask = Task.CompletedTask;

        public ModeControl()
        {
        }

        // Cezanne/Rembrandt (Renoir) + Phoenix/HawkPoint (Mobile) silently reset temp limit under load.
        private static bool IsReapplyTempRequired()
        {
            var smu = GetSmu();
            return smu != null && smu.Family is CpuFamily.Renoir or CpuFamily.Mobile;
        }

        private static bool IsReapplyRyzenRequired()
        {
            var smu = GetSmu();
            return smu != null && smu.Family is CpuFamily.Raphael;
        }

        internal static void SetReapplyEnabled(bool enabled)
        {
            if (reapplyTimer is null)
            {
                int reapplyTime = AppConfig.Get("reapply_time",
                    (IsReapplyTempRequired() || AppConfig.IsApplyPower() || AppConfig.IsApplyGPU()) ? 5 : 0);
                if (reapplyTime > 0)
                {
                    reapplyTimer = new System.Timers.Timer(reapplyTime * 1000);
                    reapplyTimer.Elapsed += ReapplyTimer_Elapsed;
                }
            }
            if (reapplyTimer != null) reapplyTimer.Enabled = enabled;
        }


        private static void ReapplyTimer_Elapsed(object? sender, System.Timers.ElapsedEventArgs e)
        {
            var mc = Program.modeControl;
            if (mc is null) return;
            // Hold off right after an AC/DC transition, then rewrite a limit
            // only when the platform actually drifted from config. Ticks
            // skipped during the settle window catch up on the next tick.
            if ((DateTime.Now - lastPowerChange).TotalSeconds < ReapplySettleSeconds) return;
            mc.ReapplyIfDrifted();
        }

        public void NotePowerTransition() => lastPowerChange = DateTime.Now;

        // Conditional reapply: one live PM-table read per tick, then rewrite a
        // limit only when its live value no longer matches config (e.g. Renoir
        // resetting the temp limit under load). Values that can't be read fall
        // back to the previous unconditional write so limits are never left
        // unapplied.
        public void ReapplyIfDrifted()
        {
            bool applyPower = AppConfig.IsApplyPower();
            bool applyGpu = AppConfig.IsApplyGPU();
            if (!applyPower && !applyGpu) return;

            if (applyPower)
            {
                var smu = GetSmu();
                PowerLimits? live = null;
                if (smu != null)
                {
                    try { live = smu.GetPowerLimits(log: false); }
                    catch (Exception ex) { Logger.WriteLine("Conditional reapply: live limits read failed: " + ex.Message); }
                }

                int cpuTemp = AppConfig.GetMode("cpu_temp");
                if (cpuTemp >= CpuInfo.MinTemp && cpuTemp <= CpuInfo.DefaultTemp && !LiveTempMatches(live, cpuTemp))
                    SetCPUTemp(cpuTemp);

                int limit_total = AppConfig.GetMode("limit_total");
                int limit_slow = AppConfig.GetMode("limit_slow", limit_total);
                if (limit_slow < 0) limit_slow = limit_total;
                int limit_fast = AppConfig.GetMode("limit_fast", limit_slow);

                // The SMU PM-table read is the authority: on models without the
                // ASUS WMI readback IDs (e.g. TUF A15), a WMI-based check would
                // report "unverifiable" every tick and force a write, defeating
                // the drift check. Only fall back to the unconditional write when
                // the live read itself is unavailable.
                bool limitsMatch = live is not null
                    && limit_total >= AsusACPI.MinTotal && limit_total <= AsusACPI.MaxTotal
                    && Math.Abs(live.Stapm - limit_total) <= DriftTolerance
                    && Math.Abs(live.Slow - limit_slow) <= DriftTolerance
                    && Math.Abs(live.Fast - limit_fast) <= DriftTolerance;

                if (!limitsMatch)
                {
                    SetRyzenPower();
                    SetPower();
                }
            }

            if (applyGpu) ReapplyGpuIfDrifted();
        }

        private static bool LiveTempMatches(PowerLimits? live, int cpuTemp)
        {
            if (live is null) return false;
            return Math.Abs(live.TctlTemp - cpuTemp) <= DriftTolerance;
        }

        // Re-assert GPU clocks/voltage when the driver dropped them mid-session
        // (game load, sleep, TDR, service restart). The setters verify live
        // state first and skip when it already matches, so steady-state cost
        // is just the reads. Never elevates from here: NV writes need admin,
        // and a sleeping dGPU is left alone (a lock dropped across sleep is
        // re-asserted on the first active tick after wake).
        private void ReapplyGpuIfDrifted()
        {
            if (!ProcessHelper.IsUserAdministrator()) return;
            if (Program.acpi.DeviceGet(AsusACPI.GPUEco) == 1) return;
            var gpu = HardwareControl.GpuControl;
            if (gpu is null || !gpu.IsNvidia) return;
            try { if (gpu.GetGpuUse() is null) return; }
            catch { return; }
            SetGPUVoltage(false);
            SetGPUClocks(false);
        }

        // False only when every writable WMI limit reads back matching config.
        public void WaitForApply()
        {
            try { _modeTask.Wait(5000); } catch { }
        }

        public void AutoPerformance(bool powerChanged = false, bool init = false)
        {
            if (powerChanged) lastPowerChange = DateTime.Now;

            // Hand the power limits back to the machine on startup: our own
            // limits can still be sitting in the SMU from the previous session.
            // Only on the first pass - on later power source changes
            // SetPerformanceMode below re-applies the mode profile, and a fixed
            // OEM constant written after that would stomp the profile instead
            // of leaving the BIOS in charge.
            if (init && !AppConfig.IsApplyPower())
                ResetPowerLimits();

            int mode = AppConfig.Get("performance_" + Program.PerformanceKey());
            Logger.WriteLine($"{Program.currentSource} Performance Mode: {Modes.GetName(mode == -1 ? Modes.GetCurrent() : mode)}");

            if (mode != -1)
                SetPerformanceMode(mode, powerChanged);
            else
                SetPerformanceMode(Modes.GetCurrent());
        }


        public void ResetPerformanceMode()
        {
            ResetRyzen();

            Program.acpi.SetPerformanceMode(Modes.GetCurrentBase());

            // Clear the saved per-mode power mode, but do NOT switch the live
            // Windows power plan here: Factory Defaults must never change the
            // user's plan. The mode default re-applies on the next explicit
            // mode switch (SetPerformanceMode).
            AppConfig.RemoveMode("powermode");
        }

        public void Toast()
        {
            Program.toast.RunToast(Modes.GetCurrentName(), SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Online ? ToastIcon.Charger : ToastIcon.Battery);
        }

        public void SetPerformanceMode(int mode = -1, bool notify = false)
        {

            int oldMode = Modes.GetCurrent();
            if (mode < 0) mode = oldMode;

            if (!Modes.Exists(mode)) mode = 0;

            settings.ShowMode(mode);

            Modes.SetCurrent(mode);


            _modeCts.Cancel();
            _modeCts = new CancellationTokenSource();
            var ct = _modeCts.Token;

            _modeTask = Task.Run(async () =>
            {
                try
                {
                    var smu = GetSmu();
                    if (smu is not null)
                    {
                        try
                        {
                            PowerLimits? lim = smu.GetPowerLimits();
                            Logger.WriteLine(
                                "LiveSMULimits: " + (lim == null
                                    ? "null"
                                    : $"SPL={lim.Stapm:F1}W sPPT={lim.Slow:F1}W fPPT={lim.Fast:F1}W Tctl={lim.TctlTemp:F0}C" +
                                      (lim.ApuSlow.HasValue ? $" APU={lim.ApuSlow.Value:F1}W" : "")));
                        }
                        catch (Exception ex)
                        {
                            Logger.WriteLine("LiveSMULimits failed: " + ex.Message);
                        }
                    }

                    bool reset = AppConfig.IsResetRequired() && (Modes.GetBase(oldMode) == Modes.GetBase(mode)) && customPower > 0 && !AppConfig.IsApplyPower();

                    customFans = false;
                    customPower = 0;

                    SetModeLabel();

                    // Workaround for not properly resetting limits on G14 2024
                    if (reset)
                    {
                        Program.acpi.DeviceSet(AsusACPI.PerformanceMode, (Modes.GetBase(oldMode) != 1) ? AsusACPI.PerformanceTurbo : AsusACPI.PerformanceBalanced, "ModeReset");
                        await Task.Delay(TimeSpan.FromMilliseconds(1500), ct);
                    }

                    ct.ThrowIfCancellationRequested();

                    if (AppConfig.Is("status_mode")) Program.acpi.DeviceSet(AsusACPI.StatusMode, [0x00, Modes.GetBase(mode) == AsusACPI.PerformanceSilent ? (byte)0x02 : (byte)0x03], "StatusMode");
                    Program.acpi.SetPerformanceMode(AppConfig.IsManualModeRequired() ? AsusACPI.PerformanceManual : Modes.GetBase(mode));

                    // First entry into this base mode: the firmware has just
                    // restored its own limits, so snapshot them now as this
                    // mode's OEM stock - before any custom limit is written.
                    // Later resets use this snapshot, never a live readback
                    // (live would be the user's own custom once applied).
                    try { CaptureOemPowerForMode(Modes.GetBase(mode)); } catch { }

                    if (AppConfig.IsApplyGPU())
                    {
                        SetGPUVoltage();
                        SetGPUClocks();
                    }
                    else Logger.WriteLine("GPU auto-apply disabled, skipping voltage/clocks");

                    await Task.Delay(TimeSpan.FromMilliseconds(100), ct);
                    ct.ThrowIfCancellationRequested();
                    AutoFans();
                    await Task.Delay(TimeSpan.FromMilliseconds(1000), ct);
                    ct.ThrowIfCancellationRequested();
                    AutoPower();
                    
                    var command = AppConfig.GetModeString("mode_command");
                    if (command is not null)
                    {   Logger.WriteLine("Running mode command: " + command);
                        RestrictedProcessHelper.RunAsRestrictedUser(command);
                    }
                }
                catch (OperationCanceledException)
                {
                    Logger.WriteLine($"SetPerformanceMode cancelled (mode {mode})");
                }
                catch (Exception ex)
                {
                    Logger.WriteLine($"SetPerformanceMode failed (mode {mode}): {ex.Message}");
                }
            }, ct);

            if (notify) Toast();

            if (!AppConfig.Is("skip_powermode"))
            {
                // Windows power mode
                if (AppConfig.GetModeString("powermode") is not null)
                    PowerNative.SetPowerMode(AppConfig.GetModeString("powermode"));
                else
                    PowerNative.SetPowerMode(Modes.GetBase(mode));

                if (AppConfig.IsAutoASPM()) PowerNative.SetBalancedASPM();
                if (AppConfig.IsAutoStandbyNetworking()) PowerNative.SetConnectivityInStandby();
            }

            // CPU Boost setting override
            if (AppConfig.GetMode("auto_boost") != -1)
                    PowerNative.SetCPUBoost(AppConfig.GetMode("auto_boost"));

            settings.FansInit();

            SetReapplyEnabled(AppConfig.IsApplyPower() || AppConfig.IsApplyGPU());
        }

        // SyncEcMode disabled: on some ASUS models the EC mode readback is unreliable,
        // so we do not let the EC override the user-selected mode from user mode.
        public void SyncEcMode()
        {
        }


        private void ModeToggleTimer_Elapsed(object? sender, System.Timers.ElapsedEventArgs e)
        {
            modeToggleTimer.Stop();
            Logger.WriteLine($"Hotkey mode: {Modes.GetCurrent()}");
            SetPerformanceMode();

        }

        public void CyclePerformanceMode(bool back = false)
        {
            int delay = AppConfig.Get("mode_delay", 1000);

            if (modeToggleTimer is null)
            {
                modeToggleTimer = new System.Timers.Timer(delay);
                modeToggleTimer.Elapsed += ModeToggleTimer_Elapsed;
            }

            modeToggleTimer.Stop();
            modeToggleTimer.Start();
            Modes.SetCurrent(Modes.GetNext(back));
            Toast();
        }

        public void AutoFans(bool force = false)
        {
            customFans = false;

            if (AppConfig.IsApplyFans() || force)
            {

                bool xgmFan = false;
                if (AppConfig.Is("xgm_fan"))
                {
                    XGM.SetFan(AppConfig.GetFanConfig(AsusFan.XGM));
                    xgmFan = Program.acpi.IsXGConnected();
                }

                int cpuResult = Program.acpi.SetFanCurve(AsusFan.CPU, AppConfig.GetFanConfig(AsusFan.CPU));
                int gpuResult = Program.acpi.SetFanCurve(AsusFan.GPU, AppConfig.GetFanConfig(AsusFan.GPU));

                if (AppConfig.Is("mid_fan"))
                    Program.acpi.SetFanCurve(AsusFan.Mid, AppConfig.GetFanConfig(AsusFan.Mid));


                // Alternative way to set fan curve
                if (cpuResult != 1 || gpuResult != 1)
                {
                    cpuResult = Program.acpi.SetFanRange(AsusFan.CPU, AppConfig.GetFanConfig(AsusFan.CPU));
                    gpuResult = Program.acpi.SetFanRange(AsusFan.GPU, AppConfig.GetFanConfig(AsusFan.GPU));

                    // Something went wrong, resetting to default profile
                    if (cpuResult != 1 || gpuResult != 1)
                    {
                        Program.acpi.DeviceSet(AsusACPI.PerformanceMode, Modes.GetCurrentBase(), "Reset Mode");
                        settings.LabelFansResult("Model doesn't support custom fan curves");
                    }
                }
                else
                {
                    settings.LabelFansResult("");
                    customFans = true;
                }

                int hystUp = AppConfig.GetMode("hysteresis_up");
                int hystDown = AppConfig.GetMode("hysteresis_down");
                if (hystUp > 0 && hystDown > 0)
                    Program.acpi.SetFanHysteresis(hystUp, hystDown);

                // force set PPTs for missbehaving bios on FX507/517 series
                if ((AppConfig.IsPowerRequired() || xgmFan) && !AppConfig.IsApplyPower())
                {
                    Task.Run(async () =>
                    {
                        await Task.Delay(TimeSpan.FromSeconds(1));
                        Program.acpi.DeviceSet(AsusACPI.PPT_APUA0, 80, "PowerLimit Fix A0");
                        Program.acpi.DeviceSet(AsusACPI.PPT_APUA3, 80, "PowerLimit Fix A3");
                    });
                }

            } else
            {
                XGM.Reset();
            }

            SetModeLabel();

        }

        public void AutoPower(bool launchAsAdmin = false)
        {

            customPower = 0;

            bool applyPower = AppConfig.IsApplyPower();
            bool applyFans = AppConfig.IsApplyFans();

            if (applyPower && !applyFans && AppConfig.IsFanRequired())
            {
                AutoFans(true);
                Thread.Sleep(500);
            }

            SetPower(launchAsAdmin);

            Thread.Sleep(500);
            if (AppConfig.IsApplyGPU()) SetGPUPower();
            SetCrossPower();
            AutoRyzen();

            if (IsReapplyRyzenRequired())
                Task.Delay(5000).ContinueWith(_ => { AutoRyzen(); ReadRyzenLimits(); });

        }

        public void SetModeLabel()
        {
            settings.SetModeLabel(Properties.Strings.PerformanceMode + ": " + Modes.GetCurrentName() + (customFans ? "+" : "") + ((customPower > 0) ? " " + customPower + "W" : ""));
        }

        // Returns true only when the limits actually reached the SMU, so callers
        // can report a failed apply instead of assuming success.
        public bool SetRyzenPower(bool init = false)
        {
            if (init) _ryzenPower = true;

            if (!_ryzenPower) return false;
            if (!AppConfig.IsApplyPower()) return false;

            var smu = GetSmu();
            if (smu == null)
            {
                // No SMU access means no way to set the limit: say so, this is
                // the "slider does nothing" case (PawnIO driver missing/not loaded).
                if (init) Logger.WriteLine(IsPawnInstalled()
                    ? "PowerLimit not applied: SMU access failed (PawnIO installed but the module did not load)"
                    : "PowerLimit not applied: SMU unavailable - install the PawnIO driver (Fans & Power > Advanced > Install PawnIO)");
                return false;
            }

            int limit_total = AppConfig.GetMode("limit_total");
            int limit_slow = AppConfig.GetMode("limit_slow", limit_total);
            int limit_fast = AppConfig.GetMode("limit_fast", limit_slow);

            if (limit_total > AsusACPI.MaxTotal || limit_total < AsusACPI.MinTotal)
            {
                Logger.WriteLine($"PowerLimit skipped: limit_total={limit_total} out of range [{AsusACPI.MinTotal}..{AsusACPI.MaxTotal}]");
                return false;
            }

            smu.SetAllLimits(limit_total, limit_fast, limit_slow,
                out SmuStatus stapm, out SmuStatus fast, out SmuStatus slow);
            if (init) Logger.WriteLine($"STAPM: {limit_total}W {stapm} | SLOW: {limit_slow}W {slow} | FAST: {limit_fast}W {fast}");
            return stapm == SmuStatus.OK;
        }

        public void SetPower(bool launchAsAdmin = false)
        {
            // The CPU tab checkbox is the apply switch: limits are written only
            // while it is on. Unchecking it pushes the OEM defaults instead
            // (ResetPowerLimits) and stops asserting limits from here.
            if (!AppConfig.IsApplyPower()) return;

            bool allAMD = Program.acpi.IsAllAmdPPT();
            bool isAMD = CpuInfo.IsAMD;

            int limit_total = AppConfig.GetMode("limit_total");
            int limit_slow = AppConfig.GetMode("limit_slow", limit_total);
            int limit_fast = AppConfig.GetMode("limit_fast", limit_slow);
            int limit_cpu = AppConfig.GetMode("limit_cpu", AsusACPI.DefaultCPU);

            if (limit_slow < 0 || allAMD) limit_slow = limit_total;
            if (limit_fast < 0) limit_fast = limit_slow;
            if (limit_cpu < 0) limit_cpu = AsusACPI.DefaultCPU;

            if (limit_total > AsusACPI.MaxTotal || limit_total < AsusACPI.MinTotal)
            {
                Logger.WriteLine($"PowerLimit skipped: limit_total={limit_total} out of range [{AsusACPI.MinTotal}..{AsusACPI.MaxTotal}]");
                return;
            }

            // Validate only the limits that will actually be written below,
            // so a missing/invalid CPU or fPPT value can't silently block
            // the main SPL/sPPT limits (or vice versa). Clamp the fallback
            // values instead of aborting the whole apply.
            limit_slow = Math.Clamp(limit_slow, AsusACPI.MinTotal, AsusACPI.MaxTotal);
            limit_fast = Math.Clamp(limit_fast, AsusACPI.MinTotal, AsusACPI.MaxTotal);
            limit_cpu = Math.Clamp(limit_cpu, AsusACPI.MinCPU, Math.Max(AsusACPI.MaxCPU, AsusACPI.MaxTotal));

            bool supportA0 = Program.acpi.IsSupported(AsusACPI.PPT_APUA0);
            bool supportB0 = Program.acpi.IsAllAmdPPT();
            bool supportC1 = Program.acpi.IsSupported(AsusACPI.PPT_APUC1);
            bool wrote = false;

            // SPL and SPPT
            if (supportA0)
            {
                Program.acpi.DeviceSet(AsusACPI.PPT_APUA3, limit_total, "PowerLimit A3");
                Program.acpi.DeviceSet(AsusACPI.PPT_APUA0, limit_slow, "PowerLimit A0");
                customPower = limit_total;
                wrote = true;
            }
            else if (isAMD)
            {
                if (ProcessHelper.IsUserAdministrator())
                {
                    // Only count as written when the SMU actually took it.
                    if (SetRyzenPower(true)) wrote = true;
                }
                else if (launchAsAdmin)
                {
                    ProcessHelper.RunAsAdmin("cpu");
                    return;
                }
                else
                {
                    Logger.WriteLine("PowerLimit not applied: SMU write needs Administrator, run G-Helper as admin");
                }
            }

            if (allAMD) // CPU limit all amd models
            {
                Program.acpi.DeviceSet(AsusACPI.PPT_CPUB0, limit_cpu, "PowerLimit B0");
                customPower = limit_cpu;
                wrote = true;
            }
            else if (isAMD && supportC1) // FPPT boost for non all-amd models
            {
                Program.acpi.DeviceSet(AsusACPI.PPT_APUC1, limit_fast, "PowerLimit C1");
                wrote = true;
            }

            // Nothing above reached the hardware. Report it instead of leaving the
            // configured limits silently unapplied - the sliders look like
            // they work, but on these models they are a no-op.
            if (!wrote)
                Logger.WriteLine(
                    $"PowerLimit not applied: no usable CPU limit endpoint " +
                    $"(AMD={isAMD}, A0/A3={supportA0}, B0={supportB0}, C1={supportC1}); " +
                    $"configured SPL={limit_total}W sPPT={limit_slow}W CPU={limit_cpu}W stayed on paper only");

            SetModeLabel();

        }

        public void SetCrossPower()
        {
            // Same apply switch as the main CPU limits: when Apply Power Limits
            // is off these sliders must not touch the hardware either.
            if (!AppConfig.IsApplyPower()) return;

            int gpucpu = AppConfig.GetMode("limit_gpucpu");
            int crossload = AppConfig.GetMode("limit_crossload");
            int cputemp = AppConfig.GetMode("limit_cputemp");

            if (gpucpu >= AsusACPI.MinGPUtoCPU && gpucpu <= AsusACPI.MaxGPUtoCPU && Program.acpi.IsSupported(AsusACPI.PPT_GPUCPU9C))
                Program.acpi.DeviceSet(AsusACPI.PPT_GPUCPU9C, gpucpu, "PowerLimit 9C (GPU to CPU)");

            if (crossload >= AsusACPI.MinCrossLoad && crossload <= AsusACPI.MaxCrossLoad && Program.acpi.IsSupported(AsusACPI.PPT_CROSS9F))
                Program.acpi.DeviceSet(AsusACPI.PPT_CROSS9F, crossload, "PowerLimit 9F (Cross Loading)");

            if (cputemp >= AsusACPI.MinCPUTemp && cputemp <= AsusACPI.MaxCPUTemp && Program.acpi.IsSupported(AsusACPI.PPT_TEMP9E))
                Program.acpi.DeviceSet(AsusACPI.PPT_TEMP9E, cputemp, "PowerLimit 9E (CPU Temp)");
        }

        public void SetGPUClocks(bool launchAsAdmin = true, bool reset = false)
        {
            Task.Run(() =>
            {

                int core = AppConfig.GetMode("gpu_core");
                int memory = AppConfig.GetMode("gpu_memory");
                int clock_limit = AppConfig.GetMode("gpu_clock_limit");

                if (reset) core = memory = clock_limit = 0;

                if (core == -1 && memory == -1 && clock_limit == -1) return;
                //if ((gpu_core > -5 && gpu_core < 5) && (gpu_memory > -5 && gpu_memory < 5)) launchAsAdmin = false;

                if (Program.acpi.DeviceGet(AsusACPI.GPUEco) == 1) { Logger.WriteLine("Clocks: Eco"); return; }
                if (HardwareControl.GpuControl is null) { Logger.WriteLine("Clocks: NoGPUControl"); return; }
                if (!HardwareControl.GpuControl!.IsNvidia) { Logger.WriteLine("Clocks: NotNvidia"); return; }

                NvidiaGpuControl nvControl = (NvidiaGpuControl)HardwareControl.GpuControl;
                try
                {
                    int statusClocks = nvControl.SetClocks(core, memory);
                    int statusLimit = nvControl.SetMaxGPUClock(clock_limit);
                    if ((statusLimit == NvidiaGpuControl.NeedsAdmin || statusClocks == NvidiaGpuControl.NeedsAdmin) && launchAsAdmin) ProcessHelper.RunAsAdmin("gpu");
                    if (statusClocks == -1 || statusLimit == -1) Logger.WriteLine("Clocks: apply failed, not escalating (already admin or non-permission error)");
                }
                catch (Exception ex)
                {
                    Logger.WriteLine("Clocks Error:" + ex.ToString());
                }
                // No UI refresh here: the initiating UI already shows the new
                // values, and mode switches refresh via SetPerformanceMode's FansInit.
            });
        }

        // Crash guard: a saved voltage that was never confirmed stable, or a
        // previous unclean shutdown, must not be blindly re-applied on boot /
        // mode switch. Manual (force) applies from the UI always go through.
        private static bool ShouldSkipVoltageAutoApply(int confirmedVoltage)
        {
            if (confirmedVoltage < 0) return false;

            int pending = AppConfig.GetMode("gpu_voltage_pending", -1);
            if (pending >= 0 && pending != confirmedVoltage)
            {
                Logger.WriteLine($"Voltage: skipping auto-apply, unconfirmed trial {pending} mV was never confirmed (previous run may have crashed)");
                return true;
            }

            // clean_shutdown is captured once at startup into
            // Program.UncleanShutdownDetected. Do not read the live config
            // value here: it is always 0 while running, which would skip
            // voltage auto-apply on every boot, clean or not.
            if (Program.UncleanShutdownDetected)
            {
                // The reapply timer polls this every few seconds: log once.
                if (!_loggedVoltageCrashGuard)
                {
                    _loggedVoltageCrashGuard = true;
                    Logger.WriteLine("Voltage: skipping auto-apply after unclean shutdown, keeping stock until user re-applies");
                }
                return true;
            }

            return false;
        }

        public void SetGPUVoltage(bool launchAsAdmin = true, bool reset = false, bool force = false)
        {
            Task.Run(() =>
            {

                int voltage;

                if (reset) voltage = 0;
                else if (force)
                {
                    // Manual trial from the UI: prefer the pending trial value
                    // written by CommitVoltage, fall back to confirmed value.
                    int pending = AppConfig.GetMode("gpu_voltage_pending", -1);
                    voltage = pending >= 0 ? pending : AppConfig.GetMode("gpu_voltage");
                }
                else voltage = AppConfig.GetMode("gpu_voltage");

                if (reset) { /* unlock below */ }
                else if (voltage == -1) return;
                else if (!force && !reset && ShouldSkipVoltageAutoApply(voltage)) return;

                if (Program.acpi.DeviceGet(AsusACPI.GPUEco) == 1) { Logger.WriteLine("Voltage: Eco"); return; }
                if (HardwareControl.GpuControl is null) { Logger.WriteLine("Voltage: NoGPUControl"); return; }
                if (!HardwareControl.GpuControl!.IsNvidia) { Logger.WriteLine("Voltage: NotNvidia"); return; }

                NvidiaGpuControl nvControl = (NvidiaGpuControl)HardwareControl.GpuControl;
                bool rewrite = force;
                if (!reset && !rewrite)
                {
                    // Live check: a stale _lastVoltage cache must not mask a lock
                    // the driver dropped mid-session (game load, sleep, TDR,
                    // service restart). Rewrite when the driver drifted.
                    int clockLimit = AppConfig.GetMode("gpu_clock_limit");
                    var (locked, mv) = nvControl.GetVoltageLock();
                    bool mirror = locked && clockLimit > 0 && clockLimit < NvidiaGpuControl.MaxClockLimit
                        && Math.Abs(mv - clockLimit) <= 5;
                    if (locked && !mirror && mv == voltage) return;
                    rewrite = true;
                }
                try
                {
                    int status = nvControl.SetVoltage(voltage, rewrite);
                    if (status == NvidiaGpuControl.NeedsAdmin && launchAsAdmin) ProcessHelper.RunAsAdmin("gpu");
                    if (status == -1) Logger.WriteLine("Voltage: apply failed, not escalating (already admin or non-permission error)");
                    // A manual re-apply proves the value is wanted: stop
                    // skipping auto-apply for the rest of this session after
                    // a real unclean shutdown.
                    if (force && (status == 1 || status == 0)) Program.UncleanShutdownDetected = false;
                }
                catch (Exception ex)
                {
                    Logger.WriteLine("Voltage Error:" + ex.ToString());
                }
                // No UI refresh here: the initiating UI already shows the new
                // value, and mode switches refresh via SetPerformanceMode's FansInit.
            });
        }

        public void SetGPUPower()
        {

            int gpu_boost = AppConfig.GetMode("gpu_boost");
            int gpu_temp = AppConfig.GetMode("gpu_temp");
            int gpu_power = AppConfig.GetMode("gpu_power");

            int boostResult = -1;

            bool boostWanted = gpu_boost >= AsusACPI.MinGPUBoost && gpu_boost <= AsusACPI.MaxGPUBoost;
            bool tempWanted = gpu_temp >= AsusACPI.MinGPUTemp && gpu_temp <= AsusACPI.MaxGPUTemp;

            // Report once per session when a requested limit has no endpoint on
            // this model, instead of silently doing nothing on every apply.
            if ((boostWanted && !Program.acpi.IsSupported(AsusACPI.PPT_GPUC0)) && !_loggedNoGpuBoost)
            {
                _loggedNoGpuBoost = true;
                Logger.WriteLine($"GPU boost not applied: model exposes no GPU boost endpoint (PPT_GPUC0); {gpu_boost}W stays on paper only");
            }

            if (tempWanted && !Program.acpi.IsSupported(AsusACPI.PPT_GPUC2) && !_loggedNoGpuTemp)
            {
                _loggedNoGpuTemp = true;
                Logger.WriteLine($"GPU temp target not applied: model exposes no GPU temp endpoint (PPT_GPUC2); {gpu_temp}C stays on paper only");
            }

            if (gpu_power >= AsusACPI.MinGPUPower && gpu_power <= AsusACPI.MaxGPUPower && Program.acpi.IsSupported(AsusACPI.GPU_POWER))
                Program.acpi.DeviceSet(AsusACPI.GPU_POWER, gpu_power, "PowerLimit TGP (GPU VAR)");

            if (boostWanted && Program.acpi.IsSupported(AsusACPI.PPT_GPUC0))
                boostResult = Program.acpi.DeviceSet(AsusACPI.PPT_GPUC0, gpu_boost, "PowerLimit C0 (GPU BOOST)");

            if (tempWanted && Program.acpi.IsSupported(AsusACPI.PPT_GPUC2))
                Program.acpi.DeviceSet(AsusACPI.PPT_GPUC2, gpu_temp, "PowerLimit C2 (GPU TEMP)");

            // Fallback
            if (boostResult == 0)
                Program.acpi.DeviceSet(AsusACPI.PPT_GPUC0, gpu_boost, "PowerLimit C0");

        }

        public SmuStatus? SetCPUTemp(int cpuTemp, bool log = false)
        {
            if (cpuTemp < CpuInfo.MinTemp || cpuTemp > CpuInfo.DefaultTemp) return null;
            if (cpuTemp == CpuInfo.DefaultTemp && _cpuTemp == CpuInfo.DefaultTemp) return null;

            var smu = GetSmu();
            if (smu == null) return null;
            SmuStatus status = smu.SetThm(cpuTemp);
            if (log) Logger.WriteLine($"CPU Temp: {cpuTemp}°C {status}");
            if (status == SmuStatus.OK) _cpuTemp = cpuTemp;
            return status;
        }

        public void SetUV(int cpuUV)
        {
            if (!CpuInfo.IsSupportedUV()) return;

            if (cpuUV >= CpuInfo.MinCPUUV && cpuUV <= CpuInfo.MaxCPUUV)
            {
                var smu = GetSmu();
                if (smu == null) return;
                SmuStatus status = smu.SetCoAll(cpuUV);
                Logger.WriteLine($"UV: {cpuUV} {status}");
                if (status == SmuStatus.OK) _cpuUV = cpuUV;
            }
        }

        public void SetUViGPU(int igpuUV)
        {
            if (!CpuInfo.IsSupportedUViGPU()) return;

            if (igpuUV >= CpuInfo.MinIGPUUV && igpuUV <= CpuInfo.MaxIGPUUV)
            {
                var smu = GetSmu();
                if (smu == null) return;
                SmuStatus status = smu.SetCoGfx(igpuUV);
                Logger.WriteLine($"iGPU UV: {igpuUV} {status}");
                if (status == SmuStatus.OK) _igpuUV = igpuUV;
            }
        }

        public string SetRyzen(bool launchAsAdmin = false)
        {
            if (!ProcessHelper.IsUserAdministrator())
            {
                if (launchAsAdmin) ProcessHelper.RunAsAdmin("uv");
                return string.Empty;
            }

            var smu = GetSmu();
            if (smu == null) return string.Empty;

            var lines = new System.Text.StringBuilder();
            try
            {
                int cpuUV   = AppConfig.GetMode("cpu_uv",   0);
                int igpuUV  = AppConfig.GetMode("igpu_uv",  0);
                int cpuTemp = AppConfig.GetMode("cpu_temp");
                string? cpuUVCores = AppConfig.GetModeString("cpu_uv_cores");

                if (CpuInfo.IsSupportedUV() && cpuUVCores is not null)
                {
                    int core = 0;
                    foreach (var token in cpuUVCores.Split('-', StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (int.TryParse(token, out int uv) && -uv >= CpuInfo.MinCPUUV && -uv <= CpuInfo.MaxCPUUV)
                        {
                            SmuStatus s = smu.SetCoPer(core, -uv);
                            Logger.WriteLine($"UV core {core}: {-uv} {s}");
                            if (s == SmuStatus.OK) _cpuUV = -uv;
                        }
                        core++;
                    }
                    lines.AppendLine($"CPU UV cores {cpuUVCores}");
                }
                else if (CpuInfo.IsSupportedUV() && cpuUV >= CpuInfo.MinCPUUV && cpuUV <= CpuInfo.MaxCPUUV)
                {
                    SmuStatus s = smu.SetCoAll(cpuUV);
                    Logger.WriteLine($"UV: {cpuUV} {s}");
                    if (s == SmuStatus.OK) _cpuUV = cpuUV;
                    lines.AppendLine($"CPU UV {cpuUV}: {s}");
                }

                if (CpuInfo.IsSupportedUViGPU() && igpuUV >= CpuInfo.MinIGPUUV && igpuUV <= CpuInfo.MaxIGPUUV)
                {
                    SmuStatus s = smu.SetCoGfx(igpuUV);
                    Logger.WriteLine($"iGPU UV: {igpuUV} {s}");
                    if (s == SmuStatus.OK) _igpuUV = igpuUV;
                    lines.AppendLine($"iGPU UV {igpuUV}: {s}");
                }

                SmuStatus? tempStatus = SetCPUTemp(cpuTemp, true);
                if (tempStatus.HasValue) lines.AppendLine($"CPU Temp {cpuTemp}°C: {tempStatus}");
            }
            catch (Exception ex)
            {
                Logger.WriteLine("UV Error: " + ex.ToString());
            }

            SetReapplyEnabled(AppConfig.IsApplyUV() || AppConfig.IsApplyPower() || AppConfig.IsApplyGPU());
            return lines.ToString().TrimEnd();
        }

        public string ReadRyzenLimits()
        {
            var smu = GetSmu();
            if (smu == null) return string.Empty;

            try
            {
                PowerLimits? lim = smu.GetPowerLimits();
                if (lim == null) return string.Empty;

                string line = $"SPL: {lim.Stapm:F1}W | sPPT {lim.Slow:F1}W | fPPT {lim.Fast:F1}W";
                if (lim.ApuSlow.HasValue) line += $" | APU {lim.ApuSlow.Value:F1}W";
                line += $", Temp: {lim.TctlTemp:F0}°C";
                Logger.WriteLine("Ryzen Limits: " + line);
                return line;
            }
            catch (Exception ex)
            {
                Logger.WriteLine("ReadRyzenLimits Error: " + ex.ToString());
                return string.Empty;
            }
        }

        public void ResetRyzen()
        {
            if (_cpuUV != 0) SetUV(0);
            if (_igpuUV != 0) SetUViGPU(0);
            if (_cpuTemp != CpuInfo.DefaultTemp) SetCPUTemp(CpuInfo.DefaultTemp, true);
            SetReapplyEnabled(AppConfig.IsApplyPower() || AppConfig.IsApplyGPU());
        }

        // Diagnostics only: log what the SMU reports now. Never feed this back
        // into a limit write - see ResetPowerLimits.
        private void LogPowerLimits(string prefix)
        {
            if (!CpuInfo.IsAMD) return;
            try
            {
                var smu = GetSmu();
                if (smu is null) return;
                PowerLimits? lim = smu.GetPowerLimits(log: false);
                if (lim is null) return;
                Logger.WriteLine(
                    $"{prefix}: SPL={lim.Stapm:F1}W sPPT={lim.Slow:F1}W fPPT={lim.Fast:F1}W Tctl={lim.TctlTemp:F0}C"
                    + (lim.ApuSlow.HasValue ? $" APU={lim.ApuSlow.Value:F1}W" : ""));
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"{prefix}: live SMU read failed: {ex.Message}");
            }
        }

        // Reset the hardware to OEM limits, ignoring IsApplyPower (which is
        // false exactly when the user unchecks the box that triggered this).
        // We cannot call SetPower() for that reason, so write ACPI/SMU directly.
        // Uses the per-mode OEM snapshot for this base mode (Turbo/Balanced/
        // Silent each have their own), never a live SMU/EC readback - once a
        // custom limit is applied the live value is the user's own, not stock.
        public void ResetPowerLimits()
        {
            int baseMode = Modes.GetCurrentBase();
            int total = GetOemTotalForMode(baseMode);
            int slow = GetOemSlowForMode(baseMode);
            int fast = GetOemFastForMode(baseMode);
            int cpu = GetOemCpuForMode(baseMode);

            try
            {
                bool isAMD = CpuInfo.IsAMD;
                bool allAMD = Program.acpi.IsAllAmdPPT();

                // SPL / sPPT via ACPI (when the device is supported)
                if (Program.acpi.IsSupported(AsusACPI.PPT_APUA0))
                {
                    Program.acpi.DeviceSet(AsusACPI.PPT_APUA3, total, "PowerLimit A3 (reset)");
                    Program.acpi.DeviceSet(AsusACPI.PPT_APUA0, slow, "PowerLimit A0 (reset)");
                }
                else if (isAMD && ProcessHelper.IsUserAdministrator())
                {
                    // AMD fallback: write via SMU
                    var smu = GetSmu();
                    if (smu is not null)
                    {
                        smu.SetAllLimits(total, fast, slow, out var stapm, out var fastSt, out var slowSt);
                        Logger.WriteLine($"ResetPower: SMU reset STAPM={total}W FAST={fast}W SLOW={slow}W");
                    }
                }

                // CPU PPT for all-AMD machines
                if (allAMD)
                {
                    Program.acpi.DeviceSet(AsusACPI.PPT_CPUB0, cpu, "PowerLimit B0 (reset)");
                }
                else if (isAMD && Program.acpi.IsSupported(AsusACPI.PPT_APUC1))
                {
                    Program.acpi.DeviceSet(AsusACPI.PPT_APUC1, fast, "PowerLimit C1 (reset)");
                }

                // Cross load, GPU-to-CPU, CPU temp limits (ACPI)
                if (Program.acpi.IsSupported(AsusACPI.PPT_CROSS9F))
                    Program.acpi.DeviceSet(AsusACPI.PPT_CROSS9F, AsusACPI.MaxCrossLoad, "PowerLimit 9F (reset)");

                if (Program.acpi.IsSupported(AsusACPI.PPT_GPUCPU9C))
                    Program.acpi.DeviceSet(AsusACPI.PPT_GPUCPU9C, AsusACPI.MaxGPUtoCPU, "PowerLimit 9C (reset)");

                if (Program.acpi.IsSupported(AsusACPI.PPT_TEMP9E))
                    Program.acpi.DeviceSet(AsusACPI.PPT_TEMP9E, AsusACPI.MaxCPUTemp, "PowerLimit 9E (reset)");

                Logger.WriteLine($"ResetPower: hardware limits reset to OEM defaults (total {total}W / cpu {cpu}W)");

                // Read the limits back purely to confirm the write landed. Never
                // reuse this read as the value to write: once the user has set a
                // custom limit the SMU holds that custom value, so writing it
                // back would re-apply the user's setting instead of the default.
                LogPowerLimits("ResetPower: after");
            }
            catch (Exception ex)
            {
                Logger.WriteLine("ResetPower: failed to reset hardware limits: " + ex.Message);
            }
        }

        // OEM values the power limits return to. Shared by the config reset and
        // the hardware reset so config, sliders and hardware can't disagree.
        // Clamped to the slider range: a value the sliders can't represent
        // would be overwritten by the next SavePower anyway.
        public static int OemLimitTotal => Math.Clamp(AsusACPI.DefaultTotal, AsusACPI.MinTotal, AsusACPI.MaxTotal);
        public static int OemLimitCpu => Math.Clamp(AsusACPI.DefaultCPU, AsusACPI.MinTotal, AsusACPI.MaxTotal);

        // Per-base-mode OEM power limits. Turbo / Balanced / Silent each ship
        // with their own SPL / sPPT / fPPT / CPU PPT, so reverting to stock
        // must use the values belonging to the mode in use - never the user's
        // own custom values and never one mode's values for another mode.
        // Captured before any custom write (startup + first entry into each
        // mode). Missing entry = unknown, fall back to the static constants.
        private static readonly ConcurrentDictionary<int, int> _oemTotalByMode = new();
        private static readonly ConcurrentDictionary<int, int> _oemSlowByMode = new();
        private static readonly ConcurrentDictionary<int, int> _oemFastByMode = new();
        private static readonly ConcurrentDictionary<int, int> _oemCpuByMode = new();
        private static readonly ConcurrentDictionary<int, int> _oemGpuBoostByMode = new();
        private static readonly ConcurrentDictionary<int, int> _oemGpuTempByMode = new();
        private static readonly ConcurrentDictionary<int, int> _oemGpuTgpByMode = new();

        public static int GetOemTotalForMode(int baseMode) => _oemTotalByMode.TryGetValue(baseMode, out int v) ? v : OemLimitTotal;
        public static int GetOemSlowForMode(int baseMode) => _oemSlowByMode.TryGetValue(baseMode, out int v) ? v : OemLimitTotal;
        public static int GetOemFastForMode(int baseMode) => _oemFastByMode.TryGetValue(baseMode, out int v) ? v : OemLimitTotal;
        public static int GetOemCpuForMode(int baseMode) => _oemCpuByMode.TryGetValue(baseMode, out int v) ? v : OemLimitCpu;
        public static int GetOemGpuBoostForMode(int baseMode) => _oemGpuBoostByMode.TryGetValue(baseMode, out int v) ? v : (OemGpuBoost >= 0 ? OemGpuBoost : AsusACPI.MaxGPUBoost);
        public static int GetOemGpuTempForMode(int baseMode) => _oemGpuTempByMode.TryGetValue(baseMode, out int v) ? v : (OemGpuTemp >= 0 ? OemGpuTemp : AsusACPI.MaxGPUTemp);
        public static int GetOemGpuTgpForMode(int baseMode) => _oemGpuTgpByMode.TryGetValue(baseMode, out int v) ? v : OemGpuPower;

        // Snapshot the ACPI power endpoints for one base mode while the
        // hardware still holds the BIOS profile (right after the mode switch,
        // before any custom limit is written). Never call after a custom
        // write - the read would then be the user's own value, not stock.
        private void CaptureOemPowerForMode(int baseMode)
        {
            if (_oemTotalByMode.ContainsKey(baseMode) && _oemGpuBoostByMode.ContainsKey(baseMode)) return;
            try
            {
                int total = ReadOemPowerEndpoint(AsusACPI.PPT_APUA3, AsusACPI.MinTotal, AsusACPI.MaxTotal);
                int slow = ReadOemPowerEndpoint(AsusACPI.PPT_APUA0, AsusACPI.MinTotal, AsusACPI.MaxTotal);
                int fast = ReadOemPowerEndpoint(AsusACPI.PPT_APUC1, AsusACPI.MinTotal, AsusACPI.MaxTotal);
                int cpu = ReadOemPowerEndpoint(AsusACPI.PPT_CPUB0, AsusACPI.MinCPU, Math.Max(AsusACPI.MaxCPU, AsusACPI.MaxTotal));
                if (total > 0) _oemTotalByMode.TryAdd(baseMode, Math.Clamp(total, AsusACPI.MinTotal, AsusACPI.MaxTotal));
                if (slow > 0) _oemSlowByMode.TryAdd(baseMode, Math.Clamp(slow, AsusACPI.MinTotal, AsusACPI.MaxTotal));
                if (fast > 0) _oemFastByMode.TryAdd(baseMode, Math.Clamp(fast, AsusACPI.MinTotal, AsusACPI.MaxTotal));
                if (cpu > 0) _oemCpuByMode.TryAdd(baseMode, Math.Clamp(cpu, AsusACPI.MinTotal, AsusACPI.MaxTotal));

                int boost = ReadOemPowerEndpoint(AsusACPI.PPT_GPUC0, AsusACPI.MinGPUBoost, AsusACPI.MaxGPUBoost);
                int temp = ReadOemPowerEndpoint(AsusACPI.PPT_GPUC2, AsusACPI.MinGPUTemp, AsusACPI.MaxGPUTemp);
                int tgp = ReadOemPowerEndpoint(AsusACPI.GPU_POWER, AsusACPI.MinGPUPower, AsusACPI.MaxGPUPower);
                if (boost > 0) _oemGpuBoostByMode.TryAdd(baseMode, boost);
                if (temp > 0) _oemGpuTempByMode.TryAdd(baseMode, temp);
                if (tgp >= 0) _oemGpuTgpByMode.TryAdd(baseMode, tgp);
                Logger.WriteLine($"OEM power {ModeName(baseMode)}: total={total} slow={slow} fast={fast} cpu={cpu} gpuBoost={boost} gpuTemp={temp} tgp={tgp}");
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"OEM power capture {ModeName(baseMode)} failed: {ex.Message}");
            }
        }

        private static int ReadOemPowerEndpoint(uint endpoint, int min, int max)
        {
            try
            {
                if (!Program.acpi.IsSupported(endpoint)) return -1;
                int value = Program.acpi.DeviceGet((uint)endpoint);
                if (value < min || value > max) return -1;
                return value;
            }
            catch { return -1; }
        }

        // Clears the stored limits for the current mode. Split from
        // ResetPowerLimits so the UI can refresh the sliders from config and
        // push the hardware in the right order: on uncheck the mode profile is
        // re-applied last and stays in charge, on Factory Defaults the OEM
        // per-mode values are written before the mode reset so the BIOS
        // profile (same values) stays in charge afterwards.
        public void ResetPowerConfig()
        {
            int baseMode = Modes.GetCurrentBase();
            int total = GetOemTotalForMode(baseMode);
            int slow = GetOemSlowForMode(baseMode);
            int fast = GetOemFastForMode(baseMode);
            int cpu = GetOemCpuForMode(baseMode);
            AppConfig.SetMode("limit_total", total);
            AppConfig.SetMode("limit_slow", slow);
            AppConfig.SetMode("limit_fast", fast);
            AppConfig.SetMode("limit_cpu", cpu);
            AppConfig.SetMode("limit_cpucap", total);
            AppConfig.SetMode("limit_crossload", AsusACPI.MaxCrossLoad);
            AppConfig.SetMode("limit_gpucpu", AsusACPI.MaxGPUtoCPU);
            AppConfig.SetMode("limit_cputemp", AsusACPI.MaxCPUTemp);
        }

        // Limits this app owns, as the machine shipped with them. -1 (or 0 for
        // hysteresis) means "unknown": either the endpoint isn't supported or
        // the read failed. They have to be captured at startup, because once a
        // custom value has been written the original can't be recovered from
        // the hardware any more.
        public static int OemGpuPower = -1;
        public static int OemGpuBoost = -1;
        public static int OemGpuTemp = -1;
        public static int OemHysteresisUp = 0;
        public static int OemHysteresisDown = 0;

        // The EC keeps a fan curve per performance mode, and each one is a
        // different curve: reverting to stock has to use the curve belonging to
        // the mode in use, not one curve for all modes. They are captured here,
        // at startup, before G-Helper writes any curve of its own - after that
        // the active mode's curve is ours and the machine's own is gone.
        // Key is (base mode, fan), base mode being AsusACPI.Performance*.
        private static readonly ConcurrentDictionary<(int, AsusFan), byte[]> _oemFanCurves = new();

        public static bool TryGetOemFanCurve(AsusFan device, int baseMode, out byte[] curve)
        {
            return _oemFanCurves.TryGetValue((baseMode, device), out curve!);
        }

        public void CaptureOemDefaults()
        {
            OemGpuPower = CaptureOemLimit(AsusACPI.GPU_POWER, AsusACPI.MinGPUPower, AsusACPI.MaxGPUPower, "GPU TGP offset");
            OemGpuBoost = CaptureOemLimit(AsusACPI.PPT_GPUC0, AsusACPI.MinGPUBoost, AsusACPI.MaxGPUBoost, "GPU boost");
            OemGpuTemp = CaptureOemLimit(AsusACPI.PPT_GPUC2, AsusACPI.MinGPUTemp, AsusACPI.MaxGPUTemp, "GPU temp target");

            CaptureOemFanCurves();

            // Per-mode power snapshot for the boot mode, before any custom
            // write. Turbo / Balanced / Silent each keep their own limits, so
            // the current mode's stock is captured here; other modes are
            // captured lazily on first entry (see SetPerformanceMode).
            try { CaptureOemPowerForMode(Modes.GetCurrentBase()); } catch { }

            try
            {
                var hysteresis = Program.acpi.GetFanHysteresis();
                if (hysteresis.up > 0 && hysteresis.down > 0)
                {
                    OemHysteresisUp = hysteresis.up;
                    OemHysteresisDown = hysteresis.down;
                }
            }
            catch (Exception ex)
            {
                Logger.WriteLine("OEM fan hysteresis read failed: " + ex.Message);
            }
        }

        // Snapshot every mode's own curve (Balanced, Turbo, Silent) for each fan.
        // Read once, at startup, while the machine still holds them.
        private void CaptureOemFanCurves()
        {
            int[] baseModes = [AsusACPI.PerformanceBalanced, AsusACPI.PerformanceTurbo, AsusACPI.PerformanceSilent];
            AsusFan[] devices = Program.acpi.IsSupported(AsusACPI.DevsMidFanCurve)
                ? [AsusFan.CPU, AsusFan.GPU, AsusFan.Mid]
                : [AsusFan.CPU, AsusFan.GPU];

            foreach (AsusFan device in devices)
            {
                foreach (int baseMode in baseModes)
                {
                    try
                    {
                        byte[] curve = Program.acpi.GetFanCurve(device, baseMode);
                        if (AsusACPI.IsInvalidCurve(curve)) continue;
                        _oemFanCurves[(baseMode, device)] = curve;
                    }
                    catch (Exception ex)
                    {
                        Logger.WriteLine($"OEM fan curve {device}/{baseMode} read failed: {ex.Message}");
                    }
                }
            }

            foreach (var pair in _oemFanCurves)
                Logger.WriteLine($"OEM fan curve {pair.Key.Item2}/{ModeName(pair.Key.Item1)}: {BitConverter.ToString(pair.Value)}");
        }

        private static string ModeName(int baseMode) => baseMode switch
        {
            AsusACPI.PerformanceTurbo => "Turbo",
            AsusACPI.PerformanceSilent => "Silent",
            _ => "Balanced"
        };

        private static int CaptureOemLimit(uint endpoint, int min, int max, string name)
        {
            try
            {
                if (!Program.acpi.IsSupported(endpoint)) return -1;
                int value = Program.acpi.DeviceGet(endpoint);
                if (value < min || value > max)
                {
                    Logger.WriteLine($"OEM {name} unavailable (read {value})");
                    return -1;
                }
                Logger.WriteLine($"OEM {name}: {value}");
                return value;
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"OEM {name} read failed: {ex.Message}");
                return -1;
            }
        }

        // Push the machine's own GPU limits back: boost, temp target and the
        // extra TGP. SetGPUPower() can't do this once the config keys are gone
        // - it only writes when a value is configured - and writing a guessed
        // max instead would raise the GPU power on a machine that never had it.
        // Uses the per-mode snapshot (Turbo/Balanced/Silent each have their
        // own), falling back to the boot-mode snapshot, never a live readback
        // (live would be the user's own custom once applied).
        public void ResetGpuPower()
        {
            int baseMode = Modes.GetCurrentBase();
            int boost = _oemGpuBoostByMode.TryGetValue(baseMode, out int b) ? b : OemGpuBoost;
            int temp = _oemGpuTempByMode.TryGetValue(baseMode, out int t) ? t : OemGpuTemp;
            int tgp = _oemGpuTgpByMode.TryGetValue(baseMode, out int g) ? g : OemGpuPower;

            if (boost >= 0)
                Program.acpi.DeviceSet(AsusACPI.PPT_GPUC0, boost, "PowerLimit C0 (reset)");

            if (temp >= 0)
                Program.acpi.DeviceSet(AsusACPI.PPT_GPUC2, temp, "PowerLimit C2 (reset)");

            if (tgp >= 0)
                Program.acpi.DeviceSet(AsusACPI.GPU_POWER, tgp, "PowerLimit TGP (reset)");

            if (boost < 0 || temp < 0 || tgp < 0)
                Logger.WriteLine($"ResetPower: GPU limits only partly known (boost {boost}, temp {temp}, TGP {tgp}); the unknown ones were left alone");
        }

        public void AutoRyzen()
        {
            if (!CpuInfo.IsAMD) return;

            if (AppConfig.IsApplyUV()) SetRyzen();
            else ResetRyzen();
        }

        public void AutoCPUTemp()
        {
            if (!CpuInfo.IsAMD) return;
            if (!AppConfig.IsApplyUV()) return;
            if (!ProcessHelper.IsUserAdministrator()) return;

            try
            {
                SetCPUTemp(AppConfig.GetMode("cpu_temp"), true);
            }
            catch (Exception ex)
            {
                Logger.WriteLine("AutoCPUTemp Error: " + ex.Message);
            }
        }

        public void ShutdownReset()
        {
            if (!AppConfig.IsShutdownReset()) return;
            Program.acpi.DeviceSet(AsusACPI.PerformanceMode,AsusACPI.PerformanceBalanced, "Mode Reset");
        }

        public void SleepReset()
        {
            if (!AppConfig.IsSleepReset()) return;
            Program.acpi.DeviceSet(AsusACPI.PerformanceMode, Modes.GetCurrentBase(), "Sleep Reset");
        }

    }
}

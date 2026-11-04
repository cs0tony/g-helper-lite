using GHelper.Display;
using GHelper.Gpu.NVidia;
using GHelper.Helpers;
using GHelper.USB;
using System.Diagnostics;

namespace GHelper.Gpu
{
    // Lite mode: GPUModeControl still exists because InputDispatcher/Settings dereference it,
    // but when AppConfig.IsGPUControlEnabled() is false (default) every method that writes
    // hardware or state (ACPI GPU mode/eco/mux, process kills, NV service restarts,
    // event-log polling) short-circuits at the top as the last line of defence.
    // Read-only monitoring (GPU temperature, usage, power, VRAM) lives in
    // HardwareControl.GpuControl / NvmlHelper / AmdGpuControl sensors and keeps working.
    public class GPUModeControl
    {
        SettingsForm settings;

        // Static audit (all safe when the switch is off):
        // - gpuMode stays 0 (= GPUModeEco). Readers: Aura.ApplyGPUColor (cosmetic colour only)
        //   and internal gated methods. Config key "gpu_mode" stays 0 -> VisualiseIcon shows the
        //   standard/eco icon; no null risks (plain int).
        // - gpuExists stays null; only read inside the gated InitGPUMode.
        // - suspended is written by Settings.cs WndProc (power events) but only read by the
        //   gated AutoGPUMode; the bool write itself is harmless.
        // - gpuError stays null; readers are R1-gated VisualiseGPUMode (early return) and
        //   PictureGPU_Click (early return on null). CheckGpuError is short-circuited so it
        //   stays null.
        // - nvRestartPending stays false; only read by the gated SetGPUEco task.
        public static int gpuMode;
        public static bool? gpuExists = null;

        static bool nvRestartPending;


        public GPUModeControl(SettingsForm settingsForm)
        {
            // Constructor intentionally does nothing but store the form reference:
            // no event subscriptions, no timers, no hardware access (verified).
            settings = settingsForm;
        }

        public void InitGPUMode()
        {
            // Probes ACPI GPUEco/GPUMux, can force-fix the eco half-state via ACPI write,
            // updates gpu_mode config and aura colour. Write path -> short-circuit.
            if (!AppConfig.IsGPUControlEnabled()) return;

            if (AppConfig.NoGpu())
            {
                settings.HideGPUModes(false);
                return;
            }

            int eco = Program.acpi.DeviceGet(AsusACPI.GPUEco);
            int mux = Program.acpi.DeviceGet(AsusACPI.GPUMux);

            Logger.WriteLine("Eco flag : " + eco);
            Logger.WriteLine("Mux flag : " + mux);

            if (eco == 1 && HardwareControl.GpuControl?.IsValid == true)
            {
                Logger.WriteLine("Eco half-state");
                if (AppConfig.IsEcoBootFix())
                {
                    HardwareControl.DisposeGpuControl();
                    Task.Run(() => Program.acpi.DeviceSet(AsusACPI.GPUEco, eco, "GPUEco Force Fix"));
                }
            }

            settings.VisualiseGPUButtons(eco >= 0, mux >= 0);

            if (mux == 0)
            {
                gpuMode = AsusACPI.GPUModeUltimate;
            }
            else
            {
                if (eco == 1)
                    gpuMode = AsusACPI.GPUModeEco;
                else
                    gpuMode = AsusACPI.GPUModeStandard;

                // GPU mode not supported
                if (eco < 0 && mux < 0)
                {
                    if (gpuExists is null) gpuExists = Program.acpi.GetFan(AsusFan.GPU) >= 0;
                    settings.HideGPUModes((bool)gpuExists);
                }
            }

            AppConfig.Set("gpu_mode", gpuMode);
            settings.VisualiseGPUMode(gpuMode);

            Aura.CustomRGB.ApplyGPUColor(gpuMode);

            CheckGpuError();

        }



        public void SetGPUMode(int GPUMode, int auto = 0)
        {
            // Writes ACPI GPUMux/GPUEco, can trigger "shutdown /r" (Ultimate mode switch).
            // Final landing point of InputDispatcher hotkeys (Alt+F3/F14/F15) and Settings
            // buttons -> must short-circuit itself.
            if (!AppConfig.IsGPUControlEnabled()) return;

            int CurrentGPU = AppConfig.Get("gpu_mode");
            AppConfig.Set("gpu_auto", auto);

            if (CurrentGPU == GPUMode)
            {
                settings.VisualiseGPUMode();
                return;
            }

            var restart = false;
            var changed = false;

            int status;

            if (CurrentGPU == AsusACPI.GPUModeUltimate)
            {
                DialogResult dialogResult = settings.ShowMessage(Properties.Strings.AlertUltimateOff, Properties.Strings.AlertUltimateTitle, MessageBoxButtons.YesNo);
                if (dialogResult == DialogResult.Yes)
                {
                    status = Program.acpi.DeviceSet(AsusACPI.GPUMux, 1, "GPUMux");
                    restart = true;
                    changed = true;
                }
            }
            else if (GPUMode == AsusACPI.GPUModeUltimate)
            {
                if (Program.acpi.DeviceGet(AsusACPI.GPUMux) < 0)
                {
                    Logger.WriteLine("Mux not supported");
                    settings.VisualiseGPUMode();
                    return;
                }

                DialogResult dialogResult = settings.ShowMessage(Properties.Strings.AlertUltimateOn, Properties.Strings.AlertUltimateTitle, MessageBoxButtons.YesNo);
                if (dialogResult == DialogResult.Yes)
                {
                    Program.acpi.SetGPUEco(0);
                    Thread.Sleep(500);

                    int eco = Program.acpi.DeviceGet(AsusACPI.GPUEco);
                    Logger.WriteLine("Eco flag : " + eco);
                    if (eco == 1)
                    {
                        settings.VisualiseGPUMode();
                        return;
                    }

                    status = Program.acpi.DeviceSet(AsusACPI.GPUMux, 0, "GPUMux");
                    restart = true;
                    changed = true;
                }

            }
            else if (GPUMode == AsusACPI.GPUModeEco)
            {
                settings.VisualiseGPUMode(GPUMode);
                SetGPUEco(1);
                changed = true;
            }
            else if (GPUMode == AsusACPI.GPUModeStandard)
            {
                settings.VisualiseGPUMode(GPUMode);
                SetGPUEco(0);
                changed = true;
            }

            if (changed)
            {
                AppConfig.Set("gpu_mode", GPUMode);
            }

            if (restart)
            {
                settings.VisualiseGPUMode();
                Process.Start("shutdown", "/r /t 1");
            }

        }



        public void SetGPUEco(int eco)
        {
            // Writes ACPI GPUEco, kills GPU apps, restarts NV services, recreates the GPU
            // control handle and reapplies performance modes. Write path -> short-circuit.
            if (!AppConfig.IsGPUControlEnabled()) return;

            settings.LockGPUModes();

            Task.Run(async () =>
            {

                int status = 1;

                Program.modeControl.WaitForApply();

                if (eco == 1)
                {
                    HardwareControl.KillGPUApps();
                    HardwareControl.DisposeGpuControl();
                    if (AppConfig.IsNVPlatform()) NvidiaGpuControl.StopNVService();
                }

                Logger.WriteLine($"Running eco command {eco}");

                try
                {

                    status = Program.acpi.SetGPUEco(eco);
                    await Task.Delay(TimeSpan.FromMilliseconds(AppConfig.Get("refresh_delay", 500)));

                    settings.Invoke(delegate
                    {
                        InitGPUMode();
                        ScreenControl.AutoScreen();
                    });

                    if (eco == 0)
                    {
                        if ((AppConfig.IsNVPlatform() || nvRestartPending) && ProcessHelper.IsUserAdministrator())
                        {
                            settings.LockGPUModes(Properties.Strings.RestartingNVServices);
                            await Task.Delay(TimeSpan.FromMilliseconds(AppConfig.Get("nv_delay", 5000)));
                            if (AppConfig.IsNVPlatform()) NvidiaGpuControl.RestartNVService();
                            else NvidiaGpuControl.RestartNvContainer();
                            nvRestartPending = false;
                            settings.Invoke(delegate { InitGPUMode(); });
                            await Task.Delay(TimeSpan.FromMilliseconds(1000));
                        }

                        await HardwareControl.RecreateGpuControlWithRetry(3, 2);
                        if (HardwareControl.GpuControl is null) await HardwareControl.RecreateGpuControlWithRetry(3, 5);
                        CheckGpuError();
                        CheckStandardHalfState();
                    }

                    if (AppConfig.IsModeReapply())
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(1000));
                        Program.modeControl.AutoPerformance();
                    } else
                    {
                        Program.modeControl.SetGPUClocks(false);
                    }
                }
                catch (Exception ex)
                {
                    Logger.WriteLine("Error setting GPU Eco: " + ex.Message);
                }

            });


        }

        public static bool IsPlugged() =>
            Program.currentSource == Program.PowerSource.Barrel ||
            (Program.currentSource == Program.PowerSource.USBC && !AppConfig.Is("optimized_usbc"));

        public static bool suspended = false;

        public bool AutoGPUMode(bool optimized = false, int delay = 0)
        {
            // Auto Eco/Standard switching: reads ACPI and ends in SetGPUEco/SetGPUMode writes.
            // Safe default is false ("no switch performed") — callers just skip the re-init
            // branch and keep running ScreenControl.AutoScreen().
            if (!AppConfig.IsGPUControlEnabled()) return false;

            bool GpuAuto = AppConfig.Is("gpu_auto");
            bool ForceGPU = AppConfig.IsForceSetGPUMode() && !GpuAuto;

            int GpuMode = AppConfig.Get("gpu_mode");

            if (!GpuAuto && !ForceGPU) return false;

            if (suspended)
            {
                Logger.WriteLine("Skipping GPU Mode switch: Suspend");
                return false;
            }

            int eco = Program.acpi.DeviceGet(AsusACPI.GPUEco);
            int mux = Program.acpi.DeviceGet(AsusACPI.GPUMux);

            if (mux == 0)
            {
                if (optimized) SetGPUMode(AsusACPI.GPUModeStandard, 1);
                return false;
            }
            else
            {

                if (eco == 1)
                    if ((GpuAuto && IsPlugged()) || (ForceGPU && GpuMode == AsusACPI.GPUModeStandard))
                    {
                        if (delay > 0) Thread.Sleep(delay);
                        SetGPUEco(0);
                        return true;
                    }
                if (eco == 0)
                    if ((GpuAuto && !IsPlugged()) || (ForceGPU && GpuMode == AsusACPI.GPUModeEco))
                    {

                        if (Program.acpi.IsXGConnected()) return false;
                        if (HardwareControl.IsUsedGPU())
                        {
                            DialogResult dialogResult = settings.ShowMessage(Properties.Strings.AlertDGPU, Properties.Strings.AlertDGPUTitle, MessageBoxButtons.YesNo);
                            if (dialogResult == DialogResult.No) return false;
                        }
                        else if (GpuAuto && Program.acpi.IsExternalDisplayConnected())
                        {
                            DialogResult dialogResult = settings.ShowMessage(Properties.Strings.AlertExternalDisplay, Properties.Strings.AlertDGPUTitle, MessageBoxButtons.YesNo);
                            if (dialogResult == DialogResult.No) return false;
                        }

                        if (delay > 0) Thread.Sleep(delay);
                        SetGPUEco(1);
                        return true;
                    }
            }

            return false;

        }


        public void KillGPUApps()
        {
            // Destructive: kills processes running on the dGPU. Not a monitoring read ->
            // short-circuit (Settings "Stop GPU apps" button and eco path land here).
            if (!AppConfig.IsGPUControlEnabled()) return;

            if (HardwareControl.GpuControl is not null)
            {
                HardwareControl.GpuControl.KillGPUApps();
            }
        }

        public void CaptureNvBootState()
        {
            // Pure read (ACPI), but its only output (nvRestartPending) is consumed by the
            // gated SetGPUEco — short-circuited for uniformity; nvRestartPending stays false.
            if (!AppConfig.IsGPUControlEnabled()) return;

            nvRestartPending = Program.acpi.IsNVidiaGPU() && Program.acpi.DeviceGet(AsusACPI.GPUEco) == 1;
        }

        public void CheckStandardHalfState()
        {
            // Standard force-fix writes ACPI GPUEco and recreates the GPU control handle
            // -> write path, short-circuit (R1 already gates the Program.cs call site).
            if (!AppConfig.IsGPUControlEnabled()) return;

            if (gpuMode != AsusACPI.GPUModeStandard || HardwareControl.GpuControl is not null) return;

            Logger.WriteLine("Standard half-state");
            if (!AppConfig.IsStandardForceFix()) return;

            Task.Run(async () =>
            {
                Program.acpi.DeviceSet(AsusACPI.GPUEco, 0, "GPUStandard Force Fix");
                await Task.Delay(TimeSpan.FromMilliseconds(AppConfig.Get("nv_delay", 5000)));
                HardwareControl.RecreateGpuControl();
            });
        }

        public void StandardModeFix()
        {
            // Forces Standard mode on shutdown/suspend via ACPI write. R1 gates the
            // Program.cs call sites; short-circuited here as the last line of defence.
            if (!AppConfig.IsGPUControlEnabled()) return;

            if (!AppConfig.IsStandardModeFix()) return;
            if (Program.acpi.DeviceGet(AsusACPI.GPUMux) == 0) return; // Ultimate mode

            Logger.WriteLine("Forcing Standard Mode on shutdown");
            Program.acpi.SetGPUEco(0);
        }

        public static string? gpuError = null;

        public static void CheckGpuError()
        {
            // Pure read (Windows event log) + visualisation, but it also drives the gpuError
            // UI state. Short-circuited for uniformity: gpuError stays null, which every
            // reader (R1-gated VisualiseGPUMode, PictureGPU_Click) already handles safely,
            // and the event-log polling stops.
            if (!AppConfig.IsGPUControlEnabled()) return;

            Task.Run(() =>
            {
                string? error = DeviceHelper.GetGpuError();
                if (gpuError == error) return;
                gpuError = error;
                if (error != null) Logger.WriteLine(error);
                Program.settingsForm.VisualiseGPUMode();
            });

        }
    }
}

<#
  MonitorControl.ps1 - control monitor settings over DDC/CI (no buttons needed)
  Works with Samsung PX2370 and most other DDC/CI monitors.

  Talks to the monitor two ways:
    - through the NVIDIA driver (NvAPI I2C)  - works on laptops where Windows' own route fails
    - through Windows (dxva2)                - for monitors on Intel/AMD outputs

  GUI (default):   powershell -ExecutionPolicy Bypass -File MonitorControl.ps1
  Command line:
    -List                         list monitors and current values
    -Get   <vcp>                  read a VCP code      e.g.  -Get 0x10
    -Set   <vcp> -Value <n>       write a VCP code     e.g.  -Set 0x10 -Value 60
    -Monitor <index>              pick monitor (default 0, see -List)

  Common VCP codes:
    0x10 Brightness   0x12 Contrast   0x14 Color preset   0x16/0x18/0x1A Red/Green/Blue gain
    0x60 Input source (PX2370: 3=DVI, 5=HDMI)      0xDC Picture mode (PX2370: 01-06, F0-F2, F9-FB)
    0xD6 Power mode (1=on, 4=standby)
    0x04 Factory reset (write 1)      0x08 Reset color (write 1)
#>
param(
    [switch]$List,
    [string]$Get,
    [string]$Set,
    [int]$Value = -1,
    [int]$Monitor = 0
)

Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

public class Mon {
    public string Name;
    public bool Nv;        // true = NVIDIA I2C, false = Windows dxva2
    public IntPtr Handle;  // NVIDIA GPU handle or Windows physical monitor handle
    public uint Mask;      // NVIDIA output mask
}

public static class Ddc {
    // ---------- Windows dxva2 ----------
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct PHYSICAL_MONITOR {
        public IntPtr hPhysicalMonitor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szPhysicalMonitorDescription;
    }
    delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr rect, IntPtr data);
    [DllImport("user32.dll")]
    static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
    [DllImport("dxva2.dll")]
    static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint count);
    [DllImport("dxva2.dll")]
    static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint count, [Out] PHYSICAL_MONITOR[] monitors);
    [DllImport("dxva2.dll")]
    static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr hMonitor, byte code, IntPtr type, out uint current, out uint max);
    [DllImport("dxva2.dll")]
    static extern bool SetVCPFeature(IntPtr hMonitor, byte code, uint value);

    // ---------- NVIDIA NvAPI ----------
    [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
    static extern IntPtr NvQuery(uint id);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int NvInit();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int NvEnumGpus([Out] IntPtr[] gpus, out uint count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int NvGetOutputs(IntPtr gpu, out uint mask);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int NvI2C(IntPtr gpu, IntPtr info, [In, Out] uint[] unknown);
    static T Nv<T>(uint id) {
        IntPtr p = NvQuery(id);
        if (p == IntPtr.Zero) throw new Exception("NvAPI function missing");
        return (T)(object)Marshal.GetDelegateForFunctionPointer(p, typeof(T));
    }

    // Fills an NV_I2C_INFO_V3 struct (64 bytes on x64) and runs one I2C transfer
    static bool NvXfer(Mon m, bool write, byte addr, byte[] reg, byte[] data) {
        IntPtr info = Marshal.AllocHGlobal(64), pReg = IntPtr.Zero, pData = Marshal.AllocHGlobal(data.Length);
        try {
            for (int i = 0; i < 64; i++) Marshal.WriteByte(info, i, 0);
            if (reg != null) { pReg = Marshal.AllocHGlobal(reg.Length); Marshal.Copy(reg, 0, pReg, reg.Length); }
            if (write) Marshal.Copy(data, 0, pData, data.Length);
            Marshal.WriteInt32(info, 0, 64 | (3 << 16));     // version
            Marshal.WriteInt32(info, 4, (int)m.Mask);         // displayMask
            Marshal.WriteByte(info, 8, 1);                    // bIsDDCPort
            Marshal.WriteByte(info, 9, addr);                 // i2cDevAddress
            Marshal.WriteIntPtr(info, 16, pReg);              // pbI2cRegAddress
            Marshal.WriteInt32(info, 24, reg == null ? 0 : reg.Length);
            Marshal.WriteIntPtr(info, 32, pData);             // pbData
            Marshal.WriteInt32(info, 40, data.Length);        // cbSize
            Marshal.WriteInt32(info, 44, 0xFFFF);             // i2cSpeed (deprecated)
            NvI2C fn = Nv<NvI2C>(write ? 0x283AC65Au : 0x4D7B0709u);
            int r = fn(m.Handle, info, new uint[] { 0, 0 });
            if (!write) Marshal.Copy(pData, data, 0, data.Length);
            return r == 0;
        } finally {
            Marshal.FreeHGlobal(info); Marshal.FreeHGlobal(pData);
            if (pReg != IntPtr.Zero) Marshal.FreeHGlobal(pReg);
        }
    }

    static string EdidName(Mon m) {
        var edid = new byte[128];
        if (!NvXfer(m, false, 0xA0, new byte[] { 0 }, edid)) return null;
        for (int d = 54; d <= 108; d += 18) {
            if (edid[d] == 0 && edid[d + 1] == 0 && edid[d + 3] == 0xFC) {
                string s = System.Text.Encoding.ASCII.GetString(edid, d + 5, 13);
                int nl = s.IndexOf('\n');
                return (nl >= 0 ? s.Substring(0, nl) : s).Trim();
            }
        }
        return null;
    }

    static bool NvRead(Mon m, byte code, out uint cur, out uint max) {
        cur = max = 0;
        byte[] req = { 0x82, 0x01, code, (byte)(0x6E ^ 0x51 ^ 0x82 ^ 0x01 ^ code) };
        if (!NvXfer(m, true, 0x6E, new byte[] { 0x51 }, req)) return false;
        Thread.Sleep(50);   // DDC/CI needs >=40ms before the reply
        var r = new byte[12];
        if (!NvXfer(m, false, 0x6E, null, r)) return false;
        // Reply: 6E 88 02 rc code type maxHi maxLo curHi curLo checksum
        if (r[0] != 0x6E || r[1] != 0x88 || r[2] != 0x02 || r[3] != 0x00 || r[4] != code) return false;
        byte chk = 0x50;
        for (int i = 0; i < 10; i++) chk ^= r[i];
        if (chk != r[10]) return false;
        max = (uint)(r[6] << 8 | r[7]);
        cur = (uint)(r[8] << 8 | r[9]);
        return true;
    }

    static bool NvWrite(Mon m, byte code, uint value) {
        byte hi = (byte)(value >> 8), lo = (byte)value;
        byte[] req = { 0x84, 0x03, code, hi, lo, (byte)(0x6E ^ 0x51 ^ 0x84 ^ 0x03 ^ code ^ hi ^ lo) };
        bool ok = NvXfer(m, true, 0x6E, new byte[] { 0x51 }, req);
        Thread.Sleep(50);
        return ok;
    }

    // ---------- Public API ----------
    public static Mon[] GetMonitors() {
        var result = new List<Mon>();
        try {
            if (Nv<NvInit>(0x0150E828)() == 0) {
                var gpus = new IntPtr[64]; uint n;
                Nv<NvEnumGpus>(0xE5AC921F)(gpus, out n);
                for (int g = 0; g < n; g++) {
                    uint mask;
                    if (Nv<NvGetOutputs>(0x1730BFC9)(gpus[g], out mask) != 0) continue;
                    for (int b = 0; b < 32; b++) {
                        if ((mask & (1u << b)) == 0) continue;
                        var m = new Mon { Nv = true, Handle = gpus[g], Mask = 1u << b };
                        m.Name = (EdidName(m) ?? "Display") + " (NVIDIA)";
                        result.Add(m);
                    }
                }
            }
        } catch (DllNotFoundException) { } catch (EntryPointNotFoundException) { } catch (Exception) { }

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (h, dc, r, d) => {
            uint n;
            if (GetNumberOfPhysicalMonitorsFromHMONITOR(h, out n) && n > 0) {
                var arr = new PHYSICAL_MONITOR[n];
                if (GetPhysicalMonitorsFromHMONITOR(h, n, arr))
                    foreach (var p in arr)
                        result.Add(new Mon { Name = p.szPhysicalMonitorDescription + " (Windows)", Handle = p.hPhysicalMonitor });
            }
            return true;
        }, IntPtr.Zero);
        return result.ToArray();
    }

    // Returns {current, max} or null if the monitor did not answer
    public static uint[] Read(Mon m, byte code) {
        for (int i = 0; i < 3; i++) {   // DDC is flaky; retry a few times
            uint cur, max;
            bool ok = m.Nv ? NvRead(m, code, out cur, out max)
                           : GetVCPFeatureAndVCPFeatureReply(m.Handle, code, IntPtr.Zero, out cur, out max);
            if (ok) return new uint[] { cur, max };
            Thread.Sleep(50);
        }
        return null;
    }

    public static bool Write(Mon m, byte code, uint value) {
        for (int i = 0; i < 3; i++) {
            if (m.Nv ? NvWrite(m, code, value) : SetVCPFeature(m.Handle, code, value)) return true;
            Thread.Sleep(50);
        }
        return false;
    }
}
"@

function ConvertTo-Code([string]$s) {
    if ($s -match '^0x') { return [byte][Convert]::ToInt32($s.Substring(2), 16) }
    return [byte][int]$s
}

$monitors = [Ddc]::GetMonitors()
if ($monitors.Count -eq 0) {
    Write-Host "No DDC/CI-capable monitors found."
    exit 1
}

# ---------------- Command-line mode ----------------
if ($List) {
    for ($i = 0; $i -lt $monitors.Count; $i++) {
        $m = $monitors[$i]
        Write-Host "[$i] $($m.Name)"
        foreach ($c in @(@(0x10,'Brightness'), @(0x12,'Contrast'), @(0x14,'Color preset'), @(0x60,'Input source'), @(0xD6,'Power mode'))) {
            $r = [Ddc]::Read($m, [byte]$c[0])
            if ($r) { Write-Host ("     {0,-13} 0x{1:X2} = {2} (max {3})" -f $c[1], $c[0], $r[0], $r[1]) }
            else    { Write-Host ("     {0,-13} 0x{1:X2} = (no reply)" -f $c[1], $c[0]) }
        }
    }
    exit 0
}

$mon = $monitors[$Monitor]

if ($Get) {
    $r = [Ddc]::Read($mon, (ConvertTo-Code $Get))
    if ($r) { Write-Host "current=$($r[0]) max=$($r[1])"; exit 0 }
    Write-Host "Monitor did not reply (is DDC/CI enabled?)"; exit 1
}

if ($Set) {
    if ($Value -lt 0) { Write-Host "Use -Value <number>"; exit 1 }
    if ([Ddc]::Write($mon, (ConvertTo-Code $Set), [uint32]$Value)) { Write-Host "OK"; exit 0 }
    Write-Host "Write failed"; exit 1
}

# ---------------- GUI mode ----------------
Add-Type -AssemblyName System.Windows.Forms
[System.Windows.Forms.Application]::EnableVisualStyles()

$form = New-Object Windows.Forms.Form
$form.Text = "Monitor Control (DDC/CI)"
$form.Size = New-Object Drawing.Size(460, 600)
$form.FormBorderStyle = 'FixedDialog'
$form.MaximizeBox = $false
$form.StartPosition = 'CenterScreen'
$form.Font = New-Object Drawing.Font('Segoe UI', 9)

$y = 12
$picker = New-Object Windows.Forms.ComboBox
$picker.DropDownStyle = 'DropDownList'
$picker.SetBounds(12, $y, 420, 24)
for ($i = 0; $i -lt $monitors.Count; $i++) { [void]$picker.Items.Add("[$i] $($monitors[$i].Name)") }
$form.Controls.Add($picker)
$y += 36

$sliders = @()
function Add-Slider($name, $code) {
    $label = New-Object Windows.Forms.Label
    $label.SetBounds(12, $script:y, 420, 18)
    $form.Controls.Add($label)
    $bar = New-Object Windows.Forms.TrackBar
    $bar.SetBounds(12, $script:y + 18, 420, 40)
    $bar.TickFrequency = 10
    $bar.Tag = @{ Code = $code; Name = $name; Label = $label }
    # Only send to the monitor when the user lets go, so DDC isn't flooded
    $bar.Add_MouseUp({ param($s) Send-Value $s })
    $bar.Add_KeyUp({ param($s) Send-Value $s })
    $bar.Add_ValueChanged({ param($s) $s.Tag.Label.Text = "$($s.Tag.Name): $($s.Value)" })
    $form.Controls.Add($bar)
    $script:sliders += $bar
    $script:y += 62
}

function Current-Monitor { $monitors[$picker.SelectedIndex] }

# Some monitors (the PX2370 included) accept brightness but report a bogus value (255 of 100).
# Remember what we last set so the slider shows something sensible next time.
$stateFile = Join-Path $PSScriptRoot 'MonitorControl.last.json'
$lastSet = @{}
try { (Get-Content $stateFile -Raw | ConvertFrom-Json).PSObject.Properties | ForEach-Object { $lastSet[$_.Name] = [int]$_.Value } } catch {}

function Send-Value($bar) {
    if (-not [Ddc]::Write((Current-Monitor), [byte]$bar.Tag.Code, [uint32]$bar.Value)) {
        $status.Text = "Failed to set $($bar.Tag.Name)"
    } else {
        $status.Text = "$($bar.Tag.Name) set to $($bar.Value)"
        $lastSet[('0x{0:X2}' -f $bar.Tag.Code)] = $bar.Value
        try { $lastSet | ConvertTo-Json | Set-Content $stateFile -Encoding utf8 } catch {}
    }
}

Add-Slider 'Brightness' 0x10
Add-Slider 'Contrast'   0x12
Add-Slider 'Red gain'   0x16
Add-Slider 'Green gain' 0x18
Add-Slider 'Blue gain'  0x1A

function Add-Button($text, $x, $w, $action) {
    $b = New-Object Windows.Forms.Button
    $b.Text = $text
    $b.SetBounds($x, $script:y, $w, 30)
    $b.Add_Click($action)
    $form.Controls.Add($b)
}

# Picture mode (Samsung MagicBright). Values come from the PX2370's capabilities string;
# the monitor doesn't report the current mode, so pick one and watch the screen.
# Brightness is locked in some modes (e.g. Dynamic Contrast).
$modeLabel = New-Object Windows.Forms.Label
$modeLabel.Text = 'Picture mode:'
$modeLabel.SetBounds(12, ($y + 4), 90, 20)
$form.Controls.Add($modeLabel)
$modeBox = New-Object Windows.Forms.ComboBox
$modeBox.DropDownStyle = 'DropDownList'
$modeBox.SetBounds(104, $y, 328, 24)
foreach ($v in 0x01,0x02,0x03,0x04,0x05,0x06,0xF0,0xF1,0xF2,0xF9,0xFA,0xFB) { [void]$modeBox.Items.Add(('Mode 0x{0:X2}' -f $v)) }
$modeBox.Add_SelectionChangeCommitted({
    $v = [Convert]::ToInt32($modeBox.SelectedItem.Substring(7), 16)
    if ([Ddc]::Write((Current-Monitor), 0xDC, [uint32]$v)) { $status.Text = "Picture mode set to $($modeBox.SelectedItem)" }
    else { $status.Text = 'Failed to set picture mode' }
})
$form.Controls.Add($modeBox)
$y += 34

# Input source (Samsung values from the capabilities string: 01, 03 = DVI, 05 = HDMI)
Add-Button 'Input: DVI'  12  100 { [void][Ddc]::Write((Current-Monitor), 0x60, 3); $status.Text = 'Switched to DVI' }
Add-Button 'Input: HDMI' 118 100 { [void][Ddc]::Write((Current-Monitor), 0x60, 5); $status.Text = 'Switched to HDMI' }
Add-Button 'Standby'     224 100 { [void][Ddc]::Write((Current-Monitor), 0xD6, 4) }
Add-Button 'Refresh'     330 102 { Load-Values }
$y += 38
Add-Button 'Reset colors'  12 150 { [void][Ddc]::Write((Current-Monitor), 0x08, 1); Start-Sleep -Milliseconds 500; Load-Values }
Add-Button 'Factory reset' 168 150 {
    if ([Windows.Forms.MessageBox]::Show('Reset all monitor settings to factory defaults?', 'Confirm', 'YesNo') -eq 'Yes') {
        [void][Ddc]::Write((Current-Monitor), 0x04, 1); Start-Sleep -Milliseconds 800; Load-Values
    }
}
$y += 40

$status = New-Object Windows.Forms.Label
$status.SetBounds(12, $y, 420, 20)
$status.ForeColor = 'DimGray'
$form.Controls.Add($status)

function Load-Values {
    $m = Current-Monitor
    $missing = @()
    foreach ($bar in $sliders) {
        $r = [Ddc]::Read($m, [byte]$bar.Tag.Code)
        if ($r -and $r[1] -gt 0) {
            $bar.Enabled = $true
            $bar.Maximum = [int]$r[1]
            $key = '0x{0:X2}' -f $bar.Tag.Code
            if ($r[0] -le $r[1]) {
                $bar.Value = [int]$r[0]
                $bar.Tag.Label.Text = "$($bar.Tag.Name): $($bar.Value)"
            } else {
                # Monitor reported an impossible value; fall back to the last one we set
                $bar.Value = if ($lastSet.ContainsKey($key)) { [Math]::Min($lastSet[$key], $bar.Maximum) } else { [int]($bar.Maximum / 2) }
                $bar.Tag.Label.Text = "$($bar.Tag.Name): $($bar.Value)  (last set here; monitor doesn't report it)"
            }
        } else {
            $bar.Enabled = $false
            $bar.Tag.Label.Text = "$($bar.Tag.Name): not supported / no reply"
            $missing += $bar.Tag.Name
        }
    }
    $status.Text = if ($missing.Count -eq $sliders.Count) { 'Monitor not responding - is DDC/CI enabled?' } else { 'Ready' }
}

$picker.Add_SelectedIndexChanged({ Load-Values })
$picker.SelectedIndex = [Math]::Min($Monitor, $monitors.Count - 1)

[void]$form.ShowDialog()

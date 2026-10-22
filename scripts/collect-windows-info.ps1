$ErrorActionPreference = 'Stop'

# This script reports only development-relevant OS, display and hardware fields.
# It deliberately omits device ID, product ID, serial numbers, user name and keys.
if ($env:OS -ne 'Windows_NT') {
    throw 'Dette script skal køres på Windows.'
}

Add-Type -AssemblyName System.Windows.Forms
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class VisionAgentDisplayInfo {
    [DllImport("user32.dll")]
    public static extern uint GetDpiForSystem();
}
'@

$operatingSystem = Get-CimInstance Win32_OperatingSystem
$computerSystem = Get-CimInstance Win32_ComputerSystem
$processor = Get-CimInstance Win32_Processor | Select-Object -First 1
$graphics = Get-CimInstance Win32_VideoController | ForEach-Object {
    [ordered]@{
        name = $_.Name
        driver_version = $_.DriverVersion
        adapter_ram_gb = if ($_.AdapterRAM) { [math]::Round($_.AdapterRAM / 1GB, 1) } else { $null }
    }
}
$screens = [System.Windows.Forms.Screen]::AllScreens | ForEach-Object {
    [ordered]@{
        primary = $_.Primary
        width = $_.Bounds.Width
        height = $_.Bounds.Height
        x = $_.Bounds.X
        y = $_.Bounds.Y
    }
}
$dpi = [VisionAgentDisplayInfo]::GetDpiForSystem()

$report = [ordered]@{
    collected_at = (Get-Date).ToString('o')
    windows = [ordered]@{
        caption = $operatingSystem.Caption
        version = $operatingSystem.Version
        build = $operatingSystem.BuildNumber
        architecture = $operatingSystem.OSArchitecture
    }
    cpu = $processor.Name
    ram_gb = [math]::Round($computerSystem.TotalPhysicalMemory / 1GB, 1)
    graphics = @($graphics)
    display_count = @($screens).Count
    system_dpi = $dpi
    system_scale_percent = [math]::Round(($dpi / 96.0) * 100)
    displays = @($screens)
}

$report | ConvertTo-Json -Depth 5

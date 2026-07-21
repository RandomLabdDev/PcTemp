param(
    [string]$ApplicationDirectory = 'PcTemp',
    [string]$OutputPath = "$env:TEMP\PcTemp-tarjetas-preview.png",
    [switch]$LightTheme,
    [switch]$CollapseMemory,
    [switch]$CollapseDisks,
    [int]$ClientWidth = 0
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$app = Join-Path $root $ApplicationDirectory
Get-ChildItem (Join-Path $app '*.dll') | ForEach-Object {
    try { [void][Reflection.Assembly]::LoadFrom($_.FullName) } catch { }
}
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $app 'PcTemp.exe'))
$dashboardType = $assembly.GetType('PcTemp.DashboardForm')
$form = $dashboardType.GetConstructors()[0].Invoke([object[]]@($null, $null, $null, $null, $null, $null, $null, $null))
if ($LightTheme) {
    $flags = [Reflection.BindingFlags]'NonPublic,Instance'
    $dashboardType.GetField('_darkTheme', $flags).SetValue($form, $false)
    [void]$dashboardType.GetMethod('ApplyTheme', $flags).Invoke($form, $null)
}
$snapshotType = $assembly.GetType('PcTemp.TemperatureSnapshot')
$snapshot = [Activator]::CreateInstance($snapshotType)
$snapshotType.GetField('Cpu').SetValue($snapshot, [Nullable[single]]43)
$snapshotType.GetField('Gpu').SetValue($snapshot, [Nullable[single]]33)
$snapshotType.GetField('Board').SetValue($snapshot, [Nullable[single]]51)
$snapshotType.GetField('CpuName').SetValue($snapshot, 'Intel Core Ultra 7 2720K Plus')
$snapshotType.GetField('GpuName').SetValue($snapshot, 'NVIDIA GeForce RTX 3070 Ti')
$snapshotType.GetField('BoardName').SetValue($snapshot, 'Gigabyte Z890 AORUS Elite')
$snapshotType.GetField('CpuSensor').SetValue($snapshot, 'Intel Core Ultra 7 · CPU Package')
$snapshotType.GetField('GpuSensor').SetValue($snapshot, 'NVIDIA GeForce RTX 3070 Ti · GPU Core')
$snapshotType.GetField('BoardSensor').SetValue($snapshot, 'MotherBoard Temperature')
$snapshotType.GetField('BoardSensorId').SetValue($snapshot, '/lpc/ite/temperature/0')
$boardReadingType = $assembly.GetType('PcTemp.BoardSensorReading')
$boardSensors = $snapshotType.GetField('BoardSensors').GetValue($snapshot)
$boardSamples = @(
    @('/lpc/ite/temperature/0', 'MotherBoard Temperature', 51, 150),
    @('/lpc/ite/temperature/1', 'VRM Temperature', 47, 90),
    @('/lpc/ite/temperature/2', 'Chipset Temperature', 55, 100)
)
foreach ($sample in $boardSamples) {
    $reading = [Activator]::CreateInstance($boardReadingType)
    $boardReadingType.GetField('Id').SetValue($reading, $sample[0])
    $boardReadingType.GetField('Name').SetValue($reading, $sample[1])
    $boardReadingType.GetField('HardwareName').SetValue($reading, 'ASUS EC')
    $boardReadingType.GetField('Value').SetValue($reading, [single]$sample[2])
    $boardReadingType.GetField('Score').SetValue($reading, [int]$sample[3])
    $boardReadingType.GetField('IsStable').SetValue($reading, $true)
    [void]$boardSensors.Add($reading)
}
$snapshotType.GetField('UpdatedAt').SetValue($snapshot, [DateTime]::Now)
$diskType = $assembly.GetType('PcTemp.DiskTemperature')
$disks = $snapshotType.GetField('Disks').GetValue($snapshot)
$names = @('Samsung SSD 850 PRO 256GB', 'Samsung SSD 970', 'CT1000T710SSD8', 'WDC WD20EARX-008FB0')
$temperatures = @(30, 29, 40, 31)
$types = @('SSD SATA', 'M2', 'M2', 'HDD')
$interfaces = @('SATA', 'NVMe / PCIe', 'NVMe / PCIe', 'SATA')
$capacities = @([UInt64]256060514304, [UInt64]1000204886016, [UInt64]1000204886016, [UInt64]2000398934016)
for ($index = 0; $index -lt $names.Count; $index++) {
    $disk = [Activator]::CreateInstance($diskType)
    $diskType.GetField('Id').SetValue($disk, "disk$index")
    $diskType.GetField('Type').SetValue($disk, $types[$index])
    $diskType.GetField('Name').SetValue($disk, $names[$index])
    $diskType.GetField('Sensor').SetValue($disk, "$($names[$index]) · Temperature")
    $diskType.GetField('Interface').SetValue($disk, $interfaces[$index])
    $diskType.GetField('Status').SetValue($disk, 'En línea')
    $diskType.GetField('Health').SetValue($disk, 'Correcta')
    $diskType.GetField('Value').SetValue($disk, [single]$temperatures[$index])
    $diskType.GetField('HealthPercent').SetValue($disk, [Nullable[single]]98)
    $diskType.GetField('FreeSpaceGigabytes').SetValue($disk, [Nullable[single]](120 + 80 * $index))
    $diskType.GetField('ActivityPercent').SetValue($disk, [Nullable[single]](2 + $index))
    $diskType.GetField('ReadBytesPerSecond').SetValue($disk, [Nullable[single]](4MB + 1MB * $index))
    $diskType.GetField('WriteBytesPerSecond').SetValue($disk, [Nullable[single]](1MB + 512KB * $index))
    $diskType.GetField('CapacityBytes').SetValue($disk, $capacities[$index])
    [void]$disks.Add($disk)
}
$memoryType = $assembly.GetType('PcTemp.MemoryTemperature')
$memories = $snapshotType.GetField('Memories').GetValue($snapshot)
foreach ($slot in @(1, 3)) {
    $memory = [Activator]::CreateInstance($memoryType)
    $memoryType.GetField('Id').SetValue($memory, "memory:preview:$slot")
    $memoryType.GetField('Name').SetValue($memory, "Corsair - CMK32GX5M2E6000Z36 (#$slot)")
    $memoryType.GetField('Sensor').SetValue($memory, 'Temperature')
    $memoryType.GetField('Value').SetValue($memory, [single](35 + $slot))
    $memoryType.GetField('CapacityBytes').SetValue($memory, [UInt64]17179869184)
    $memoryType.GetField('MemoryType').SetValue($memory, 'DDR5')
    $memoryType.GetField('SpeedMHz').SetValue($memory, [uint32]6000)
    $memoryType.GetField('ConfiguredSpeedMHz').SetValue($memory, [uint32]6000)
    $memoryType.GetField('ConfiguredVoltageMillivolts').SetValue($memory, [uint32]1350)
    $memoryType.GetField('Slot').SetValue($memory, "DIMM #$slot")
    [void]$memories.Add($memory)
}
$updateValues = $dashboardType.GetMethod('UpdateValues')
for ($sample = 0; $sample -lt 18; $sample++) {
    $snapshotType.GetField('Cpu').SetValue($snapshot, [Nullable[single]](43 + [Math]::Sin($sample / 2.2) * 5))
    $snapshotType.GetField('Gpu').SetValue($snapshot, [Nullable[single]](33 + [Math]::Sin($sample / 3.1) * 3))
    $snapshotType.GetField('Board').SetValue($snapshot, [Nullable[single]](51 + [Math]::Sin($sample / 4.0) * 2))
    $snapshotType.GetField('UpdatedAt').SetValue($snapshot, [DateTime]::Now.AddSeconds($sample))
    for ($index = 0; $index -lt $names.Count; $index++) {
        $diskType.GetField('Value').SetValue($disks[$index], [single]($temperatures[$index] + [Math]::Sin(($sample + $index) / 3.0) * 2))
    }
    [void]$updateValues.Invoke($form, [object[]]@($snapshot, 5, $true, $true, $true))
}
if ($ClientWidth -gt 0) {
    $form.ClientSize = [Drawing.Size]::new($ClientWidth, $form.ClientSize.Height)
}
$form.Show()
[Windows.Forms.Application]::DoEvents()
$flags = [Reflection.BindingFlags]'NonPublic,Instance'
if ($CollapseMemory) {
    $dashboardType.GetField('_memoryGroupCollapsed', $flags).SetValue($form, $true)
}
if ($CollapseDisks) {
    $dashboardType.GetField('_diskGroupCollapsed', $flags).SetValue($form, $true)
}
if ($CollapseMemory -or $CollapseDisks) {
    [void]$dashboardType.GetMethod('ApplyGroupVisibility', $flags).Invoke($form, $null)
}
[Windows.Forms.Application]::DoEvents()
$form.PerformLayout()
$bitmap = New-Object Drawing.Bitmap $form.Width, $form.Height
$form.DrawToBitmap($bitmap, [Drawing.Rectangle]::new(0, 0, $bitmap.Width, $bitmap.Height))
$bitmap.Save($OutputPath, [Drawing.Imaging.ImageFormat]::Png)
$bitmap.Dispose()
$form.Hide()
$form.Dispose()
Write-Output $OutputPath

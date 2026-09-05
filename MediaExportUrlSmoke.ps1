param([string]$AssemblyPath = ".\dist\LiveBoard.exe")

$ErrorActionPreference = "Stop"
$assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $AssemblyPath))
$type = $assembly.GetType("LiveBoard.MediaExportService", $true)
$flags = [Reflection.BindingFlags]"NonPublic,Static"
$getModalId = $type.GetMethod("GetDouyinModalId", $flags)
$isMediaUrl = $type.GetMethod("IsDouyinMediaUrl", $flags)
$cases = @{
    "https://www.douyin.com/user/example?from_tab_name=main&modal_id=7677466408520061274" = "7677466408520061274"
    "https://www.douyin.com/user/example?modal_id=7673110614768847962&from_tab_name=main" = "7673110614768847962"
}

foreach ($case in $cases.GetEnumerator()) {
    $id = $getModalId.Invoke($null, @($case.Key))
    $isMedia = $isMediaUrl.Invoke($null, @($case.Key))
    if ($id -ne $case.Value -or -not $isMedia) {
        throw "Douyin modal URL regression: $($case.Key)"
    }
}

$dictionaryType = [Collections.Generic.Dictionary[string,object]]
$api = [Activator]::CreateInstance($dictionaryType)
$api["format_id"] = "download_addr-0"
$api["format_note"] = "Download video, watermarked (API)"
$api["vcodec"] = "h264"
$api["acodec"] = "aac"
$api["width"] = 720
$api["height"] = 1078
$valid = [Activator]::CreateInstance($dictionaryType)
$valid["format_id"] = "playback_540p-2"
$valid["format_note"] = "Playback video"
$valid["vcodec"] = "h264"
$valid["acodec"] = "aac"
$valid["width"] = 576
$valid["height"] = 856
$validHigh = [Activator]::CreateInstance($dictionaryType)
$validHigh["format_id"] = "playback_720p-2"
$validHigh["format_note"] = "Playback video"
$validHigh["vcodec"] = "hevc"
$validHigh["acodec"] = "aac"
$validHigh["width"] = 720
$validHigh["height"] = 1070
$root = [Activator]::CreateInstance($dictionaryType)
$root["formats"] = [object[]]@($api, $valid, $validHigh)
$formats = [Activator]::CreateInstance([Collections.Generic.List[LiveBoard.MediaFormatOption]])
$buildFormats = $type.GetMethod("BuildFormatOptions", [Reflection.BindingFlags]"NonPublic,Instance")
$service = New-Object LiveBoard.MediaExportService
$douyin = [string][char]0x6296 + [char]0x97F3
$arguments = New-Object "object[]" 3
$arguments[0] = $root
$arguments[1] = $formats
$arguments[2] = $douyin
$buildFormats.Invoke($service, $arguments) | Out-Null
$formatIds = @($formats | ForEach-Object FormatId)
if ($formatIds -contains "download_addr-0" -or $formatIds -contains "playback_540p-2" -or
    $formatIds -notcontains "playback_720p-2" -or $formatIds -notcontains "best") {
    throw "Douyin API fallback format regression: $($formatIds -join ',')"
}

"Douyin URL and format checks passed."

$mainType = $assembly.GetType("LiveBoard.MainWindow", $true)
$resolveReflow = $mainType.GetMethod("ResolveDouyinReflowWebRidAsync", $flags)
$reflowId = $resolveReflow.Invoke($null, @($null, [Uri]"https://webcast.amemv.com/douyin/webcast/reflow/7679460790810086179"))
$reflowId = $reflowId.GetAwaiter().GetResult()
if ($reflowId -ne "7679460790810086179") {
    throw "Douyin reflow room fallback regression: $reflowId"
}

"Douyin reflow room check passed."

param([string]$AssemblyPath = ".\dist\LiveBoard.exe")

$ErrorActionPreference = "Stop"
$assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $AssemblyPath))
$public = [Reflection.BindingFlags]"Public,Static"
$private = [Reflection.BindingFlags]"NonPublic,Static"
$privateInstance = [Reflection.BindingFlags]"NonPublic,Instance"

$cookieType = $assembly.GetType("LiveBoard.BrowserCookieService", $true)
$inspect = $cookieType.GetMethod("Inspect", $public)

function Get-CookieReport([string]$Label) {
    return $inspect.Invoke($null, @($Label))
}

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "FAILED: $Message" }
    Write-Host "  ok  $Message"
}

Write-Host "== 登录来源探测 =="
$noBrowser = Get-CookieReport "不使用浏览器登录"
Assert-True ($noBrowser.State.ToString() -eq "NotSelected") "未选择浏览器时状态为 NotSelected"

$chrome = Get-CookieReport "Google Chrome"
Write-Host ("  Chrome  -> {0} : {1}" -f $chrome.State, $chrome.Detail)
Assert-True (@("Locked", "Encrypted", "Missing", "Ready") -contains $chrome.State.ToString()) "Chrome 探测返回了已知状态"
if ($chrome.State.ToString() -ne "Ready") {
    Assert-True (-not [string]::IsNullOrWhiteSpace($chrome.Detail)) "Chrome 不可用时给出中文原因"
    Assert-True ($chrome.Detail -match "cookies\.txt|退出") "Chrome 不可用时提示可执行的替代方案"
}

$edge = Get-CookieReport "Microsoft Edge"
Write-Host ("  Edge    -> {0} : {1}" -f $edge.State, $edge.Detail)
Assert-True (@("Locked", "Encrypted", "Missing", "Ready") -contains $edge.State.ToString()) "Edge 探测返回了已知状态"
if ($edge.State.ToString() -eq "Encrypted") {
    Assert-True ($edge.HasAppBoundCookies) "判定为 v20 加密时同时标记了 HasAppBoundCookies"
    Assert-True ($edge.Detail -match "App-Bound") "v20 加密给出明确原因"
}

$firefox = Get-CookieReport "Mozilla Firefox"
Write-Host ("  Firefox -> {0} : {1}" -f $firefox.State, $firefox.Detail)
Assert-True (@("Locked", "Missing", "Ready") -contains $firefox.State.ToString()) "Firefox 探测返回了已知状态"

Write-Host "== cookies.txt 解析 =="
$sample = Join-Path (Get-Location) "cookie-smoke-sample.txt"
@(
    "# Netscape HTTP Cookie File",
    ".instagram.com`tTRUE`t/`tTRUE`t1893456000`tsessionid`tabc123",
    ".instagram.com`tTRUE`t/`tTRUE`t1893456000`tcsrftoken`txyz",
    ".x.com`tTRUE`t/`tTRUE`t0`tauth_token`tdeadbeef"
) | Set-Content -LiteralPath $sample -Encoding UTF8

try {
    $describe = $cookieType.GetMethod("TryDescribeCookieFile", $public)
    $arguments = New-Object "object[]" 3
    $arguments[0] = [string]$sample
    $arguments[1] = $null
    $arguments[2] = $null
    $valid = $describe.Invoke($null, $arguments)
    Write-Host ("  summary: {0}" -f $arguments[1])
    Assert-True ([bool]$valid) "有效 cookies.txt 通过校验"
    Assert-True ($arguments[1] -match "3 条") "统计出 3 条 Cookie"

    $missing = New-Object "object[]" 3
    $missing[0] = [string](Join-Path (Get-Location) "not-exists.txt")
    $missing[1] = $null
    $missing[2] = $null
    $validMissing = $describe.Invoke($null, $missing)
    Assert-True (-not [bool]$validMissing) "不存在的文件被拒绝"
    Assert-True (-not [string]::IsNullOrWhiteSpace($missing[2])) "缺失文件给出错误说明"
}
finally {
    Remove-Item -LiteralPath $sample -Force -ErrorAction SilentlyContinue
}

Write-Host "== 错误信息本地化 =="
$serviceType = $assembly.GetType("LiveBoard.MediaExportService", $true)
$buildFormats = $serviceType.GetMethod("BuildFormatOptions", $privateInstance)
$formatType = $assembly.GetType("LiveBoard.MediaFormatOption", $true)
$formatListType = [System.Collections.Generic.List``1].MakeGenericType($formatType)
$formatList = [Activator]::CreateInstance($formatListType)
$formatRoot = New-Object "System.Collections.Generic.Dictionary[string,object]"

function New-BilibiliFormat([string]$Id, [string]$Name, [double]$Fps) {
    $item = New-Object "System.Collections.Generic.Dictionary[string,object]"
    $item["format_id"] = $Id
    $item["format"] = $Name
    $item["format_note"] = "1080P"
    $item["fps"] = $Fps
    $item["width"] = 960
    $item["height"] = 1920
    $item["vcodec"] = "avc1.640033"
    $item["acodec"] = "none"
    $item["tbr"] = 3000
    return $item
}

$format30 = New-BilibiliFormat "30080" "1080P 高清" 30
$format60 = New-BilibiliFormat "30116" "1080P 60帧" 60
$formatRoot["formats"] = [object[]]@($format30.PSObject.BaseObject, $format60.PSObject.BaseObject)
$buildArguments = New-Object "object[]" 3
$buildArguments[0] = $formatRoot.PSObject.BaseObject
$buildArguments[1] = $formatList.PSObject.BaseObject
$buildArguments[2] = "Bilibili"
$buildFormats.Invoke((New-Object LiveBoard.MediaExportService), $buildArguments)
$labels = @($formatList | ForEach-Object { $_.Label })
Assert-True (($labels -join "|") -match "1080P 60帧") "B站 1080P 60帧不会与普通 1080P 合并"
Assert-True (($labels -join "|") -match "1080P 高清") "B站普通 1080P 画质仍被保留"
Assert-True ($labels[1] -match "60帧") "同分辨率下 60 帧排在普通画质前"

$mapError = $serviceType.GetMethod("MapError", $private)
$copyError = $mapError.Invoke($null, @("ERROR: Could not copy Chrome cookie database. See  https://github.com/yt-dlp/yt-dlp/issues/7271  for more info", "Instagram"))
Write-Host ("  copy  -> {0}" -f $copyError)
Assert-True ($copyError -match "无法读取浏览器 Cookie 数据库") "锁定/复制失败被翻译为中文说明"

$v20Error = $mapError.Invoke($null, @("WARNING: Failed to decrypt with DPAPI. See https://github.com/yt-dlp/yt-dlp/issues/10927 for more info", "Instagram"))
Write-Host ("  dpapi -> {0}" -f $v20Error)
Assert-True ($v20Error -match "App-Bound") "v20 解密失败被翻译为中文说明"

$detect = $serviceType.GetMethod("DetectPlatformFor", $public)
$platform = $detect.Invoke($null, @("https://www.instagram.com/p/DddbkEfH1JQ/?stkn=MWltd2dnZDkzZjYybg=="))
Write-Host ("  platform -> {0}" -f $platform)
Assert-True ($platform -eq "Instagram") "Instagram 链接被正确识别为 Instagram"

Write-Host "== 登录态加密存储 =="
$configStoreType = $assembly.GetType("LiveBoard.ConfigStore", $true)
$protect = $configStoreType.GetMethod("ProtectSecret", $public)
$unprotect = $configStoreType.GetMethod("UnprotectSecret", $public)
$secret = "# Netscape HTTP Cookie File`r`n.example.com`tTRUE`t/`tTRUE`t0`tsid`t秘密值"
$encrypted = $protect.Invoke($null, @($secret))
Assert-True (-not [string]::IsNullOrWhiteSpace($encrypted)) "登录态可以被 DPAPI 加密"
Assert-True ($encrypted -ne $secret) "存储内容不是明文"
Assert-True ($unprotect.Invoke($null, @($encrypted)) -eq $secret) "登录态可以解密还原"

Write-Host "Cookie source checks passed."

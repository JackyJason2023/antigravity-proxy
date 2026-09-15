# ============================================================
#  Antigravity-Proxy 编译脚本
#  PowerShell Build Script for Windows
# ============================================================
# 使用方法:
#   .\build.ps1              # 默认 Release x64 编译
#   .\build.ps1 -Config Debug
#   .\build.ps1 -Arch x86
#   .\build.ps1 -Config Debug -Arch x86
# ============================================================

[CmdletBinding()]
param(
    [ValidateSet("Release", "Debug")]
    [string]$Config = "Release",
    
    [ValidateSet("x64", "x86")]
    [string]$Arch = "x64",
    
    [switch]$StaticRuntime,
    [switch]$DynamicRuntime,
    [switch]$Clean,
    [switch]$RunTests,
    [switch]$SkipTests,
    [string]$Generator = "",
    [switch]$Help
)

# 某些宿主环境会同时注入大小写不同的 PATH/Path。Windows 本身不区分大小写，
# 但 .NET/MSBuild 枚举进程环境时可能把它们当成重复字典键，导致 MSB6001。
function Normalize-ProcessEnvironment {
    $pathValue = $env:Path
    if ([string]::IsNullOrWhiteSpace($pathValue)) {
        return
    }

    # 先删除两个可能的拼写，再只写回一个键，确保后续工具链看到唯一的 Path。
    Remove-Item -LiteralPath Env:PATH -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath Env:Path -ErrorAction SilentlyContinue
    $env:Path = $pathValue
}

Normalize-ProcessEnvironment

# ============================================================
# 版本信息 (在此处统一管理版本号)
# ============================================================
$Version = "2.4"

# ============================================================
# 辅助函数
# ============================================================

function Write-Header {
    param([string]$Message)
    Write-Host ""
    Write-Host "============================================================" -ForegroundColor Cyan
    Write-Host "  $Message" -ForegroundColor Cyan
    Write-Host "============================================================" -ForegroundColor Cyan
}

function Write-Step {
    param([string]$Message)
    Write-Host "[*] $Message" -ForegroundColor Yellow
}

function Write-Success {
    param([string]$Message)
    Write-Host "[✓] $Message" -ForegroundColor Green
}

function Write-Error {
    param([string]$Message)
    Write-Host "[✗] $Message" -ForegroundColor Red
}

function Show-Help {
    Write-Host @"
Antigravity-Proxy 编译脚本

用法:
    .\build.ps1 [参数]

参数:
    -Config <Release|Debug>  编译配置 (默认: Release)
    -Arch   <x64|x86>        目标架构 (默认: x64)
    -StaticRuntime           使用静态运行库 (/MT) (默认启用)
    -DynamicRuntime          使用动态运行库 (/MD)
    -Clean                   清理后重新编译
    -RunTests                构建并运行 CTest 回归测试
    -SkipTests               显式跳过测试步骤（默认行为）
    -Generator <名称>        覆盖 CMake 生成器；默认自动选择可用的 Visual Studio
    -Verbose                 输出详细构建日志（PowerShell 通用参数）
    -Help                    显示帮助信息

示例:
    .\build.ps1                      # Release x64 编译
    .\build.ps1 -Config Debug        # Debug x64 编译
    .\build.ps1 -Arch x86            # Release x86 编译
    .\build.ps1 -DynamicRuntime      # 使用动态运行库编译
    .\build.ps1 -Clean -Config Debug # 清理后 Debug 编译
    .\build.ps1 -RunTests            # 编译并运行 CTest
    .\build.ps1 -Verbose             # 显示详细编译输出
"@
}

function Resolve-CMakeGenerator {
    param(
        [Parameter(Mandatory = $false)]
        [string]$RequestedGenerator
    )

    if (-not [string]::IsNullOrWhiteSpace($RequestedGenerator)) {
        return $RequestedGenerator.Trim()
    }

    $vsWhereCandidates = @()
    if ($env:ProgramFiles) {
        $vsWhereCandidates += Join-Path $env:ProgramFiles "Microsoft Visual Studio\Installer\vswhere.exe"
    }
    if (${env:ProgramFiles(x86)}) {
        $vsWhereCandidates += Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
    }
    $vsWhere = $vsWhereCandidates |
        Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
        Select-Object -First 1

    if (-not $vsWhere) {
        throw "未找到 vswhere.exe，无法自动定位 Visual Studio。请安装带 C++ 工具的 Visual Studio，或通过 -Generator 手动指定 CMake 生成器。"
    }

    $installations = @()
    try {
        $vsJson = & $vsWhere -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -format json 2>$null
        if ($LASTEXITCODE -eq 0 -and $vsJson) {
            $installations = @($vsJson | ConvertFrom-Json)
        }
    } catch {
        $installations = @()
    }

    $generatorMap = @{
        18 = "Visual Studio 18 2026"
        17 = "Visual Studio 17 2022"
        16 = "Visual Studio 16 2019"
        15 = "Visual Studio 15 2017"
    }
    $cmakeCommand = Get-Command cmake -ErrorAction SilentlyContinue
    if (-not $cmakeCommand) {
        throw "CMake 未找到，请确保 CMake 已安装并添加到 PATH"
    }
    $cmakeHelp = (& $cmakeCommand.Source --help 2>$null | Out-String)
    $available = @{}
    foreach ($installation in $installations) {
        $versionText = [string]$installation.installationVersion
        if ($versionText -notmatch '^([0-9]+)\.') { continue }
        $major = [int]$Matches[1]
        if ($generatorMap.ContainsKey($major)) {
            $candidate = $generatorMap[$major]
            if ($cmakeHelp.Contains($candidate)) {
                $available[$major] = $candidate
            }
        }
    }

    if ($available.Count -eq 0) {
        $installedVersions = [string[]]($installations | ForEach-Object { $_.installationVersion })
        throw "未找到 CMake 支持的 Visual Studio C++ 工具链。可用 Visual Studio 版本：$($installedVersions -join ', ')"
    }

    return ($available.Keys | Sort-Object -Descending | Select-Object -First 1 | ForEach-Object { $available[$_] })
}

# ============================================================
# 主逻辑
# ============================================================

if ($Help) {
    Show-Help
    exit 0
}

if ($RunTests -and $SkipTests) {
    Write-Error "RunTests 与 SkipTests 参数互斥"
    exit 1
}

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$BuildDir = Join-Path $ScriptDir "build-$Arch"
$OutputDir = Join-Path $ScriptDir "output"

# 默认启用静态运行库，降低运行库缺失导致的启动失败风险
$UseStaticRuntime = $true
if ($DynamicRuntime) { $UseStaticRuntime = $false }
elseif ($StaticRuntime) { $UseStaticRuntime = $true }
$RuntimeLabel = if ($UseStaticRuntime) { "静态(/MT)" } else { "动态(/MD)" }
$InstallerProject = Join-Path $ScriptDir "tools\AntigravityProxyInstaller\AntigravityProxyInstaller.csproj"

Write-Header "Antigravity-Proxy 编译开始"
Write-Host "  配置: $Config" -ForegroundColor White
Write-Host "  架构: $Arch" -ForegroundColor White
Write-Host "  运行库: $RuntimeLabel" -ForegroundColor White
Write-Host "  CMake 生成器: 自动检测" -ForegroundColor White
Write-Host "  构建目录: $BuildDir" -ForegroundColor White
Write-Host "  详细输出: $(if ($PSBoundParameters.ContainsKey('Verbose')) { '开启' } else { '关闭' })" -ForegroundColor White
Write-Host ""

# ============================================================
# 步骤 1: 检查依赖
# ============================================================

Write-Step "检查依赖项..."

# 检查 CMake
$cmake = Get-Command cmake -ErrorAction SilentlyContinue
if (-not $cmake) {
    Write-Error "CMake 未找到，请确保 CMake 已安装并添加到 PATH"
    exit 1
}
Write-Success "CMake 已找到: $($cmake.Source)"

$CMakeGenerator = Resolve-CMakeGenerator -RequestedGenerator $Generator
Write-Success "CMake 生成器: $CMakeGenerator"

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) {
    Write-Error "未找到 .NET SDK，无法编译桌面部署工具"
    exit 1
}
if (-not (Test-Path -LiteralPath $InstallerProject -PathType Leaf)) {
    Write-Error "未找到部署工具项目: $InstallerProject"
    exit 1
}
Write-Success ".NET SDK 已找到: $($dotnet.Source)"

# 检查 nlohmann/json
$jsonHeader = Join-Path $ScriptDir "include\nlohmann\json.hpp"
if (-not (Test-Path $jsonHeader)) {
    Write-Step "下载 nlohmann/json (单头文件)..."
    $nlohmannDir = Join-Path $ScriptDir "include\nlohmann"
    if (-not (Test-Path $nlohmannDir)) {
        New-Item -ItemType Directory -Path $nlohmannDir -Force | Out-Null
    }
    try {
        Invoke-WebRequest -Uri "https://raw.githubusercontent.com/nlohmann/json/develop/single_include/nlohmann/json.hpp" -OutFile $jsonHeader
        Write-Success "nlohmann/json 下载完成"
    } catch {
        Write-Error "下载失败: $_"
        Write-Host "请手动下载 json.hpp 到 include/nlohmann/ 目录" -ForegroundColor Yellow
        exit 1
    }
} else {
    Write-Success "nlohmann/json 已存在"
}

# ============================================================
# 步骤 2: 清理 (可选)
# ============================================================

if ($Clean -and (Test-Path $BuildDir)) {
    Write-Step "清理构建目录..."
    Remove-Item -Recurse -Force $BuildDir
    Write-Success "构建目录已清理"
}

# ============================================================
# 步骤 3: 创建构建目录
# ============================================================

if (-not (Test-Path $BuildDir)) {
    Write-Step "构建目录将在 CMake 配置时自动创建..."
}

# ============================================================
# 步骤 4: CMake 配置
# ============================================================

Write-Step "运行 CMake 配置..."

$cmakeArch = if ($Arch -eq "x64") { "x64" } else { "Win32" }

$cmakeArgs = @(
    "-S", $ScriptDir,
    "-B", $BuildDir,
    "-G", $CMakeGenerator,
    "-A", $cmakeArch,
    "-DCMAKE_VS_GLOBALS=TrackFileAccess=false"
)
if ($UseStaticRuntime) {
    $cmakeArgs += "-DSTATIC_RUNTIME=ON"
} else {
    $cmakeArgs += "-DSTATIC_RUNTIME=OFF"
}
# 显式覆盖缓存值，确保 CI 的测试开关不受既有构建目录影响。
$buildTestsValue = if ($RunTests) { "ON" } else { "OFF" }
$cmakeArgs += "-DBUILD_TESTS=$buildTestsValue"

$cmakeResult = & $cmake.Source @cmakeArgs 2>&1
$cmakeFailed = ($LASTEXITCODE -ne 0)

# 处理项目目录迁移或生成器切换后的旧缓存：自动清理并重试一次
if ($cmakeFailed) {
    # 兼容 Windows PowerShell 5.1:
    # - 2>&1 可能返回 ErrorRecord 而非纯字符串
    # - 输出可能按控制台宽度换行，导致关键句子被拆断
    $cmakeText = (($cmakeResult | ForEach-Object { $_.ToString() }) -join "`n")
    $cmakeTextNormalized = [regex]::Replace($cmakeText, "\s+", " ")
    $isCacheMismatch = $cmakeTextNormalized -match "CMakeCache\.txt directory .* is different than the directory" -or
                      $cmakeTextNormalized -match "does not match the source .* used to generate cache"
    $isGeneratorMismatch = $cmakeTextNormalized -match "generator .* does not match the generator used previously" -or
                           $cmakeTextNormalized -match "does not match the generator used previously"

    if ($isCacheMismatch -or $isGeneratorMismatch) {
        Write-Step "检测到 CMake 缓存与当前构建环境不匹配，自动清理构建目录后重试..."
        if (Test-Path $BuildDir) {
            Remove-Item -Recurse -Force $BuildDir
        }
        $cmakeResult = & $cmake.Source @cmakeArgs 2>&1
        $cmakeFailed = ($LASTEXITCODE -ne 0)
    }
}

if ($cmakeFailed) {
    Write-Error "CMake 配置失败"
    Write-Host $cmakeResult -ForegroundColor Red
    exit 1
}
Write-Success "CMake 配置完成"

# ============================================================
# 步骤 5: 编译
# ============================================================

Write-Step "开始编译 ($Config $Arch)..."

$buildArgs = @("--build", $BuildDir, "--config", $Config)
if ($PSBoundParameters.ContainsKey('Verbose')) {
    $buildArgs += "--verbose"
}
$buildArgs += "--"
$buildArgs += "/p:TrackFileAccess=false"
$buildArgs += "/nr:false"
$buildResult = & $cmake.Source @buildArgs 2>&1
if ($PSBoundParameters.ContainsKey('Verbose')) {
    $buildResult | ForEach-Object { Write-Host $_ }
}
if ($LASTEXITCODE -ne 0) {
    Write-Error "编译失败"
    Write-Host $buildResult -ForegroundColor Red
    exit 1
}
Write-Success "编译完成"

# ============================================================
# 步骤 5.5: 可选 CTest 回归
# ============================================================

if ($RunTests) {
    Write-Step "运行 CTest 回归测试..."
    Push-Location $BuildDir
    try {
        $ctestCommand = Get-Command ctest -ErrorAction SilentlyContinue
        if (-not $ctestCommand) {
            Write-Error "CTest 未找到"
            exit 1
        }
        $ctestResult = & $ctestCommand.Source -C $Config --output-on-failure 2>&1
        $ctestFailed = ($LASTEXITCODE -ne 0)
        $ctestResult | ForEach-Object { Write-Host $_ }
        if ($ctestFailed) {
            Write-Error "CTest 回归失败"
            exit 1
        }
        Write-Success "CTest 回归通过"
    } finally {
        Pop-Location
    }
} elseif ($SkipTests) {
    Write-Step "已按参数跳过测试步骤 (-SkipTests)"
} else {
    Write-Step "默认跳过测试步骤（使用 -RunTests 可构建并运行 CTest）"
}

# ============================================================
# 步骤 6: 查找输出文件
# ============================================================

Write-Step "查找编译产物..."

$dllPattern = if ($Config -eq "Debug") { "version*.dll" } else { "version.dll" }
$dllPath = Get-ChildItem -Path $BuildDir -Recurse -Filter $dllPattern | Select-Object -First 1
$dbghelpPattern = if ($Config -eq "Debug") { "dbghelp*.dll" } else { "dbghelp.dll" }
$dbghelpPath = Get-ChildItem -Path $BuildDir -Recurse -Filter $dbghelpPattern | Select-Object -First 1

if (-not $dllPath) {
    Write-Error "未找到编译产物 DLL"
    exit 1
}
if (-not $dbghelpPath) {
    Write-Error "未找到 Antigravity CLI dbghelp.dll 劫持产物"
    exit 1
}

Write-Success "找到 DLL: $($dllPath.FullName)"
Write-Success "找到 CLI dbghelp.dll: $($dbghelpPath.FullName)"

# ============================================================
# 步骤 7: 创建输出目录并复制文件
# ============================================================

Write-Step "创建输出目录..."

if (-not (Test-Path $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir | Out-Null
}

$IdeOutputDir = Join-Path $OutputDir "ide"
$CliOutputDir = Join-Path $OutputDir "cli"

# 每次重建两个部署目录，避免旧版根目录 DLL 残留后继续诱导混装。
foreach ($deploymentDir in @($IdeOutputDir, $CliOutputDir)) {
    if (Test-Path -LiteralPath $deploymentDir) {
        Remove-Item -LiteralPath $deploymentDir -Recurse -Force
    }
    New-Item -ItemType Directory -Path $deploymentDir | Out-Null
}
Get-ChildItem -LiteralPath $OutputDir -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match '^(version|dbghelp|antigravity_proxy).*\.dll$' -or $_.Name -eq 'config.json' } |
    Remove-Item -Force

Copy-Item $dllPath.FullName -Destination (Join-Path $IdeOutputDir "version.dll") -Force
Write-Success "IDE 代理 DLL 已复制到 output\ide"

# CLI 主体使用唯一名称，避免与系统 version.dll 的已加载模块发生冲突。
Copy-Item $dllPath.FullName -Destination (Join-Path $CliOutputDir "antigravity_proxy.dll") -Force
Copy-Item $dbghelpPath.FullName -Destination (Join-Path $CliOutputDir "dbghelp.dll") -Force
Write-Success "CLI shim 与代理主体已复制到 output\cli"

# ============================================================
# 步骤 8: 生成配置文件
# ============================================================

Write-Step "生成配置文件..."

$configJson = @{
    "_comment" = "Antigravity-Proxy 配置文件"
    "_version" = $Version
    "_build" = @{
        "date" = (Get-Date -Format "yyyy-MM-dd HH:mm:ss")
        "config" = $Config
        "arch" = $Arch
    }
    # 日志等级：默认 info（克制日志输出）；排障时可改为 debug 以获得更详细信息
    log_level = "info"
    proxy = @{
        host = "127.0.0.1"
        port = 7890
        type = "socks5"
    }
    fake_ip = @{
        enabled = $true
        cidr = "198.18.0.0/15"
    }
    timeout = @{
        connect = 5000
        send = 5000
        recv = 5000
    }
    # 更新检查默认关闭；启用后仅异步检查 GitHub Release 并提示打开下载页，不自动下载文件
    updates = @{
        enabled = $false
        check_delay_ms = 15000
        timeout_ms = 5000
        notify_once = $true
        allow_insecure_mirrors = $true
        mirrors = @(
            "https://wget.la/",
            "https://rapidgit.jjda.de5.net/",
            "https://fastgit.cc/",
            "https://gitproxy.mrhjx.cn/",
            "https://github.boki.moe/",
            "https://github.ednovas.xyz/"
        )
    }
    traffic_logging = $false
    # 地域/资格排障时可显式开启；默认不访问外部 IP 查询服务
    diagnostics = @{
        agent_ip_probe = $false
    }
    child_injection = $true
    # 子进程注入模式: filtered(按target_processes过滤) / inherit(注入所有子进程)
    child_injection_mode = "filtered"
    # 子进程注入排除列表（大小写不敏感，支持子串匹配）
    child_injection_exclude = @()
    # 目标进程列表（空数组=注入所有子进程）
    # 兼容 Antigravity 2.0 新增的 language_server.exe，同时覆盖 Antigravity CLI 的 agy.exe。
    target_processes = @("agy.exe", "language_server.exe", "language_server_windows", "Antigravity.exe", "Antigravity IDE.exe", "node.exe")
    proxy_rules = @{
        # 端口白名单: 仅代理 HTTP(80) 和 HTTPS(443)，空数组=代理所有端口
        allowed_ports = @(80, 443)
        dns_mode = "direct"
        ipv6_mode = "proxy"
        # UDP策略: auto(SOCKS5自动代理) / block(阻断) / direct(直连) / proxy(强制SOCKS5代理)
        udp_mode = "auto"
        # UDP代理失败或auto遇到HTTP代理时的策略: block(阻断) / direct(回退直连)
        udp_fallback = "block"
        # 高级路由规则（内网自动直连，无需手动配置）
        routing = @{
            enabled = $true
            priority_mode = "order"
            default_action = "proxy"
            use_default_private = $true
            rules = @()
        }
    }
} | ConvertTo-Json -Depth 5

$configPaths = @(
    (Join-Path $IdeOutputDir "config.json"),
    (Join-Path $CliOutputDir "config.json")
)
foreach ($configPath in $configPaths) {
    $configJson | Out-File -FilePath $configPath -Encoding UTF8
    Write-Success "配置文件已生成: $configPath"
}

# ============================================================
# 步骤 9: 编译桌面部署工具
# ============================================================

Write-Step "编译桌面部署工具..."

$installerRuntime = if ($Arch -eq "x86") { "win-x86" } else { "win-x64" }
$installerPublishDir = Join-Path $BuildDir "installer-publish-$Arch"
if (Test-Path -LiteralPath $installerPublishDir) {
    Remove-Item -LiteralPath $installerPublishDir -Recurse -Force
}
New-Item -ItemType Directory -Path $installerPublishDir -Force | Out-Null

$installerRestoreArgs = @(
    "restore",
    $InstallerProject,
    "--runtime", $installerRuntime,
    "--ignore-failed-sources",
    "--nologo",
    "-p:NuGetAudit=false"
)
$installerRestoreResult = & $dotnet.Source @installerRestoreArgs 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Error "桌面部署工具依赖还原失败"
    Write-Host $installerRestoreResult -ForegroundColor Red
    exit 1
}

$installerArgs = @(
    "publish",
    $InstallerProject,
    "--configuration", $Config,
    "--runtime", $installerRuntime,
    "--self-contained", "true",
    "--output", $installerPublishDir,
    "--nologo",
    "-p:PublishSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:EnableCompressionInSingleFile=true",
    "-p:DebugType=None",
    "--no-restore"
)
$installerBuildResult = & $dotnet.Source @installerArgs 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Error "桌面部署工具编译失败"
    Write-Host $installerBuildResult -ForegroundColor Red
    exit 1
}

$installerExe = Join-Path $installerPublishDir "AntigravityProxyInstaller.exe"
if (-not (Test-Path -LiteralPath $installerExe -PathType Leaf)) {
    Write-Error "未找到桌面部署工具产物: $installerExe"
    exit 1
}
Copy-Item -LiteralPath $installerExe -Destination (Join-Path $IdeOutputDir "AntigravityProxyInstaller.exe") -Force
Write-Success "桌面部署工具已复制到 output\ide"

# ============================================================
# 步骤 10: 生成使用说明
# ============================================================

Write-Step "生成使用说明..."

$usageDoc = @'
# Antigravity-Proxy 使用说明

## 概述
Antigravity-Proxy 是一个基于 MinHook 的 Windows DLL 代理注入工具。
通过劫持 version.dll，可以透明地将目标进程的网络流量重定向到代理服务器。

## 先看这个：对话报错先排 IP

> **如果 Antigravity 对话时报 `Agent execution terminated due to error`，请先排查代理出口 IP，不要先默认怀疑 DLL 没生效。**

最容易误判的真实场景是：
- DLL 已加载成功
- `language_server_windows_x64.exe` 已注入成功
- `node.exe` 已注入成功
- `oauth2.googleapis.com` / `daily-cloudcode-pa.googleapis.com` 仍能通过 SOCKS5 正常连通
- 但 `%APPDATA%\Antigravity\logs\<最新目录>\ls-main.log` 返回：

```
FAILED_PRECONDITION (code 400): User location is not supported for the API use.
```

这时主因通常不是 DLL，而是：
- 当前代理出口 IP 的国家/ASN/机房属性，被 Antigravity agent mode / Gemini CLI 路径判定为不可用
- 也就是说：**国家支持不等于当前这条 agent 执行链路一定接受这条出口 IP**

设置 `diagnostics.agent_ip_probe=true` 后，DLL 会额外输出诊断日志：
- `[诊断/IP] 当前代理出口探测完成: ...`
- `[诊断/IP] 当前代理出口呈现机房/托管特征...`
- `[诊断/IP] 最新 Antigravity 日志已命中 location 限制错误，同时当前代理出口呈现机房/托管特征...`

建议的排查顺序：
1. 先看 `proxy-YYYYMMDD.log`，确认注入和 SOCKS5 是否成功
2. 再看 `%APPDATA%\Antigravity\logs\<最新目录>\ls-main.log`，确认是否命中 `User location is not supported for the API use.`
3. 如果命中，优先更换**非机房 / 非托管 / 普通 ISP / 住宅**出口，再重试
4. 只有 DLL 日志里根本没有注入成功、或根本没有代理握手成功时，才回头排 DLL

## 快速开始

### 1. 选择部署目录
- Antigravity 桌面端/IDE：只复制 `output/ide` 内的 `version.dll` 与 `config.json`。
- Antigravity CLI：只复制 `output/cli` 内的 `dbghelp.dll`、`antigravity_proxy.dll` 与 `config.json`。

两个目录不得混合复制。CLI shim 会延迟加载唯一名称的 `antigravity_proxy.dll`，桌面端不需要 `dbghelp.dll`。

### 2. 配置代理
编辑 `config.json`，设置代理服务器地址：
``````jsonc
{
    "proxy": {
        "host": "127.0.0.1",       // 代理服务器地址
        "port": 7890,              // 代理服务器端口
        "type": "socks5"           // 代理类型: socks5 或 http
    },
    "log_level": "info",           // 日志等级: debug/info/warn/error (默认 info)
    "fake_ip": {
        "enabled": true,           // 是否启用 FakeIP 系统 (拦截 DNS 解析)
        "cidr": "198.18.0.0/15"    // FakeIP 分配的虚拟 IP 地址范围 (默认为基准测试保留网段)
    },
    "timeout": {
        "connect": 5000,           // 连接超时 (毫秒)
        "send": 5000,              // 发送超时 (毫秒)
        "recv": 5000               // 接收超时 (毫秒)
    },
    "traffic_logging": false,      // 是否记录流量日志 (调试用)
    "diagnostics": {
        "agent_ip_probe": false    // 开启后探测代理出口 IP，并关联 location 日志
    },
    "child_injection": true,       // 是否自动注入子进程
    "child_injection_mode": "filtered",  // 子进程注入模式: filtered(按target_processes过滤) / inherit(注入所有)
    "child_injection_exclude": [],       // 子进程注入排除列表 (大小写不敏感，支持子串匹配)
    "target_processes": [],        // 目标进程列表 (空数组=注入所有子进程)
    "proxy_rules": {
        "allowed_ports": [80, 443],  // 端口白名单: 仅代理 HTTP/HTTPS，空数组=代理所有端口
        "dns_mode": "direct",        // DNS策略: direct(直连) 或 proxy(走代理)
        "ipv6_mode": "proxy",        // IPv6策略: proxy(走代理) / direct(直连) / block(阻止)
        "udp_mode": "auto",          // UDP策略: auto(SOCKS5自动代理) / block(阻断) / direct(直连) / proxy(强制SOCKS5代理)
        "udp_fallback": "block",     // UDP代理失败或auto遇到HTTP代理时: block(阻断) / direct(回退直连)
        "routing": {                 // 高级路由规则 (内网自动直连，一般无需配置)
            "enabled": true,
            "priority_mode": "order",
            "default_action": "proxy",
            "use_default_private": true,
            "rules": []
        }
    }
}
``````

#### 常用代理软件端口参考

| 代理软件 | SOCKS5 端口 | HTTP 端口 | 混合端口 | 说明 |
|----------|-------------|-----------|----------|------|
| Clash / Clash Verge | 7891 | 7890 | 7890 | 混合端口同时支持 SOCKS5 和 HTTP |
| Clash for Windows | 7891 | 7890 | 7890 | 设置 → Ports 查看 |
| Mihomo (Clash Meta) | 7891 | 7890 | 7890 | 配置同 Clash |
| V2RayN | 10808 | 10809 | - | 设置 → Core 基础设置 |
| Shadowsocks | 1080 | - | - | 仅 SOCKS5 |
| Surge | 6153 | 6152 | - | Mac/iOS |
| Qv2ray | 1089 | 8889 | - | 首选项 → 入站设置 |

> **提示**: 推荐使用 SOCKS5 协议，本工具对其支持更完善。

#### 如何确认端口是否开启？
```powershell
# PowerShell 测试端口
Test-NetConnection -ComputerName 127.0.0.1 -Port 7890
```

### 3. 一键部署工具
IDE 发布目录中包含 `AntigravityProxyInstaller.exe`。双击打开后，将桌面或开始菜单中的 Antigravity 快捷方式拖入窗口，工具会自动识别执行文件和目标目录，并检查 `version.dll` 与 `config.json`。检查通过后点击“复制缺少的文件”即可部署。工具默认不覆盖已有文件；覆盖前会自动备份。部署前请完全退出 Antigravity。

### 4. 启动目标程序
直接启动目标程序，DLL 会自动加载并重定向网络流量。

## 配置文件说明

| 配置项 | 说明 | 默认值 |
|--------|------|--------|
| log_level | 日志等级 (debug/info/warn/error) | info |
| proxy.host | 代理服务器地址 | 127.0.0.1 |
| proxy.port | 代理服务器端口 | 7890 |
| proxy.type | 代理类型 (socks5/http) | socks5 |
| fake_ip.enabled | 是否启用 FakeIP 系统 | true |
| fake_ip.cidr | 虚拟 IP 地址范围 | 198.18.0.0/15 |
| timeout.connect | 连接超时 (毫秒) | 5000 |
| timeout.send | 发送超时 (毫秒) | 5000 |
| timeout.recv | 接收超时 (毫秒) | 5000 |
| traffic_logging | 是否记录流量日志 | false |
| diagnostics.agent_ip_probe | 是否启用出口 IP/location 联合诊断 | false |
| child_injection | 是否注入子进程 | true |
| child_injection_mode | 子进程注入模式 (filtered/inherit) | filtered |
| child_injection_exclude | 子进程注入排除列表 | [] |
| target_processes | 目标进程列表 (空=全部) | [] |
| proxy_rules.allowed_ports | 端口白名单 (空=全部代理) | [80, 443] |
| proxy_rules.dns_mode | DNS策略 (direct/proxy) | direct |
| proxy_rules.ipv6_mode | IPv6策略 (proxy/direct/block) | proxy |
| proxy_rules.udp_mode | UDP策略 (auto/block/direct/proxy) | auto |
| proxy_rules.udp_fallback | UDP代理失败降级策略 (block/direct) | block |
| proxy_rules.routing.enabled | 是否启用路由分流 | true |
| proxy_rules.routing.priority_mode | 规则优先级模式 (order/number) | order |
| proxy_rules.routing.default_action | 默认动作 (proxy/direct) | proxy |
| proxy_rules.routing.use_default_private | 是否自动添加内网直连规则 | true |
| proxy_rules.routing.rules | 自定义路由规则列表 | [] |

## v1.1.0 更新说明

### 新增功能
1. **目标进程过滤**: 可配置 `target_processes` 数组，仅对指定进程注入 DLL
2. **回环地址 bypass**: `127.0.0.1`、`localhost` 等本地地址不再走代理
3. **日志中文化**: 所有日志已统一为中文输出
4. **智能路由规则**: 新增 `proxy_rules` 配置，支持端口白名单、DNS/IPv6/UDP 策略
   - `allowed_ports`: 仅指定端口走代理，其他直连
   - `dns_mode`: DNS (53端口) 可选直连或走代理
   - `ipv6_mode`: IPv6 可选走代理/直连/阻止
   - `udp_mode`: 默认 auto；SOCKS5 自动使用 UDP Associate，HTTP 代理按 udp_fallback 处理
   - `udp_fallback`: UDP 代理失败或 auto 遇到非 SOCKS5 代理时的策略，默认阻断以防止流量泄漏

### 配置示例
```json
{
    "target_processes": ["agy.exe", "language_server.exe", "language_server_windows", "Antigravity.exe", "Antigravity IDE.exe", "node.exe"],
    "child_injection_mode": "filtered",
    "child_injection_exclude": ["unwanted_process.exe"]
}
```
- `target_processes` 为空数组或不存在时，注入所有子进程(原行为)
- `child_injection_mode="inherit"` 时注入所有子进程，可用 `child_injection_exclude` 排除特定进程

## 日志文件
DLL 运行时会在当前目录生成 `proxy.log` 日志文件，用于调试。

## WSL 环境说明

> ⚠️ **重要提示**：Antigravity-Proxy (version.dll 劫持方案) **无法代理 WSL 内部的流量**。

这是由技术架构决定的根本性限制：
- DLL 注入只能 Hook Windows PE 进程
- WSL2 运行真正的 Linux 内核，使用 Linux socket() 系统调用
- 即使注入 wsl.exe，也无法 Hook WSL 内部的 language_server_linux_x64

### WSL 替代方案

**方案一：使用 antissh 工具（推荐）**
```bash
# 在 WSL 中执行
curl -O https://raw.githubusercontent.com/ccpopy/antissh/main/antissh.sh
chmod +x antissh.sh && bash ./antissh.sh
```
项目地址：https://github.com/ccpopy/antissh

**方案二：WSL Mirrored 网络模式**
1. 创建 %USERPROFILE%\.wslconfig 文件，内容如下：
```ini
[wsl2]
networkingMode=mirrored
```
2. 执行 `wsl --shutdown` 重启 WSL
3. 在 WSL 中设置环境变量：
```bash
export ALL_PROXY=socks5://127.0.0.1:7890
```
要求：Windows 11 22H2+，WSL 2.0+

**方案三：TUN 模式全局代理**
在 Clash/Mihomo 中开启 TUN 模式，实现全局透明代理。

## 常见问题

### Q: DLL 加载失败？
A: 确保使用正确的架构版本 (x64 程序用 x64 DLL，x86 程序用 x86 DLL)。

### Q: 网络连接失败？
A: 检查代理服务器是否正常运行，且端口配置正确。

### Q: 对话时报 `Agent execution terminated due to error`，但 DLL 日志看起来都正常？
A: 先去看 `%APPDATA%\Antigravity\logs\<最新目录>\ls-main.log`。如果里面出现 `User location is not supported for the API use.`，优先排查代理出口 IP / ASN / 机房属性，而不是继续怀疑 DLL 失效。

### Q: 如何验证 DLL 是否生效？
A: 检查目标程序目录是否生成 `proxy.log` 文件。

### Q: WSL 中的程序不走代理？
A: 这是技术限制，请参考上述"WSL 环境说明"使用替代方案。

## 编译信息
- 编译时间: $(Get-Date -Format "yyyy-MM-dd HH:mm:ss")
- 编译配置: $Config
- 目标架构: $Arch
- 编译版本: $Version
- 开发环境: Windows 11
- 开发者: 煎饼果子@86

---
GitHub: https://github.com/yuaotian/antigravity-proxy
关注公众号「煎饼果子卷AI」获取最新动态
'@

$usagePath = Join-Path $OutputDir "使用说明.md"
$usageDoc | Out-File -FilePath $usagePath -Encoding UTF8
Write-Success "使用说明已生成: $usagePath"

# ============================================================
# 步骤 11: 复制配置工具
# ============================================================

Write-Step "复制配置工具..."
$configWebSrc = Join-Path $PSScriptRoot "resources\config-web\index.html"
if (Test-Path $configWebSrc) {
    Copy-Item $configWebSrc -Destination (Join-Path $OutputDir "config-web.html") -Force
    Write-Success "配置工具已复制到 output 目录"
} else {
    Write-Warning "配置工具源文件不存在: $configWebSrc"
}

# ============================================================
# 完成
# ============================================================

Write-Header "编译完成!"
Write-Host ""
Write-Host "输出目录: $OutputDir" -ForegroundColor Green
Write-Host ""
Write-Host "生成的文件:" -ForegroundColor White
Get-ChildItem $OutputDir -Recurse -File | ForEach-Object {
    $relativePath = $_.FullName.Substring($OutputDir.Length).TrimStart('\')
    Write-Host "  - $relativePath" -ForegroundColor Gray
}
Write-Host ""
Write-Host "下一步: 双击 output\ide\AntigravityProxyInstaller.exe 部署桌面端；CLI 复制 output\cli。请勿混装两个目录。" -ForegroundColor Yellow
Write-Host ""

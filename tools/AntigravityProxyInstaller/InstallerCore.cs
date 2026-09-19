using System.Diagnostics;
using System.IO.Compression;
using Microsoft.Win32;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security;
using System.Text.Json;

namespace AntigravityProxyInstaller;

internal enum PeArchitecture
{
    Unknown,
    X86,
    X64,
    Arm64
}

internal enum PayloadOrigin
{
    /// <summary>A folder the tool discovered next to itself, for example output\ide.</summary>
    Bundled,
    /// <summary>A GitHub latest build cached in %LOCALAPPDATA%.</summary>
    Remote,
    /// <summary>A folder the user picked manually.</summary>
    Local
}

internal sealed class PayloadInfo
{
    public PayloadInfo(
        string directoryPath,
        string versionDllPath,
        string configPath,
        PeArchitecture architecture,
        PayloadOrigin origin = PayloadOrigin.Local)
    {
        DirectoryPath = directoryPath;
        VersionDllPath = versionDllPath;
        ConfigPath = configPath;
        Architecture = architecture;
        Origin = origin;
    }

    public string DirectoryPath { get; }
    public string VersionDllPath { get; }
    public string ConfigPath { get; }
    public PeArchitecture Architecture { get; }
    public PayloadOrigin Origin { get; }

    public string OriginDisplay => Origin switch
    {
        PayloadOrigin.Remote => "GitHub 最新构建",
        PayloadOrigin.Bundled => "随工具附带的构建",
        _ => "手动选择的文件夹"
    };
}

internal sealed class TargetInfo
{
    public TargetInfo(string shortcutPath, string executablePath, string directoryPath, PeArchitecture architecture)
    {
        ShortcutPath = shortcutPath;
        ExecutablePath = executablePath;
        DirectoryPath = directoryPath;
        Architecture = architecture;
    }

    public string ShortcutPath { get; }
    public string ExecutablePath { get; }
    public string DirectoryPath { get; }
    public PeArchitecture Architecture { get; }
}

internal sealed class FileInspection
{
    public FileInspection(string name, string targetPath, bool exists, bool readable, bool hashMatches, string? error)
    {
        Name = name;
        TargetPath = targetPath;
        Exists = exists;
        Readable = readable;
        HashMatches = hashMatches;
        Error = error;
    }

    public string Name { get; }
    public string TargetPath { get; }
    public bool Exists { get; }
    public bool Readable { get; }
    public bool HashMatches { get; }
    public string? Error { get; }

    public string DisplayStatus
    {
        get
        {
            if (!Exists)
            {
                return "缺失";
            }

            if (!Readable)
            {
                return "无法读取";
            }

            return HashMatches ? "已存在，内容一致" : "已存在，内容不同";
        }
    }
}

internal sealed class DeploymentAssessment
{
    public DeploymentAssessment(
        PayloadInfo payload,
        TargetInfo target,
        FileInspection versionDll,
        FileInspection config,
        bool architectureMatches,
        string architectureStatus)
    {
        Payload = payload;
        Target = target;
        VersionDll = versionDll;
        Config = config;
        ArchitectureMatches = architectureMatches;
        ArchitectureStatus = architectureStatus;
    }

    public PayloadInfo Payload { get; }
    public TargetInfo Target { get; }
    public FileInspection VersionDll { get; }
    public FileInspection Config { get; }
    public bool ArchitectureMatches { get; }
    public string ArchitectureStatus { get; }

    public bool HasMissingFiles => !VersionDll.Exists || !Config.Exists;
    public bool HasDifferentFiles => (VersionDll.Exists && !VersionDll.HashMatches) ||
                                     (Config.Exists && !Config.HashMatches);
    public bool HasUnreadableFiles => (VersionDll.Exists && !VersionDll.Readable) ||
                                      (Config.Exists && !Config.Readable);
    public bool IsReady => ArchitectureMatches && !HasUnreadableFiles;

    public bool HasWork(bool overwriteExisting)
    {
        if (!IsReady)
        {
            return false;
        }

        return HasMissingFiles || (overwriteExisting && HasDifferentFiles);
    }
}

internal static class InstalledApplicationLocator
{
    private static readonly string[] SupportedExecutableNames =
    {
        "Antigravity.exe",
        "Antigravity IDE.exe"
    };

    private static readonly (RegistryHive Hive, RegistryView View, string Path)[] UninstallRoots =
    {
        (RegistryHive.CurrentUser, RegistryView.Default, @"Software\Microsoft\Windows\CurrentVersion\Uninstall"),
        (RegistryHive.CurrentUser, RegistryView.Registry64, @"Software\Microsoft\Windows\CurrentVersion\Uninstall"),
        (RegistryHive.CurrentUser, RegistryView.Registry32, @"Software\Microsoft\Windows\CurrentVersion\Uninstall"),
        (RegistryHive.LocalMachine, RegistryView.Registry64, @"Software\Microsoft\Windows\CurrentVersion\Uninstall"),
        (RegistryHive.LocalMachine, RegistryView.Registry32, @"Software\Microsoft\Windows\CurrentVersion\Uninstall")
    };

    public static bool TryFindDefault(out TargetInfo? target)
    {
        target = null;
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var executable in EnumerateRegistryCandidates())
        {
            AddCandidate(candidates, executable);
        }

        foreach (var directory in EnumerateKnownDirectories())
        {
            foreach (var executable in EnumerateExecutables(directory))
            {
                AddCandidate(candidates, executable);
            }
        }

        foreach (var executable in candidates)
        {
            if (!File.Exists(executable))
            {
                continue;
            }

            var directory = Path.GetDirectoryName(executable);
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            target = new TargetInfo(
                executable,
                executable,
                directory,
                PeArchitectureReader.Read(executable));
            return true;
        }

        return false;
    }

    private static IEnumerable<string> EnumerateRegistryCandidates()
    {
        var results = new List<string>();
        foreach (var root in UninstallRoots)
        {
            RegistryKey? baseKey = null;
            RegistryKey? uninstallKey = null;
            try
            {
                baseKey = RegistryKey.OpenBaseKey(root.Hive, root.View);
                uninstallKey = baseKey.OpenSubKey(root.Path);
                if (uninstallKey is null)
                {
                    continue;
                }

                foreach (var subKeyName in uninstallKey.GetSubKeyNames())
                {
                    using var appKey = uninstallKey.OpenSubKey(subKeyName);
                    if (appKey is null)
                    {
                        continue;
                    }

                    var displayName = appKey.GetValue("DisplayName") as string;
                    if (string.IsNullOrWhiteSpace(displayName) ||
                        displayName.IndexOf("Antigravity", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }

                    foreach (var valueName in new[] { "InstallLocation", "DisplayIcon", "InstallSource" })
                    {
                        if (appKey.GetValue(valueName) is string value && !string.IsNullOrWhiteSpace(value))
                        {
                            foreach (var executable in ExpandCandidate(value))
                            {
                                results.Add(executable);
                            }
                        }
                    }
                }
            }
            catch (SecurityException)
            {
                // Some machine-wide uninstall keys can be unreadable without elevation.
            }
            catch (UnauthorizedAccessException)
            {
                // Continue with the remaining registry views and known locations.
            }
            catch (PlatformNotSupportedException)
            {
                // A registry view may be unavailable on 32-bit Windows.
            }
            catch (ArgumentException)
            {
                // Ignore malformed or unsupported registry roots.
            }
            finally
            {
                uninstallKey?.Dispose();
                baseKey?.Dispose();
            }
        }

        return results;
    }

    private static IEnumerable<string> EnumerateKnownDirectories()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        foreach (var root in new[] { localAppData, programFiles, programFilesX86 })
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            yield return Path.Combine(root, "Programs", "Antigravity IDE");
            yield return Path.Combine(root, "Programs", "Antigravity");
            yield return Path.Combine(root, "Antigravity IDE");
            yield return Path.Combine(root, "Antigravity");
        }
    }

    private static IEnumerable<string> EnumerateExecutables(string directory)
    {
        if (!Directory.Exists(directory))
        {
            yield break;
        }

        foreach (var name in SupportedExecutableNames)
        {
            yield return Path.Combine(directory, name);
        }

        string[] nested;
        try
        {
            nested = Directory.EnumerateFiles(directory, "*.exe", SearchOption.AllDirectories)
                .Where(path => SupportedExecutableNames.Any(name =>
                    string.Equals(Path.GetFileName(path), name, StringComparison.OrdinalIgnoreCase)))
                .Take(20)
                .ToArray();
        }
        catch (IOException)
        {
            yield break;
        }
        catch (UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var path in nested)
        {
            yield return path;
        }
    }

    private static IEnumerable<string> ExpandCandidate(string value)
    {
        var expanded = Environment.ExpandEnvironmentVariables(value.Trim());
        if (expanded.StartsWith('"'))
        {
            var closingQuote = expanded.IndexOf('"', 1);
            expanded = closingQuote > 1 ? expanded[1..closingQuote] : expanded.Trim('"');
        }
        else
        {
            var commaIndex = expanded.IndexOf(',');
            if (commaIndex >= 0)
            {
                expanded = expanded[..commaIndex];
            }

            expanded = expanded.Trim().Trim('"');
        }
        if (string.IsNullOrWhiteSpace(expanded))
        {
            yield break;
        }

        if (Directory.Exists(expanded))
        {
            foreach (var executable in EnumerateExecutables(expanded))
            {
                yield return executable;
            }

            yield break;
        }

        if (Path.GetExtension(expanded).Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            yield return expanded;
            yield break;
        }

        foreach (var executable in EnumerateExecutables(expanded))
        {
            yield return executable;
        }
    }

    private static void AddCandidate(HashSet<string> candidates, string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            if (SupportedExecutableNames.Any(name =>
                    string.Equals(Path.GetFileName(fullPath), name, StringComparison.OrdinalIgnoreCase)))
            {
                candidates.Add(fullPath);
            }
        }
        catch (ArgumentException)
        {
            // Ignore malformed registry values.
        }
    }
}

internal enum DownloadPhase
{
    Connecting,
    Receiving,
    Extracting,
    Caching
}

internal readonly record struct DownloadProgress(
    DownloadPhase Phase,
    long? TotalBytes,
    long BytesReceived);

internal static class RemotePayloadService
{
    // The continuous workflow updates this public release after every main-branch build.
    // Change this value when publishing the project under another GitHub repository.
    private const string Repository = "JackyJason2023/antigravity-proxy";
    private const string ReleaseTag = "latest";

    public static string ReleasePageUrl { get; } = $"https://github.com/{Repository}/releases/latest";

    private static readonly HttpClient Client = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        // Keep the Windows/system proxy enabled so GitHub can be reached through
        // the proxy configured by the user or the current environment.
        var handler = new HttpClientHandler
        {
            UseProxy = true,
            AllowAutoRedirect = true
        };
        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(5)
        };
    }

    public static async Task<PayloadInfo> DownloadLatestAsync(
        PeArchitecture architecture,
        CancellationToken cancellationToken,
        IProgress<DownloadProgress>? progress = null)
    {
        var architectureName = architecture switch
        {
            PeArchitecture.X86 => "x86",
            PeArchitecture.X64 => "x64",
            _ => throw new InvalidOperationException("目前 GitHub 最新构建仅提供 x86 和 x64 版本。")
        };

        var cacheRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AntigravityProxyInstaller",
            "payload-cache");
        var cacheDirectory = Path.Combine(cacheRoot, architectureName);
        var stagingDirectory = Path.Combine(cacheRoot, $".download-{architectureName}-{Guid.NewGuid():N}");
        var archivePath = Path.Combine(stagingDirectory, "payload.zip");
        var assetName = $"antigravity-proxy-latest-ide-win-{architectureName}.zip";
        var assetUrl = $"https://github.com/{Repository}/releases/download/{ReleaseTag}/{assetName}?cacheBust={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";

        Directory.CreateDirectory(stagingDirectory);
        try
        {
            Report(progress, new DownloadProgress(DownloadPhase.Connecting, null, 0));

            using var request = new HttpRequestMessage(HttpMethod.Get, assetUrl);
            request.Headers.UserAgent.ParseAdd("AntigravityProxyInstaller/1.0");
            request.Headers.TryAddWithoutValidation("Cache-Control", "no-cache");
            using var response = await Client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var publicUrl = assetUrl[..assetUrl.IndexOf('?')];
                if ((int)response.StatusCode == 404)
                {
                    throw new InvalidOperationException(
                        $"GitHub 返回 404，未找到最新构建。\n下载地址：{publicUrl}\n这通常表示 continuous workflow 尚未成功运行，或仓库/资产名称配置不一致；如果是代理问题，通常会显示连接失败而不是 404。");
                }

                throw new InvalidOperationException(
                    $"GitHub 最新构建暂不可用（HTTP {(int)response.StatusCode}）。请稍后重试，或手动选择部署源。");
            }

            var totalBytes = response.Content.Headers.ContentLength;
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var destination = new FileStream(
                archivePath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 128 * 1024,
                useAsync: true))
            {
                var buffer = new byte[128 * 1024];
                long received = 0;
                var lastReportTicks = Environment.TickCount64;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    received += read;

                    // Throttle UI updates so a fast download stays responsive; the last
                    // chunk is reported explicitly after the loop.
                    if (Environment.TickCount64 - lastReportTicks >= 80)
                    {
                        lastReportTicks = Environment.TickCount64;
                        Report(progress, new DownloadProgress(DownloadPhase.Receiving, totalBytes, received));
                    }
                }

                Report(progress, new DownloadProgress(DownloadPhase.Receiving, totalBytes, received));
            }

            Report(progress, new DownloadProgress(DownloadPhase.Extracting, null, 0));
            var extractedDirectory = Path.Combine(stagingDirectory, "extracted");
            ZipFile.ExtractToDirectory(archivePath, extractedDirectory);
            if (!PayloadLocator.TryLoad(extractedDirectory, out var downloadedPayload, out var error) ||
                downloadedPayload is null)
            {
                throw new InvalidDataException($"GitHub 最新构建内容无效：{error}");
            }

            Report(progress, new DownloadProgress(DownloadPhase.Caching, null, 0));
            Directory.CreateDirectory(cacheDirectory);
            File.Copy(downloadedPayload.VersionDllPath,
                Path.Combine(cacheDirectory, "version.dll"), overwrite: true);
            File.Copy(downloadedPayload.ConfigPath,
                Path.Combine(cacheDirectory, "config.json"), overwrite: true);

            if (!PayloadLocator.TryLoad(cacheDirectory, out var cachedPayload, out error, PayloadOrigin.Remote) ||
                cachedPayload is null)
            {
                throw new InvalidDataException($"下载的部署源缓存失败：{error}");
            }

            return cachedPayload;
        }
        finally
        {
            try
            {
                if (Directory.Exists(stagingDirectory))
                {
                    Directory.Delete(stagingDirectory, recursive: true);
                }
            }
            catch (IOException)
            {
                // A failed cleanup must not hide the download result.
            }
            catch (UnauthorizedAccessException)
            {
                // A failed cleanup must not hide the download result.
            }
        }
    }

    private static void Report(IProgress<DownloadProgress>? progress, DownloadProgress value)
    {
        progress?.Report(value);
    }

    public static string FormatFileSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB" };
        var size = (double)bytes;
        var unitIndex = 0;
        while (size >= 1024 && unitIndex < units.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }

        return unitIndex == 0
            ? $"{bytes} {units[0]}"
            : $"{size:0.#} {units[unitIndex]}";
    }
}

internal static class ShortcutResolver
{
    private static readonly string[] SupportedExecutableNames =
    {
        "Antigravity.exe",
        "Antigravity IDE.exe"
    };

    public static bool TryResolve(string droppedPath, out string executablePath, out string error)
    {
        executablePath = string.Empty;
        error = string.Empty;

        if (!File.Exists(droppedPath))
        {
            error = "拖入的文件不存在。";
            return false;
        }

        try
        {
            var extension = Path.GetExtension(droppedPath);
            if (extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                executablePath = ResolveShortcut(droppedPath);
            }
            else if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
            {
                executablePath = Path.GetFullPath(droppedPath);
            }
            else
            {
                error = "请拖入 Antigravity 的 .lnk 快捷方式。";
                return false;
            }
        }
        catch (Exception ex)
        {
            error = $"无法解析快捷方式：{ex.Message}";
            return false;
        }

        var fileName = Path.GetFileName(executablePath);
        if (!SupportedExecutableNames.Any(name => name.Equals(fileName, StringComparison.OrdinalIgnoreCase)))
        {
            error = "该快捷方式不是 Antigravity IDE 快捷方式。请确认目标是 Antigravity.exe 或 Antigravity IDE.exe。";
            return false;
        }

        if (!File.Exists(executablePath))
        {
            error = $"快捷方式目标不存在：{executablePath}";
            return false;
        }

        return true;
    }

    private static string ResolveShortcut(string shortcutPath)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
                         ?? throw new InvalidOperationException("Windows 快捷方式组件不可用。");
        var shell = Activator.CreateInstance(shellType)
                    ?? throw new InvalidOperationException("无法创建 Windows 快捷方式组件。");
        object? shortcut = null;

        try
        {
            shortcut = shellType.InvokeMember(
                "CreateShortcut",
                BindingFlags.InvokeMethod,
                binder: null,
                target: shell,
                args: new object[] { shortcutPath });

            var shortcutType = shortcut?.GetType()
                               ?? throw new InvalidOperationException("快捷方式对象无效。");
            var target = ReadShortcutProperty(shortcutType, shortcut, "TargetPath");
            if (string.IsNullOrWhiteSpace(target))
            {
                throw new InvalidOperationException("快捷方式没有可用的目标程序。");
            }

            target = target.Trim().Trim('"');
            if (!Path.IsPathRooted(target))
            {
                var workingDirectory = ReadShortcutProperty(shortcutType, shortcut, "WorkingDirectory");
                var baseDirectory = string.IsNullOrWhiteSpace(workingDirectory)
                    ? Path.GetDirectoryName(shortcutPath)
                    : workingDirectory;
                target = Path.Combine(baseDirectory ?? string.Empty, target);
            }

            return Path.GetFullPath(target);
        }
        finally
        {
            ReleaseComObject(shortcut);
            ReleaseComObject(shell);
        }
    }

    private static string? ReadShortcutProperty(Type shortcutType, object shortcut, string propertyName)
    {
        return shortcutType.InvokeMember(
            propertyName,
            BindingFlags.GetProperty,
            binder: null,
            target: shortcut,
            args: null) as string;
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }
}

internal static class PeArchitectureReader
{
    public static PeArchitecture Read(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new BinaryReader(stream);

            if (stream.Length < 0x40 || reader.ReadUInt16() != 0x5A4D)
            {
                return PeArchitecture.Unknown;
            }

            stream.Position = 0x3C;
            var peOffset = reader.ReadInt32();
            if (peOffset < 0 || (long)peOffset + 6 > stream.Length)
            {
                return PeArchitecture.Unknown;
            }

            stream.Position = peOffset;
            if (reader.ReadUInt32() != 0x00004550)
            {
                return PeArchitecture.Unknown;
            }

            return reader.ReadUInt16() switch
            {
                0x014C => PeArchitecture.X86,
                0x8664 => PeArchitecture.X64,
                0xAA64 => PeArchitecture.Arm64,
                _ => PeArchitecture.Unknown
            };
        }
        catch
        {
            return PeArchitecture.Unknown;
        }
    }
}

internal static class PayloadLocator
{
    public static bool TryLoad(
        string directoryPath,
        out PayloadInfo? payload,
        out string error,
        PayloadOrigin origin = PayloadOrigin.Local)
    {
        payload = null;
        error = string.Empty;

        try
        {
            var directory = Path.GetFullPath(directoryPath.Trim());
            var versionDllPath = Path.Combine(directory, "version.dll");
            var configPath = Path.Combine(directory, "config.json");

            if (!File.Exists(versionDllPath) || !File.Exists(configPath))
            {
                error = "文件夹中必须同时包含 version.dll 和 config.json。";
                return false;
            }

            var architecture = PeArchitectureReader.Read(versionDllPath);
            if (architecture == PeArchitecture.Unknown)
            {
                error = "无法识别 version.dll 的程序架构。";
                return false;
            }

            ValidateJson(configPath);
            payload = new PayloadInfo(directory, versionDllPath, configPath, architecture, origin);
            return true;
        }
        catch (Exception ex)
        {
            error = $"部署源无效：{ex.Message}";
            return false;
        }
    }

    public static bool TryFindDefault(out PayloadInfo? payload)
    {
        payload = null;
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var baseDirectory = Path.GetFullPath(AppContext.BaseDirectory);
        var current = new DirectoryInfo(baseDirectory);

        AddCandidate(candidates, baseDirectory);
        AddCandidate(candidates, Path.Combine(baseDirectory, "ide"));

        for (var depth = 0; depth < 8 && current is not null; depth++)
        {
            AddCandidate(candidates, Path.Combine(current.FullName, "output", "ide"));
            AddCandidate(candidates, Path.Combine(current.FullName, "ide"));
            current = current.Parent;
        }

        foreach (var candidate in candidates)
        {
            if (TryLoad(candidate, out payload, out _, PayloadOrigin.Bundled))
            {
                return true;
            }
        }

        return false;
    }

    private static void AddCandidate(HashSet<string> candidates, string path)
    {
        try
        {
            candidates.Add(Path.GetFullPath(path));
        }
        catch
        {
            // Ignore malformed candidate paths while probing the normal layouts.
        }
    }

    private static void ValidateJson(string path)
    {
        using var document = JsonDocument.Parse(
            File.ReadAllText(path),
            new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });

        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("config.json 的根节点必须是 JSON 对象。");
        }
    }
}

internal static class DeploymentInspector
{
    public static bool TryCreateTarget(string droppedPath, out TargetInfo? target, out string error)
    {
        target = null;
        error = string.Empty;

        if (!ShortcutResolver.TryResolve(droppedPath, out var executablePath, out error))
        {
            return false;
        }

        var directoryPath = Path.GetDirectoryName(executablePath);
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            error = "无法确定 Antigravity 的执行目录。";
            return false;
        }

        target = new TargetInfo(
            Path.GetFullPath(droppedPath),
            executablePath,
            directoryPath,
            PeArchitectureReader.Read(executablePath));
        return true;
    }

    public static DeploymentAssessment Inspect(PayloadInfo payload, TargetInfo target)
    {
        var versionPath = Path.Combine(target.DirectoryPath, "version.dll");
        var configPath = Path.Combine(target.DirectoryPath, "config.json");
        var sourceVersionHash = ComputeHash(payload.VersionDllPath);
        var sourceConfigHash = ComputeHash(payload.ConfigPath);

        var version = InspectFile("version.dll", versionPath, sourceVersionHash, validateJson: false);
        var config = InspectFile("config.json", configPath, sourceConfigHash, validateJson: true);
        var architectureMatches = payload.Architecture != PeArchitecture.Unknown &&
                                   target.Architecture != PeArchitecture.Unknown &&
                                   payload.Architecture == target.Architecture;

        var architectureStatus = target.Architecture == PeArchitecture.Unknown
            ? "无法识别目标程序架构"
            : architectureMatches
                ? $"架构匹配：{FormatArchitecture(target.Architecture)}"
                : $"架构不匹配：目标 {FormatArchitecture(target.Architecture)}，部署源 {FormatArchitecture(payload.Architecture)}";

        return new DeploymentAssessment(payload, target, version, config, architectureMatches, architectureStatus);
    }

    public static string ComputeHash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    public static string FormatArchitecture(PeArchitecture architecture)
    {
        return architecture switch
        {
            PeArchitecture.X86 => "x86",
            PeArchitecture.X64 => "x64",
            PeArchitecture.Arm64 => "ARM64",
            _ => "未知"
        };
    }

    private static FileInspection InspectFile(string name, string targetPath, string sourceHash, bool validateJson)
    {
        if (!File.Exists(targetPath))
        {
            return new FileInspection(name, targetPath, exists: false, readable: true, hashMatches: false, error: null);
        }

        try
        {
            if (validateJson)
            {
                using var document = JsonDocument.Parse(
                    File.ReadAllText(targetPath),
                    new JsonDocumentOptions
                    {
                        AllowTrailingCommas = true,
                        CommentHandling = JsonCommentHandling.Skip
                    });
            }

            var hashMatches = sourceHash.Equals(ComputeHash(targetPath), StringComparison.OrdinalIgnoreCase);
            return new FileInspection(name, targetPath, exists: true, readable: true, hashMatches, error: null);
        }
        catch (Exception ex)
        {
            return new FileInspection(name, targetPath, exists: true, readable: false, hashMatches: false, ex.Message);
        }
    }
}

internal sealed class DeploymentResult
{
    public List<string> CopiedFiles { get; } = new();
    public List<string> SkippedFiles { get; } = new();
    public List<string> BackupFiles { get; } = new();
}

internal static class DeploymentService
{
    public static DeploymentResult Deploy(DeploymentAssessment assessment, bool overwriteExisting)
    {
        if (!assessment.IsReady)
        {
            throw new InvalidOperationException("目标目录尚未通过检查，无法部署。");
        }

        if (IsApplicationRunning(assessment.Target))
        {
            throw new InvalidOperationException("Antigravity 正在运行。请完全退出 Antigravity 后再点击部署。");
        }

        var result = new DeploymentResult();
        var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var files = new[]
        {
            (Source: assessment.Payload.VersionDllPath, Target: assessment.VersionDll.TargetPath, Inspection: assessment.VersionDll),
            (Source: assessment.Payload.ConfigPath, Target: assessment.Config.TargetPath, Inspection: assessment.Config)
        };

        foreach (var file in files)
        {
            if (file.Inspection.Exists && file.Inspection.HashMatches)
            {
                result.SkippedFiles.Add(file.Inspection.Name);
                continue;
            }

            if (file.Inspection.Exists && !overwriteExisting)
            {
                result.SkippedFiles.Add($"{file.Inspection.Name}（已存在，未覆盖）");
                continue;
            }

            if (Path.GetFullPath(file.Source).Equals(Path.GetFullPath(file.Target), StringComparison.OrdinalIgnoreCase))
            {
                result.SkippedFiles.Add(file.Inspection.Name);
                continue;
            }

            if (file.Inspection.Exists)
            {
                var backupDirectory = Path.Combine(assessment.Target.DirectoryPath, ".antigravity-proxy-backup");
                Directory.CreateDirectory(backupDirectory);
                var backupPath = Path.Combine(backupDirectory, $"{timestamp}-{file.Inspection.Name}.bak");
                File.Copy(file.Target, backupPath, overwrite: false);
                result.BackupFiles.Add(backupPath);
            }

            File.Copy(file.Source, file.Target, overwrite: file.Inspection.Exists);
            var sourceHash = DeploymentInspector.ComputeHash(file.Source);
            var targetHash = DeploymentInspector.ComputeHash(file.Target);
            if (!sourceHash.Equals(targetHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException($"复制后校验失败：{file.Inspection.Name}");
            }

            result.CopiedFiles.Add(file.Inspection.Name);
        }

        return result;
    }

    /// <summary>
    /// Reports whether the selected installation is currently running. The deployment
    /// must not copy files into a live install, so the UI also uses this as a pre-flight check.
    /// </summary>
    public static bool IsApplicationRunning(TargetInfo target)
    {
        var processName = Path.GetFileNameWithoutExtension(target.ExecutablePath);
        foreach (var process in Process.GetProcessesByName(processName))
        {
            try
            {
                var runningPath = process.MainModule?.FileName;
                if (string.IsNullOrWhiteSpace(runningPath) ||
                    Path.GetFullPath(runningPath).Equals(target.ExecutablePath, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch
            {
                // If the process cannot expose its path, do not risk copying into a locked install.
                return true;
            }
            finally
            {
                process.Dispose();
            }
        }

        return false;
    }
}

internal static class AntigravityLauncher
{
    /// <summary>Starts the deployed Antigravity executable through the shell.</summary>
    public static void Launch(TargetInfo target)
    {
        Start(new ProcessStartInfo
        {
            FileName = target.ExecutablePath,
            WorkingDirectory = target.DirectoryPath,
            UseShellExecute = true
        });
    }

    /// <summary>Opens Explorer with the Antigravity executable selected in its install folder.</summary>
    public static void RevealInstallDirectory(TargetInfo target)
    {
        Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"/select,\"{target.ExecutablePath}\"",
            UseShellExecute = true
        });
    }

    /// <summary>Opens a http/https URL with the user's default browser.</summary>
    public static void OpenUrl(string url)
    {
        Start(new ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true
        });
    }

    private static void Start(ProcessStartInfo startInfo)
    {
        try
        {
            using var process = Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"无法打开 {startInfo.FileName}：{ex.Message}", ex);
        }
    }
}

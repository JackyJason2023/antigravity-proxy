using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace AntigravityProxyInstaller;

internal enum PeArchitecture
{
    Unknown,
    X86,
    X64,
    Arm64
}

internal sealed class PayloadInfo
{
    public PayloadInfo(string directoryPath, string versionDllPath, string configPath, PeArchitecture architecture)
    {
        DirectoryPath = directoryPath;
        VersionDllPath = versionDllPath;
        ConfigPath = configPath;
        Architecture = architecture;
    }

    public string DirectoryPath { get; }
    public string VersionDllPath { get; }
    public string ConfigPath { get; }
    public PeArchitecture Architecture { get; }
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
    public static bool TryLoad(string directoryPath, out PayloadInfo? payload, out string error)
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
            payload = new PayloadInfo(directory, versionDllPath, configPath, architecture);
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
            if (TryLoad(candidate, out payload, out _))
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

        if (IsTargetRunning(assessment.Target))
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

    private static bool IsTargetRunning(TargetInfo target)
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

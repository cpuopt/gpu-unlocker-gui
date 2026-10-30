using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace GpuUnlocker.Core;

public static class ProcessRunner
{
    public static async Task<string[]> RunAsync(string executable, IEnumerable<string> arguments, Action<string>? log,
        CancellationToken token = default, string? workingDirectory = null)
    {
        var result = await RunCapturedAsync(executable, arguments, log, token, workingDirectory);
        if (result.ExitCode != 0 && !result.Lines.Any(x => x.StartsWith(WindowsBackend.ResultPrefix)))
            throw new IOException($"进程退出代码 {result.ExitCode}。详细原因见终端日志。");
        return result.Lines;
    }
    public sealed record Captured(int ExitCode, string[] Lines);
    public static async Task<Captured> RunCapturedAsync(string executable, IEnumerable<string> arguments, Action<string>? log,
        CancellationToken token = default, string? workingDirectory = null, IReadOnlyDictionary<string, string>? environment = null)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workingDirectory ?? AppContext.BaseDirectory
        };
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        if (environment is not null) foreach (var pair in environment) start.Environment[pair.Key] = pair.Value;
        if (string.Equals(Path.GetFullPath(executable), WindowsBackend.PowerShell, StringComparison.OrdinalIgnoreCase))
        {
            // A parent PowerShell 7 process can otherwise leak its incompatible
            // PSModulePath into Windows PowerShell 5.1 child processes.
            start.Environment["PSModulePath"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "Modules");
        }
        using var process = new Process { StartInfo = start };
        var lines = new List<string>(); var gate = new object();
        if (!process.Start()) throw new IOException("无法启动子进程。");
        using var registration = token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
        async Task ReadAsync(StreamReader reader)
        {
            while (await reader.ReadLineAsync() is { } line)
            { lock (gate) lines.Add(line); log?.Invoke(line); }
        }
        await Task.WhenAll(ReadAsync(process.StandardOutput), ReadAsync(process.StandardError), process.WaitForExitAsync());
        token.ThrowIfCancellationRequested();
        return new(process.ExitCode, lines.ToArray());
    }
}

public sealed class WindowsBackend(string? scripts = null)
{
    public const string ResultPrefix = "GPUUNLOCKER_RESULT:";
    public static string PowerShell => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
    // Full-content bundles load assemblies from their extraction directory.
#pragma warning disable IL3000
    public static string ScriptDirectory => Path.Combine(Path.GetDirectoryName(typeof(WindowsBackend).Assembly.Location)
        ?? throw new IOException("无法定位内置脚本。"), "Scripts");
#pragma warning restore IL3000
    private readonly string scriptDirectory = scripts ?? ScriptDirectory;
    public async Task<T> RunAsync<T>(string operation, Action<string>? log = null, string? packagePath = null,
        CancellationToken token = default)
    {
        if (!new[] { "Probe", "Gpus", "Turing", "Install", "SetFirst", "Verify", "Remove" }.Contains(operation)) throw new ArgumentException("非法操作。");
        var args = new List<string> { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(scriptDirectory, "backend.ps1"), "-Operation", operation };
        if (packagePath is not null) { args.Add("-PackagePath"); args.Add(Path.GetFullPath(packagePath)); }
        var output = await ProcessRunner.RunAsync(PowerShell, args, line => { if (!line.StartsWith(ResultPrefix)) log?.Invoke(line); }, token);
        var result = output.LastOrDefault(x => x.StartsWith(ResultPrefix)) ?? throw new IOException("系统操作没有返回结构化结果。");
        using var document = JsonDocument.Parse(result[ResultPrefix.Length..]);
        if (!document.RootElement.GetProperty("success").GetBoolean())
            throw new IOException(document.RootElement.GetProperty("error").GetString());
        return document.RootElement.GetProperty("data").Deserialize<T>(WireJson.Options) ?? throw new IOException("系统操作结果为空。");
    }
}

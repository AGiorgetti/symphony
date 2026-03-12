using System.Diagnostics;

namespace Symphony.DotNet.Services;

internal static class ProcessCommandRunner
{
    public static async Task<(int ExitCode, string Output)> RunShellAsync(string command, string workingDirectory, int timeoutMs, CancellationToken cancellationToken)
    {
        using var process = CreateProcess(command, workingDirectory);
        process.Start();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeoutMs);

        var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw;
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return (process.ExitCode, stdout + stderr);
    }

    private static Process CreateProcess(string command, string workingDirectory)
    {
        var isWindows = OperatingSystem.IsWindows();
        return new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = isWindows ? "cmd.exe" : "/bin/sh",
                Arguments = isWindows ? $"/c {command}" : $"-lc \"{command.Replace("\"", "\\\"", StringComparison.Ordinal)}\"",
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }
}

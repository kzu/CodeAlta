using System.Diagnostics;
using System.Text;

namespace CodeAlta.Agent.Grok;

/// <summary>Starts the Grok Build CLI with the arguments of one launch.</summary>
internal static class GrokProcess
{
    /// <summary>
    /// Starts the executable of <paramref name="launch"/>. Standard input, output, and error are redirected.
    /// </summary>
    /// <param name="launch">The executable and the complete argument list.</param>
    /// <returns>The started process.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="launch"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The process could not be started.</exception>
    public static Process Start(GrokCliLaunch launch)
    {
        ArgumentNullException.ThrowIfNull(launch);
        var fileName = ResolveFile(launch.FileName, Environment.GetEnvironmentVariable("PATH"), OperatingSystem.IsWindows());
        var start = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (!string.IsNullOrWhiteSpace(launch.WorkingDirectory))
        {
            start.WorkingDirectory = launch.WorkingDirectory;
        }

        foreach (var argument in launch.Arguments)
        {
            start.ArgumentList.Add(argument);
        }

        var process = Process.Start(start)
            ?? throw new InvalidOperationException($"Could not start the Grok Build CLI '{fileName}'.");
        process.StandardInput.AutoFlush = true;
        return process;
    }

    /// <summary>
    /// Resolves a bare command on <paramref name="searchPath"/>. On Windows an <c>.exe</c> is preferred to a <c>.cmd</c> shim.
    /// </summary>
    /// <param name="fileName">The configured executable.</param>
    /// <param name="searchPath">The PATH used to resolve a bare command.</param>
    /// <param name="windows"><see langword="true"/> to search for <c>.exe</c> and <c>.cmd</c>.</param>
    /// <returns>The file that should be started, or <paramref name="fileName"/> when nothing on the path matches.</returns>
    internal static string ResolveFile(string fileName, string? searchPath, bool windows)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (Path.IsPathRooted(fileName) || fileName.Contains(Path.DirectorySeparatorChar) || fileName.Contains(Path.AltDirectorySeparatorChar))
        {
            return fileName;
        }

        if (string.IsNullOrWhiteSpace(searchPath))
        {
            return fileName;
        }

        var extensions = windows ? new[] { ".exe", ".cmd", "" } : new[] { "" };
        foreach (var entry in searchPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = entry.Trim().Trim('"');
            if (directory.Length == 0)
            {
                continue;
            }

            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(directory, extension.Length == 0 ? fileName : fileName + extension);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return fileName;
    }
}

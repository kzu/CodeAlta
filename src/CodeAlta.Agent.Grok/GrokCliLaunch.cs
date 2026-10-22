namespace CodeAlta.Agent.Grok;

/// <summary>
/// Describes how to start the Grok Build CLI for one provider definition.
/// </summary>
public sealed class GrokCliLaunch
{
    /// <summary>
    /// Initializes a launch for the Grok Build executable.
    /// </summary>
    /// <param name="fileName">The executable, or <c>grok</c> when the definition leaves it unset.</param>
    /// <param name="arguments">Arguments of this invocation, including the verb.</param>
    /// <param name="workingDirectory">Optional working directory for the process.</param>
    public GrokCliLaunch(string fileName, IReadOnlyList<string> arguments, string? workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(arguments);
        FileName = fileName;
        Arguments = arguments;
        WorkingDirectory = workingDirectory;
    }

    /// <summary>Gets the executable file name.</summary>
    public string FileName { get; }

    /// <summary>Gets the arguments, including the verb.</summary>
    public IReadOnlyList<string> Arguments { get; }

    /// <summary>Gets the working directory, when one was configured.</summary>
    public string? WorkingDirectory { get; }

    /// <summary>
    /// Builds a launch from a configured provider command. An empty command selects <c>grok</c> on the path.
    /// </summary>
    /// <param name="command">The configured executable, or empty to use <c>grok</c>.</param>
    /// <param name="arguments">Arguments of this invocation.</param>
    /// <param name="workingDirectory">Optional working directory.</param>
    /// <returns>The launch description.</returns>
    public static GrokCliLaunch From(string? command, IReadOnlyList<string>? arguments, string? workingDirectory)
    {
        var fileName = string.IsNullOrWhiteSpace(command) ? "grok" : command.Trim();
        var prefix = arguments is null
            ? Array.Empty<string>()
            : arguments.Where(static argument => !string.IsNullOrWhiteSpace(argument)).Select(static argument => argument.Trim()).ToArray();
        return new GrokCliLaunch(fileName, prefix, string.IsNullOrWhiteSpace(workingDirectory) ? null : workingDirectory.Trim());
    }
}

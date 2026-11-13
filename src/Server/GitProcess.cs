using System.Diagnostics;
using System.Text;

sealed class ForgeException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

sealed class GitProcess(IConfiguration config, ForgeStore store, ILogger<GitProcess> logger)
{
    private readonly SemaphoreSlim slots = new(4);
    private readonly string executable = FindExecutable(config["Forge:GitExecutable"] ?? "git");

    private static string FindExecutable(string name)
    {
        if (Path.IsPathFullyQualified(name) && File.Exists(name))
            return name;
        if (name != Path.GetFileName(name))
            throw new InvalidOperationException("Configure an absolute Git executable path.");
        var file = OperatingSystem.IsWindows() && !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name + ".exe" : name;
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (!Path.IsPathFullyQualified(directory))
                continue;
            var candidate = Path.Combine(directory, file);
            if (File.Exists(candidate))
                return candidate;
        }
        throw new InvalidOperationException("Git executable was not found.");
    }

    public async Task<byte[]> Run(string? repo, string[] args, CancellationToken cancellation,
        byte[]? input = null, Dictionary<string, string>? environment = null, int limit = 2 * 1024 * 1024,
        bool allowFailure = false)
    {
        var arguments = new List<string> { "--literal-pathspecs" };
        if (repo is not null)
            arguments.Add($"--git-dir={repo}");
        arguments.AddRange(args);
        return await Execute(arguments, environment, TimeSpan.FromSeconds(15), cancellation,
            async (process, token) =>
            {
                var write = WriteInput(process, input, token);
                var output = await ReadBounded(process.StandardOutput.BaseStream, limit, token);
                await write;
                await process.WaitForExitAsync(token);
                if (process.ExitCode != 0)
                {
                    if (allowFailure)
                        return [];
                    throw new ForgeException(422, "Git rejected the operation.");
                }
                return output;
            });
    }

    private static async Task WriteInput(Process process, byte[]? input, CancellationToken token)
    {
        try
        {
            if (input is not null)
                await process.StandardInput.BaseStream.WriteAsync(input, token);
        }
        finally
        {
            process.StandardInput.Close();
        }
    }

    public async Task<T> Execute<T>(IEnumerable<string> args, IReadOnlyDictionary<string, string>? environment,
        TimeSpan timeout, CancellationToken cancellation, Func<Process, CancellationToken, Task<T>> handle)
    {
        if (!await slots.WaitAsync(TimeSpan.FromSeconds(2), cancellation))
            throw new ForgeException(503, "Git is busy. Try again.");
        try
        {
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            lifetime.CancelAfter(timeout);
            var start = new ProcessStartInfo(executable)
            {
                WorkingDirectory = store.Scratch,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var argument in args)
                start.ArgumentList.Add(argument);
            start.Environment.Clear();
            foreach (var key in new[] { "PATH", "SystemRoot", "WINDIR", "TEMP", "TMP" })
            {
                if (Environment.GetEnvironmentVariable(key) is { } value)
                    start.Environment[key] = value;
            }
            start.Environment["HOME"] = store.Scratch;
            start.Environment["XDG_CONFIG_HOME"] = store.Scratch;
            start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
            start.Environment["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
            start.Environment["GIT_TERMINAL_PROMPT"] = "0";
            start.Environment["LC_ALL"] = "C";
            if (environment is not null)
            {
                foreach (var (key, value) in environment)
                    start.Environment[key] = value;
            }

            using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start Git.");
            using var kill = lifetime.Token.Register(() => Kill(process));
            var errors = DrainErrors(process.StandardError.BaseStream, lifetime.Token);
            try
            {
                var result = await handle(process, lifetime.Token);
                await process.WaitForExitAsync(lifetime.Token);
                await errors;
                if (process.ExitCode != 0)
                    logger.LogDebug("Git exited with code {ExitCode}", process.ExitCode);
                return result;
            }
            catch (Exception) when (lifetime.IsCancellationRequested && !cancellation.IsCancellationRequested)
            {
                throw new ForgeException(504, "Git operation timed out.");
            }
            finally
            {
                Kill(process);
                await process.WaitForExitAsync(CancellationToken.None);
                lifetime.Cancel();
                try { await errors; }
                catch (OperationCanceledException) { }
                catch (IOException) { }
            }
        }
        finally
        {
            slots.Release();
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private static async Task DrainErrors(Stream stream, CancellationToken token)
    {
        var buffer = new byte[4096];
        while (await stream.ReadAsync(buffer, token) > 0) { }
    }

    private static async Task<byte[]> ReadBounded(Stream stream, int limit, CancellationToken token)
    {
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (await stream.ReadAsync(buffer, token) is var count && count > 0)
        {
            if (output.Length + count > limit)
                throw new ForgeException(413, "Git output exceeds the response limit.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    public static string Decode(byte[] bytes) => Encoding.UTF8.GetString(bytes);
}

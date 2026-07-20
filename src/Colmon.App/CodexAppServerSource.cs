using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Colmon;

internal sealed class CodexAppServerSource(SourceConfig config) : InfoSource(config)
{
    private const int WeeklyWindowMinutes = 7 * 24 * 60;

    public override async Task<InfoSample> ReadAsync(CancellationToken cancellationToken)
    {
        var candidates = CodexCommandLocator.Find(Config.Command);
        if (candidates.Count == 0)
            throw new FileNotFoundException("Codex App Server executable was not found.");

        Exception? lastError = null;
        foreach (var candidate in candidates)
        {
            try
            {
                var remaining = await ReadRemainingPercentAsync(candidate, cancellationToken);
                return Sample(remaining.ToString("0.###", CultureInfo.InvariantCulture) + "%");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                lastError = exception;
                if (!string.IsNullOrWhiteSpace(Config.Command)) break;
            }
        }

        throw new InvalidOperationException(
            $"Codex App Server quota read failed after {candidates.Count} candidate(s): {SafeError(lastError)}",
            lastError);
    }

    internal static decimal ParseRemainingPercent(JsonElement result)
    {
        var snapshot = SelectRateLimitSnapshot(result);
        var weekly = SelectWeeklyWindow(snapshot);
        if (!TryDecimal(weekly, out var usedPercent, "usedPercent", "used_percent"))
            throw new InvalidDataException("Codex weekly window did not include usedPercent.");
        return Math.Clamp(100M - usedPercent, 0M, 100M);
    }

    private static async Task<decimal> ReadRemainingPercentAsync(string command, CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = StartInfo(command), EnableRaisingEvents = true };
        try
        {
            if (!process.Start()) throw new InvalidOperationException("Codex App Server did not start.");
        }
        catch (Win32Exception exception)
        {
            throw new InvalidOperationException("Codex App Server executable could not be launched.", exception);
        }

        var stderrTask = process.StandardError.ReadToEndAsync();
        try
        {
            process.StandardInput.AutoFlush = true;
            await SendRequestAsync(process, 1, "initialize", new
            {
                clientInfo = new { name = "colmon", title = "Colmon", version = "0.1.0" }
            }, cancellationToken);
            await SendNotificationAsync(process, "initialized", new { }, cancellationToken);
            var result = await SendRequestAsync(process, 2, "account/rateLimits/read", null, cancellationToken);
            try
            {
                return ParseRemainingPercent(result);
            }
            catch (InvalidDataException)
            {
                await Task.Delay(300, cancellationToken);
                result = await SendRequestAsync(process, 3, "account/rateLimits/read", null, cancellationToken);
                return ParseRemainingPercent(result);
            }
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                catch (Win32Exception) { }
            }
            try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (TimeoutException) { }
            catch (Exception) when (process.HasExited || cancellationToken.IsCancellationRequested) { }
            try { await stderrTask.WaitAsync(TimeSpan.FromSeconds(1)); }
            catch (TimeoutException) { }
        }
    }

    private static ProcessStartInfo StartInfo(string command)
    {
        var isCommandScript = command.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
                              command.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);
        var startInfo = new ProcessStartInfo
        {
            FileName = isCommandScript ? Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe" : command,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        if (isCommandScript)
        {
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/s");
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add($"\"{command}\" -s read-only -a untrusted app-server");
        }
        else
        {
            foreach (var argument in new[] { "-s", "read-only", "-a", "untrusted", "app-server" })
                startInfo.ArgumentList.Add(argument);
        }
        return startInfo;
    }

    private static async Task<JsonElement> SendRequestAsync(
        Process process,
        int id,
        string method,
        object? parameters,
        CancellationToken cancellationToken)
    {
        object message = parameters is null ? new { method, id } : new { method, id, @params = parameters };
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(), cancellationToken);

        while (true)
        {
            var line = await process.StandardOutput.ReadLineAsync(cancellationToken);
            if (line is null)
                throw new InvalidOperationException($"Codex App Server exited before responding to {method}.");
            if (string.IsNullOrWhiteSpace(line)) continue;

            JsonDocument document;
            try { document = JsonDocument.Parse(line); }
            catch (JsonException) { continue; }
            using (document)
            {
                var root = document.RootElement;
                if (!root.TryGetProperty("id", out var responseId) || !responseId.TryGetInt32(out var value) || value != id)
                    continue;
                if (root.TryGetProperty("error", out var error))
                    throw new InvalidOperationException(SafeRpcError(error));
                if (!root.TryGetProperty("result", out var result))
                    throw new InvalidDataException($"Codex App Server response to {method} did not include result.");
                return result.Clone();
            }
        }
    }

    private static Task SendNotificationAsync(
        Process process,
        string method,
        object parameters,
        CancellationToken cancellationToken) =>
        process.StandardInput.WriteLineAsync(
            JsonSerializer.Serialize(new { method, @params = parameters }).AsMemory(), cancellationToken);

    private static JsonElement SelectRateLimitSnapshot(JsonElement result)
    {
        var byId = Property(result, "rateLimitsByLimitId", "rate_limits_by_limit_id");
        if (byId is { ValueKind: JsonValueKind.Object } &&
            byId.Value.TryGetProperty("codex", out var codex) && HasWindows(codex))
            return codex;

        var direct = Property(result, "rateLimits", "rate_limits");
        if (direct is { } directValue && HasWindows(directValue)) return directValue;

        if (byId is { ValueKind: JsonValueKind.Object })
        {
            var alternatives = byId.Value.EnumerateObject()
                .Where(property => !property.NameEquals("codex") && HasWindows(property.Value))
                .Select(property => property.Value)
                .ToArray();
            if (alternatives.Length == 1) return alternatives[0];
            if (alternatives.Length > 1 && alternatives.Select(WindowSignature).Distinct().Count() == 1)
                return alternatives[0];
        }

        throw new InvalidDataException("Codex quota response did not include an unambiguous rate-limit window.");
    }

    private static JsonElement SelectWeeklyWindow(JsonElement snapshot)
    {
        JsonElement? secondary = null;
        foreach (var name in new[] { "primary", "secondary" })
        {
            if (!snapshot.TryGetProperty(name, out var window) || window.ValueKind != JsonValueKind.Object) continue;
            if (name == "secondary") secondary = window;
            if (TryDecimal(window, out var minutes, "windowDurationMins", "window_duration_mins") &&
                minutes >= WeeklyWindowMinutes)
                return window;
        }
        return secondary ?? throw new InvalidDataException("Codex quota response did not include a weekly window.");
    }

    private static bool HasWindows(JsonElement value) => value.ValueKind == JsonValueKind.Object &&
        (value.TryGetProperty("primary", out _) || value.TryGetProperty("secondary", out _));

    private static string WindowSignature(JsonElement snapshot) => string.Join("|", new[] { "primary", "secondary" }.Select(name =>
    {
        if (!snapshot.TryGetProperty(name, out var window)) return name + ":none";
        return string.Join(":", name,
            Text(window, "usedPercent", "used_percent"),
            Text(window, "resetsAt", "resets_at"),
            Text(window, "windowDurationMins", "window_duration_mins"));
    }));

    private static JsonElement? Property(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in names)
            if (value.TryGetProperty(name, out var property)) return property;
        return null;
    }

    private static bool TryDecimal(JsonElement value, out decimal result, params string[] names)
    {
        var property = Property(value, names);
        if (property is { ValueKind: JsonValueKind.Number } && property.Value.TryGetDecimal(out result)) return true;
        if (property is { ValueKind: JsonValueKind.String } && decimal.TryParse(property.Value.GetString(),
                NumberStyles.Float, CultureInfo.InvariantCulture, out result)) return true;
        result = 0;
        return false;
    }

    private static string Text(JsonElement value, params string[] names) => Property(value, names)?.ToString() ?? "";

    private static string SafeRpcError(JsonElement error)
    {
        var message = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var value)
            ? value.GetString()
            : "Codex App Server returned an RPC error.";
        return string.IsNullOrWhiteSpace(message)
            ? "Codex App Server returned an RPC error."
            : message[..Math.Min(message.Length, 240)];
    }

    private static string SafeError(Exception? exception)
    {
        if (exception is null) return "unknown error";
        var message = exception.Message.Replace('\r', ' ').Replace('\n', ' ');
        return $"{exception.GetType().Name}: {message[..Math.Min(message.Length, 240)]}";
    }
}

internal static class CodexCommandLocator
{
    public static IReadOnlyList<string> Find(string? configuredCommand)
    {
        if (!string.IsNullOrWhiteSpace(configuredCommand)) return [configuredCommand];
        var candidates = new List<string>();
        Add(candidates, Environment.GetEnvironmentVariable("COLMON_CODEX_COMMAND"));

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        AddBinDirectory(candidates, Path.Combine(localAppData, "OpenAI", "Codex", "bin"));
        var packages = Path.Combine(localAppData, "Packages");
        try
        {
            foreach (var package in Directory.GetDirectories(packages, "OpenAI.Codex_*").OrderByDescending(Path.GetFileName))
                AddBinDirectory(candidates, Path.Combine(package, "LocalCache", "Local", "OpenAI", "Codex", "bin"));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        Add(candidates, Path.Combine(localAppData, "Programs", "Codex", "resources", "codex.exe"));
        Add(candidates, Path.Combine(localAppData, "Microsoft", "WindowsApps", "codex.exe"));
        Add(candidates, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "codex.cmd"));
        Add(candidates, "codex.exe");
        Add(candidates, "codex.cmd");
        Add(candidates, "codex");
        return candidates.Where(candidate => !Path.IsPathRooted(candidate) || File.Exists(candidate))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void AddBinDirectory(List<string> candidates, string directory)
    {
        Add(candidates, Path.Combine(directory, "codex.exe"));
        try
        {
            foreach (var versionDirectory in Directory.GetDirectories(directory).OrderByDescending(File.GetLastWriteTimeUtc))
                Add(candidates, Path.Combine(versionDirectory, "codex.exe"));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void Add(List<string> candidates, string? candidate)
    {
        if (!string.IsNullOrWhiteSpace(candidate)) candidates.Add(candidate);
    }
}

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Colmon;

internal sealed record CodexRateLimitReading(
    decimal RemainingPercent,
    decimal UsedPercent,
    decimal WindowDurationMinutes,
    string ResetAt,
    string? LimitId,
    string? PlanType);

internal sealed class CodexAppServerSource(SourceConfig config, JsonLog? log = null) : InfoSource(config)
{
    internal const int WeeklyWindowMinutes = 7 * 24 * 60;
    internal const int FiveHourWindowMinutes = 5 * 60;
    private static readonly string[] ApprovalPolicies = ["never", "untrusted"];

    private int TargetWindowMinutes => ResolveTargetWindowMinutes(Config);

    private string WindowLogPrefix => TargetWindowMinutes == FiveHourWindowMinutes
        ? "codex-five-hour"
        : "codex-weekly";

    public override async Task<InfoSample> ReadAsync(CancellationToken cancellationToken)
    {
        var candidates = CodexCommandLocator.Find(Config.Command);
        if (candidates.Count == 0)
            throw new FileNotFoundException("Codex App Server executable was not found.");

        Exception? lastError = null;
        foreach (var candidate in candidates)
        {
            foreach (var approvalPolicy in ApprovalPolicies)
            {
                try
                {
                    var reading = await ReadQuotaAsync(candidate, approvalPolicy, TargetWindowMinutes, cancellationToken, log);
                    log?.Write($"{WindowLogPrefix}.read", new
                    {
                        candidate = SafeCandidateName(candidate),
                        approvalPolicy,
                        window = WindowDescription(TargetWindowMinutes),
                        reading.RemainingPercent,
                        reading.UsedPercent,
                        reading.WindowDurationMinutes,
                        reading.ResetAt,
                        reading.LimitId,
                        reading.PlanType
                    });
                    return Sample(reading.RemainingPercent.ToString("0.###", CultureInfo.InvariantCulture) + "%");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    lastError = exception;
                    log?.Write($"{WindowLogPrefix}.candidate.failed", new
                    {
                        candidate = SafeCandidateName(candidate),
                        approvalPolicy,
                        window = WindowDescription(TargetWindowMinutes),
                        error = SafeError(exception)
                    });

                    if (!ShouldTryLegacyApprovalPolicy(exception, approvalPolicy))
                        break;
                }
            }

            if (!string.IsNullOrWhiteSpace(Config.Command))
                break;
        }

        throw new InvalidOperationException(
            $"Codex App Server quota read failed after {candidates.Count} candidate(s): {SafeError(lastError)}",
            lastError);
    }

    internal static decimal ParseRemainingPercent(JsonElement result) => ParseQuotaReading(result).RemainingPercent;

    internal static decimal ParseFiveHourRemainingPercent(JsonElement result) =>
        ParseQuotaReading(result, FiveHourWindowMinutes).RemainingPercent;

    internal static CodexRateLimitReading ParseQuotaReading(JsonElement result)
        => ParseQuotaReading(result, WeeklyWindowMinutes);

    internal static CodexRateLimitReading ParseQuotaReading(JsonElement result, int targetWindowMinutes)
    {
        var snapshot = SelectRateLimitSnapshot(result);
        var window = SelectWindow(snapshot, targetWindowMinutes);
        if (!TryDecimal(window, out var usedPercent, "usedPercent", "used_percent"))
        {
            if (!TryDecimal(window, out var remainingPercent, "remainingPercent", "remaining_percent"))
                throw new InvalidDataException($"Codex {WindowDescription(targetWindowMinutes)} window did not include usedPercent.");

            usedPercent = 100M - remainingPercent;
        }

        var duration = TryDecimal(window, out var windowDuration, "windowDurationMins", "window_duration_mins")
            ? windowDuration
            : 0M;
        return new CodexRateLimitReading(
            Math.Clamp(100M - usedPercent, 0M, 100M),
            usedPercent,
            duration,
            Text(window, "resetsAt", "resets_at"),
            TextOrNull(snapshot, "limitId", "limit_id"),
            TextOrNull(snapshot, "planType", "plan_type"));
    }

    private static async Task<CodexRateLimitReading> ReadQuotaAsync(
        string command,
        string approvalPolicy,
        int targetWindowMinutes,
        CancellationToken cancellationToken,
        JsonLog? log)
    {
        using var process = new Process { StartInfo = StartInfo(command, approvalPolicy), EnableRaisingEvents = true };
        try
        {
            if (!process.Start()) throw new InvalidOperationException("Codex App Server did not start.");
        }
        catch (Win32Exception exception)
        {
            throw new InvalidOperationException("Codex App Server executable could not be launched.", exception);
        }

        var stderrTask = process.StandardError.ReadToEndAsync();
        CodexRateLimitReading? reading = null;
        Exception? failure = null;
        string stderr = "";

        try
        {
            process.StandardInput.AutoFlush = true;
            await SendRequestAsync(process, 1, "initialize", new
            {
                clientInfo = new { name = "colmon", title = "Colmon", version = "0.1.0" }
            }, cancellationToken);
            await SendNotificationAsync(process, "initialized", new { }, cancellationToken);

            var rateLimitResult = await SendRequestAsync(
                process, 2, "account/rateLimits/read", null, cancellationToken);
            JsonElement? accountResult = null;
            try
            {
                accountResult = await SendRequestAsync(process, 3, "account/read", new { }, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                log?.Write($"{WindowLogPrefixFor(targetWindowMinutes)}.account.read.error", new
                {
                    candidate = SafeCandidateName(command),
                    approvalPolicy,
                    window = WindowDescription(targetWindowMinutes),
                    error = SafeError(exception)
                });
            }

            if (!TryParseQuotaReading(rateLimitResult, targetWindowMinutes, out reading) &&
                ShouldRetryEmptyQuota(rateLimitResult, accountResult))
            {
                await Task.Delay(300, cancellationToken);
                var retryResult = await SendRequestAsync(
                    process, 4, "account/rateLimits/read", null, cancellationToken);
                reading = ParseQuotaReading(retryResult, targetWindowMinutes);
            }
            else if (reading is null)
            {
                throw new InvalidDataException(
                    $"Codex quota response did not include a usable {WindowDescription(targetWindowMinutes)} window.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            failure = exception;
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

            try { stderr = await stderrTask.WaitAsync(TimeSpan.FromSeconds(1)); }
            catch (TimeoutException) { }
        }

        if (failure is not null)
            throw EnrichProcessFailure(failure, stderr);
        return reading ?? throw new InvalidDataException("Codex App Server returned no quota reading.");
    }

    private static bool TryParseQuotaReading(
        JsonElement result,
        int targetWindowMinutes,
        out CodexRateLimitReading? reading)
    {
        try
        {
            reading = ParseQuotaReading(result, targetWindowMinutes);
            return true;
        }
        catch (InvalidDataException)
        {
            reading = null;
            return false;
        }
    }

    private static bool ShouldRetryEmptyQuota(JsonElement rateLimitResult, JsonElement? accountResult)
    {
        if (HasAnyWindows(rateLimitResult) || accountResult is not { } account)
            return false;

        var accountValue = Property(account, "account");
        if (accountValue is not { ValueKind: JsonValueKind.Object } || !HasAccountIdentity(accountValue.Value))
            return false;

        var planType = Text(accountValue.Value, "planType", "plan_type").ToLowerInvariant();
        return !planType.Contains("usage", StringComparison.Ordinal) &&
               !planType.Contains("cbp", StringComparison.Ordinal) &&
               !planType.Contains("business", StringComparison.Ordinal);
    }

    private static bool HasAccountIdentity(JsonElement account)
    {
        foreach (var name in new[] { "id", "email", "planType", "plan_type", "type" })
        {
            if (account.TryGetProperty(name, out var value) &&
                value.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(value.GetString()))
                return true;
        }
        return false;
    }

    private static bool ShouldTryLegacyApprovalPolicy(Exception exception, string approvalPolicy) =>
        approvalPolicy == "never" &&
        exception.Message.Contains("invalid value", StringComparison.OrdinalIgnoreCase) &&
        exception.Message.Contains("approval", StringComparison.OrdinalIgnoreCase);

    private static ProcessStartInfo StartInfo(string command, string approvalPolicy)
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
        var arguments = new[] { "-s", "read-only", "-a", approvalPolicy, "app-server" };
        if (isCommandScript)
        {
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/s");
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add($"\"{command}\" {string.Join(' ', arguments)}");
        }
        else
        {
            foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
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

    private static JsonElement SelectWindow(JsonElement snapshot, int targetWindowMinutes)
    {
        JsonElement? secondary = null;
        foreach (var name in new[] { "primary", "secondary" })
        {
            var window = Property(snapshot, name);
            if (window is not { ValueKind: JsonValueKind.Object }) continue;
            if (name == "secondary") secondary = window;
            if (!TryDecimal(window.Value, out var minutes, "windowDurationMins", "window_duration_mins"))
                continue;

            var matches = targetWindowMinutes == WeeklyWindowMinutes
                ? minutes >= WeeklyWindowMinutes
                : minutes == targetWindowMinutes;
            if (matches) return window.Value;
        }

        if (targetWindowMinutes == WeeklyWindowMinutes && secondary is not null)
            return secondary.Value;

        throw new InvalidDataException(
            $"Codex quota response did not include a {WindowDescription(targetWindowMinutes)} window.");
    }

    private static int ResolveTargetWindowMinutes(SourceConfig config)
    {
        if (config.WindowDurationMinutes is > 0)
            return config.WindowDurationMinutes.Value;

        return config.Type.Equals("codex-five-hour", StringComparison.OrdinalIgnoreCase) ||
               config.Type.Equals("codex-5h", StringComparison.OrdinalIgnoreCase)
            ? FiveHourWindowMinutes
            : WeeklyWindowMinutes;
    }

    private static string WindowLogPrefixFor(int targetWindowMinutes) =>
        targetWindowMinutes == FiveHourWindowMinutes ? "codex-five-hour" : "codex-weekly";

    private static string WindowDescription(int targetWindowMinutes) => targetWindowMinutes switch
    {
        FiveHourWindowMinutes => "5h",
        WeeklyWindowMinutes => "weekly",
        _ => $"{targetWindowMinutes}-minute"
    };

    private static bool HasWindows(JsonElement value) => value.ValueKind == JsonValueKind.Object &&
        (Property(value, "primary") is { ValueKind: JsonValueKind.Object } ||
         Property(value, "secondary") is { ValueKind: JsonValueKind.Object });

    private static bool HasAnyWindows(JsonElement result)
    {
        var direct = Property(result, "rateLimits", "rate_limits");
        if (direct is { } directValue && HasWindows(directValue)) return true;
        var byId = Property(result, "rateLimitsByLimitId", "rate_limits_by_limit_id");
        return byId is { ValueKind: JsonValueKind.Object } &&
            byId.Value.EnumerateObject().Any(property => HasWindows(property.Value));
    }

    private static string WindowSignature(JsonElement snapshot) => string.Join("|", new[] { "primary", "secondary" }.Select(name =>
    {
        var window = Property(snapshot, name);
        if (window is not { } value) return name + ":none";
        return string.Join(":", name,
            Text(value, "usedPercent", "used_percent", "remainingPercent", "remaining_percent"),
            Text(value, "resetsAt", "resets_at"),
            Text(value, "windowDurationMins", "window_duration_mins"));
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

    private static string? TextOrNull(JsonElement value, params string[] names)
    {
        var text = Text(value, names);
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static string SafeRpcError(JsonElement error)
    {
        var message = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var value)
            ? value.GetString()
            : "Codex App Server returned an RPC error.";
        return string.IsNullOrWhiteSpace(message)
            ? "Codex App Server returned an RPC error."
            : message[..Math.Min(message.Length, 240)];
    }

    private static Exception EnrichProcessFailure(Exception exception, string stderr)
    {
        var message = SafeError(exception);
        if (!string.IsNullOrWhiteSpace(stderr))
        {
            var safeStderr = stderr.Replace('\r', ' ').Replace('\n', ' ').Trim();
            message += $"; stderr: {safeStderr[..Math.Min(safeStderr.Length, 240)]}";
        }
        return new InvalidOperationException(message, exception);
    }

    private static string SafeCandidateName(string candidate) =>
        string.IsNullOrWhiteSpace(Path.GetFileName(candidate)) ? candidate : Path.GetFileName(candidate);

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
        AddPackageBinDirectories(candidates, Path.Combine(localAppData, "Packages"));
        Add(candidates, Path.Combine(localAppData, "Programs", "Codex", "resources", "codex.exe"));
        Add(candidates, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Codex", "resources", "codex.exe"));
        Add(candidates, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Codex", "resources", "codex.exe"));
        AddWindowsAppsCandidates(candidates, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        AddWindowsAppsCandidates(candidates, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
        Add(candidates, Path.Combine(localAppData, "Microsoft", "WindowsApps", "codex.exe"));
        Add(candidates, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "codex.cmd"));
        Add(candidates, "codex.exe");
        Add(candidates, "codex.cmd");
        Add(candidates, "codex");

        return candidates
            .Where(candidate => !Path.IsPathRooted(candidate) || File.Exists(candidate))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void AddPackageBinDirectories(List<string> candidates, string packagesRoot)
    {
        try
        {
            foreach (var package in Directory.GetDirectories(packagesRoot, "OpenAI.Codex_*")
                         .OrderByDescending(PackageVersion)
                         .ThenByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
                AddBinDirectory(candidates, Path.Combine(package, "LocalCache", "Local", "OpenAI", "Codex", "bin"));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void AddWindowsAppsCandidates(List<string> candidates, string programFiles)
    {
        if (string.IsNullOrWhiteSpace(programFiles)) return;
        var windowsApps = Path.Combine(programFiles, "WindowsApps");
        try
        {
            foreach (var package in Directory.GetDirectories(windowsApps, "OpenAI.Codex_*")
                         .OrderByDescending(PackageVersion)
                         .ThenByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                Add(candidates, Path.Combine(package, "app", "resources", "codex.exe"));
                Add(candidates, Path.Combine(package, "app", "Codex.exe"));
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static Version PackageVersion(string directory)
    {
        var name = Path.GetFileName(directory);
        const string prefix = "OpenAI.Codex_";
        if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            var versionText = name[prefix.Length..].Split('_', 2)[0];
            if (Version.TryParse(versionText, out var version)) return version;
        }
        return new Version(0, 0);
    }

    private static void AddBinDirectory(List<string> candidates, string directory)
    {
        Add(candidates, Path.Combine(directory, "codex.exe"));
        try
        {
            foreach (var versionDirectory in Directory.GetDirectories(directory)
                         .OrderByDescending(File.GetLastWriteTimeUtc))
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

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Colmon;

internal sealed record TokenTodayScanResult(
    long Total,
    int FilesScanned,
    int CachedFiles,
    long BytesRead,
    int InvalidLines,
    int PartialFiles,
    int FileErrors,
    bool IsStale);

internal sealed class CodexTokenTodaySource(SourceConfig config, JsonLog log) : InfoSource(config)
{
    private const int BufferSize = 64 * 1024;
    private const int SignatureBytes = 4096;
    private const int MaximumLineBytes = 4 * 1024 * 1024;
    private const int IndexVersion = 1;
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly Dictionary<string, TokenFileScanState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private InfoSample? _lastGoodSample;
    private string? _loadedRoot;
    private bool _indexLoaded;

    public override async Task<InfoSample> ReadAsync(CancellationToken cancellationToken)
    {
        await _scanGate.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(() => Collect(cancellationToken), cancellationToken);
        }
        finally
        {
            _scanGate.Release();
        }
    }

    public override void Dispose()
    {
        _scanGate.Dispose();
    }

    private InfoSample Collect(CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var root = NormalizePath(ResolveSessionsRoot());
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Codex sessions directory was not found: {root}");

        LoadIndex(root);
        var files = Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories)
            .Select(NormalizePath)
            .ToArray();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var filesScanned = 0;
        var cachedFiles = 0;
        var invalidLines = 0;
        var partialFiles = 0;
        var fileErrors = 0;
        long bytesRead = 0;

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            seen.Add(file);
            try
            {
                var existing = _states.TryGetValue(file, out var state) ? state : null;
                var result = ScanFile(file, existing, today, cancellationToken);
                _states[file] = result.State;
                if (result.BytesRead == 0) cachedFiles++;
                else filesScanned++;
                bytesRead = SaturatingAdd(bytesRead, result.BytesRead);
                invalidLines += result.InvalidLines;
                if (result.State.PendingBytes.Length > 0) partialFiles++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                fileErrors++;
                log.Write("token-today.file.error", new
                {
                    file = Path.GetFileName(file),
                    error = SafeError(exception)
                });
            }
        }

        foreach (var missing in _states.Keys.Where(path => !seen.Contains(path)).ToArray())
        {
            _states.Remove(missing);
            log.Write("token-today.file.missing", new { file = Path.GetFileName(missing) });
        }

        var total = _states.Values.Aggregate(0L, (current, state) => SaturatingAdd(current, state.Total));
        var scan = new TokenTodayScanResult(
            total,
            filesScanned,
            cachedFiles,
            bytesRead,
            invalidLines,
            partialFiles,
            fileErrors,
            fileErrors > 0);

        if (invalidLines > 0)
            log.Write("token-today.lines.invalid", new { invalidLines, filesScanned, bytesRead });

        log.Write("token-today.collected", scan);
        SaveIndex(root);

        if (scan.IsStale)
        {
            var stale = new InfoSample(
                $"{Config.Prefix}~{total.ToString(CultureInfo.InvariantCulture)}",
                DateTimeOffset.Now,
                $"{fileErrors} Codex session file(s) could not be read.",
                IsStale: true);
            return stale;
        }

        var sample = Sample(total.ToString(CultureInfo.InvariantCulture));
        _lastGoodSample = sample;
        return sample;
    }

    private string ResolveSessionsRoot()
    {
        if (!string.IsNullOrWhiteSpace(Config.DataPath)) return Path.GetFullPath(Config.DataPath);
        var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (string.IsNullOrWhiteSpace(codexHome))
            codexHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        return Path.Combine(codexHome, "sessions");
    }

    private void LoadIndex(string root)
    {
        if (_indexLoaded && string.Equals(_loadedRoot, root, StringComparison.OrdinalIgnoreCase)) return;
        _indexLoaded = true;
        _loadedRoot = root;
        _states.Clear();

        var path = IndexPath();
        if (!File.Exists(path)) return;

        try
        {
            var document = JsonSerializer.Deserialize<TokenIndexDocument>(File.ReadAllText(path), JsonDefaults.Indented);
            if (document is null || document.Version != IndexVersion ||
                !string.Equals(document.Root, root, StringComparison.OrdinalIgnoreCase))
                return;

            foreach (var entry in document.Files)
            {
                if (!DateOnly.TryParseExact(entry.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var date))
                    continue;
                _states[NormalizePath(entry.Path)] = new TokenFileScanState
                {
                    Path = NormalizePath(entry.Path),
                    Signature = entry.Signature,
                    Offset = Math.Max(0, entry.Offset),
                    Total = Math.Max(0, entry.Total),
                    TotalDate = date,
                    LastWriteTimeUtc = entry.LastWriteTimeUtc
                };
            }
            log.Write("token-today.index.loaded", new { path = Path.GetFileName(path), files = _states.Count });
        }
        catch (Exception exception)
        {
            log.Write("token-today.index.load.error", new
            {
                path = Path.GetFileName(path),
                error = SafeError(exception)
            });
            _states.Clear();
        }
    }

    private void SaveIndex(string root)
    {
        var path = IndexPath();
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(directory)) return;
            Directory.CreateDirectory(directory);

            var entries = _states.Values.Select(state => new TokenIndexEntry(
                state.Path,
                state.Signature,
                Math.Max(0, state.Offset - state.PendingBytes.Length),
                state.Total,
                state.TotalDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                state.LastWriteTimeUtc)).ToList();
            var document = new TokenIndexDocument(IndexVersion, root, entries);
            var temporaryPath = path + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(document, JsonDefaults.Indented), new UTF8Encoding(false));
            File.Move(temporaryPath, path, overwrite: true);
        }
        catch (Exception exception)
        {
            log.Write("token-today.index.save.error", new
            {
                path = Path.GetFileName(path),
                error = SafeError(exception)
            });
        }
    }

    private static FileScanResult ScanFile(
        string file,
        TokenFileScanState? existing,
        DateOnly today,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(file);
        var signature = existing is not null &&
                        info.Length >= existing.Offset &&
                        info.LastWriteTimeUtc == existing.LastWriteTimeUtc
            ? existing.Signature
            : ComputeSignature(file, info);

        var state = existing?.Clone() ?? new TokenFileScanState { Path = file };
        if (state.Signature != signature || info.Length < state.Offset)
        {
            state.Reset(signature, today);
        }
        else if (state.TotalDate != today)
        {
            state.Total = 0;
            state.TotalDate = today;
        }

        var bytesRead = 0L;
        var invalidLines = 0;
        var line = new List<byte>(Math.Min(MaximumLineBytes, state.PendingBytes.Length + 4096));
        line.AddRange(state.PendingBytes);
        var droppingOversizedLine = false;

        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, BufferSize, FileOptions.SequentialScan);
        if (state.Offset > stream.Length)
        {
            state.Reset(signature, today);
            line.Clear();
            droppingOversizedLine = false;
        }
        stream.Position = state.Offset;
        var buffer = new byte[BufferSize];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bytesRead = SaturatingAdd(bytesRead, read);
            for (var index = 0; index < read; index++)
            {
                var value = buffer[index];
                if (value == (byte)'\n')
                {
                    if (droppingOversizedLine)
                    {
                        invalidLines++;
                        droppingOversizedLine = false;
                        line.Clear();
                        continue;
                    }

                    if (line.Count > 0 && line[^1] == (byte)'\r') line.RemoveAt(line.Count - 1);
                    ProcessLine(line, today, state, ref invalidLines);
                    line.Clear();
                    continue;
                }

                if (droppingOversizedLine) continue;
                if (line.Count >= MaximumLineBytes)
                {
                    droppingOversizedLine = true;
                    line.Clear();
                    continue;
                }
                line.Add(value);
            }
        }

        state.Offset = stream.Position;
        state.PendingBytes = droppingOversizedLine ? [] : line.ToArray();
        state.LastWriteTimeUtc = info.LastWriteTimeUtc;
        return new FileScanResult(state, bytesRead, invalidLines);
    }

    private static void ProcessLine(
        List<byte> line,
        DateOnly today,
        TokenFileScanState state,
        ref int invalidLines)
    {
        if (line.Count == 0) return;

        string json;
        try
        {
            json = StrictUtf8.GetString(line.ToArray());
        }
        catch (DecoderFallbackException)
        {
            invalidLines++;
            return;
        }

        if (!json.Contains("token_count", StringComparison.Ordinal)) return;
        try
        {
            var value = ParseTokenEvent(json, today);
            if (value is not null) state.Total = SaturatingAdd(state.Total, value.Value);
        }
        catch (JsonException)
        {
            invalidLines++;
        }
        catch (InvalidOperationException)
        {
            invalidLines++;
        }
    }

    internal static long? ParseTokenEvent(string json, DateOnly localDate)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!TryGetString(root, out var type, "type") || type != "event_msg" ||
            !TryGetString(root, out var timestampText, "timestamp") ||
            !DateTimeOffset.TryParse(timestampText, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal, out var timestamp) ||
            DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(timestamp, TimeZoneInfo.Local).Date) != localDate)
            return null;

        var payload = Property(root, "payload");
        if (payload is not { ValueKind: JsonValueKind.Object } ||
            !TryGetString(payload.Value, out var payloadType, "type") || payloadType != "token_count")
            return null;

        var info = Property(payload.Value, "info");
        var usage = info is { ValueKind: JsonValueKind.Object }
            ? Property(info.Value, "last_token_usage", "lastTokenUsage")
            : null;
        var total = usage is { ValueKind: JsonValueKind.Object }
            ? Property(usage.Value, "total_tokens", "totalTokens")
            : null;
        if (total is not { } totalValue) return null;

        if (totalValue.ValueKind == JsonValueKind.Number && totalValue.TryGetInt64(out var numeric))
            return Math.Max(0, numeric);
        if (totalValue.ValueKind == JsonValueKind.String && long.TryParse(totalValue.GetString(), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out numeric))
            return Math.Max(0, numeric);
        return null;
    }

    private static bool TryGetString(JsonElement value, out string text, params string[] names)
    {
        var property = Property(value, names);
        if (property is { ValueKind: JsonValueKind.String } && !string.IsNullOrWhiteSpace(property.Value.GetString()))
        {
            text = property.Value.GetString()!;
            return true;
        }
        text = "";
        return false;
    }

    private static JsonElement? Property(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in names)
            if (value.TryGetProperty(name, out var property)) return property;
        return null;
    }

    private string IndexPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Colmon", "cache", "codex-token-today.index.json");

    private static string ComputeSignature(string file, FileInfo info)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, SignatureBytes, FileOptions.SequentialScan);
        var buffer = new byte[Math.Min(SignatureBytes, Math.Max(1, info.Length))];
        var read = stream.Read(buffer, 0, buffer.Length);
        var hash = SHA256.HashData(buffer.AsSpan(0, read));
        return $"{info.CreationTimeUtc.Ticks}:{Convert.ToHexString(hash)}";
    }

    private static string NormalizePath(string path) => Path.GetFullPath(path);

    private static long SaturatingAdd(long left, long right) =>
        right > 0 && left > long.MaxValue - right ? long.MaxValue : left + right;

    private static string SafeError(Exception exception)
    {
        var message = exception.Message.Replace('\r', ' ').Replace('\n', ' ');
        return $"{exception.GetType().Name}: {message[..Math.Min(message.Length, 240)]}";
    }

    private sealed class TokenFileScanState
    {
        public string Path { get; init; } = "";
        public string Signature { get; set; } = "";
        public long Offset { get; set; }
        public byte[] PendingBytes { get; set; } = [];
        public long Total { get; set; }
        public DateOnly TotalDate { get; set; }
        public DateTime LastWriteTimeUtc { get; set; }

        public TokenFileScanState Clone() => new()
        {
            Path = Path,
            Signature = Signature,
            Offset = Offset,
            PendingBytes = PendingBytes.ToArray(),
            Total = Total,
            TotalDate = TotalDate,
            LastWriteTimeUtc = LastWriteTimeUtc
        };

        public void Reset(string signature, DateOnly date)
        {
            Signature = signature;
            Offset = 0;
            PendingBytes = [];
            Total = 0;
            TotalDate = date;
            LastWriteTimeUtc = default;
        }
    }

    private sealed record FileScanResult(TokenFileScanState State, long BytesRead, int InvalidLines);

    private sealed record TokenIndexDocument(int Version, string Root, List<TokenIndexEntry> Files);

    private sealed record TokenIndexEntry(
        string Path,
        string Signature,
        long Offset,
        long Total,
        string Date,
        DateTime LastWriteTimeUtc);
}

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;

namespace PCOptimizer.Services;

/// <summary>
/// "Ask about your PC": sends the question plus a snapshot of this PC's live state to Claude.
/// Uses the user's own Anthropic API key, stored encrypted for this Windows account (DPAPI),
/// never in settings.json, so it's not included in setup exports.
/// </summary>
public sealed class AiAssistant
{
    const string ModelId = "claude-opus-5-5";

    const string SystemPrompt =
        "You are the help assistant built into PC Optimizer, a Windows app for cleaning up, speeding up and gaming. " +
        "Each question comes with a snapshot of the user's PC taken just now; base your answer on it and point to the specific numbers that matter. " +
        "Be practical and brief: lead with the likely cause, then the fix as short steps. Plain language for a non-expert. " +
        "When a fix is something PC Optimizer does, name its page: Dashboard, Game Mode, Game Library, Clips & Screenshots, Clipboard & Notes, " +
        "Screen Time, Junk Cleaner, Disk Space, Uninstaller, Startup Apps, Tweaks, Right-click Menu, Processes, Hardware, Network, Security, Apps, Tools, Settings. " +
        "Never suggest registry cleaners or RAM cleaners. Say so plainly when the snapshot doesn't show enough to know, and what to check.";

    static string KeyFile => Path.Combine(
        Path.GetDirectoryName(Environment.GetEnvironmentVariable("PCOPTIMIZER_SETTINGS") is { Length: > 0 } s ? s
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PCOptimizer", "x"))!,
        "ai.key");

    readonly List<BetaMessageParam> _messages = [];

    public static bool HasKey => LoadKey() is not null;

    public static string? LoadKey()
    {
        if (Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") is { Length: > 0 } env) return env;
        try
        {
            if (!File.Exists(KeyFile)) return null;
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(KeyFile), null, DataProtectionScope.CurrentUser));
        }
        catch { return null; }
    }

    public static void SaveKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) { try { File.Delete(KeyFile); } catch { } return; }
        Directory.CreateDirectory(Path.GetDirectoryName(KeyFile)!);
        File.WriteAllBytes(KeyFile, ProtectedData.Protect(Encoding.UTF8.GetBytes(key.Trim()), null, DataProtectionScope.CurrentUser));
    }

    public void NewConversation() => _messages.Clear();

    /// <summary>Asks one question in the running conversation. Returns the answer text, or throws with a user-facing message.</summary>
    public async Task<string> AskAsync(string question, string snapshot, CancellationToken cancel)
    {
        var key = LoadKey() ?? throw new InvalidOperationException("Add your Anthropic API key first.");
        var client = new AnthropicClient { ApiKey = key };

        BetaMessageParam user = new BetaUserMessageParam
        {
            Content = $"<pc_snapshot>\n{snapshot}\n</pc_snapshot>\n\n{question}",
        };
        var request = new List<BetaMessageParam>(_messages) { user };

        BetaMessage response;
        try
        {
            response = await client.Beta.Messages.Create(new MessageCreateParams
            {
                Model = ModelId,
                MaxTokens = 16000,
                System = SystemPrompt,
                OutputConfig = new BetaOutputConfig { Effort = Effort.Low },
                // If a safety check declines the question, another model answers instead of stopping.
                Betas = ["server-side-fallback-2026-06-01"],
                Fallbacks = new BetaFallbacksParam([new BetaFallbackParam { Model = "claude-opus-4-8" }]),
                Messages = request,
            }, cancel);
        }
        catch (AnthropicUnauthorizedException) { throw new InvalidOperationException("That API key wasn't accepted. Check it in the key box."); }
        catch (AnthropicRateLimitException) { throw new InvalidOperationException("Too many requests right now. Wait a moment and try again."); }
        catch (AnthropicIOException) { throw new InvalidOperationException("Couldn't reach Anthropic. Check your internet connection."); }
        catch (AnthropicApiException ex) { throw new InvalidOperationException($"The request failed: {ex.Message}"); }

        if (response.StopReason == BetaStopReason.Refusal)
            return "I can't help with that one. Try asking about your PC's performance, storage, games or settings.";

        // Keep the whole reply (thinking blocks included) so the conversation can continue unchanged.
        var blocks = new List<BetaContentBlockParam>();
        var answer = new StringBuilder();
        foreach (var b in response.Content)
        {
            if (b.TryPickText(out var t)) { blocks.Add(new BetaTextBlockParam { Text = t.Text }); answer.Append(t.Text); }
            else if (b.TryPickThinking(out var th)) blocks.Add(new BetaThinkingBlockParam { Thinking = th.Thinking, Signature = th.Signature });
            else if (b.TryPickRedactedThinking(out var r)) blocks.Add(new BetaRedactedThinkingBlockParam { Data = r.Data });
        }
        _messages.Add(user);
        _messages.Add(new BetaAssistantMessageParam { Content = blocks });
        return answer.Length > 0 ? answer.ToString() : "(No answer came back. Try asking again.)";
    }

    /// <summary>A plain-text picture of this PC right now: hardware, load, memory hogs, disks, uptime.</summary>
    public static string Snapshot(string appFindings)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Windows: {SystemInfo.OsName()}");
        sb.AppendLine($"CPU: {SystemInfo.CpuName()}, {Environment.ProcessorCount} threads");
        var cpu = new CpuSampler(); cpu.Sample(); Thread.Sleep(500);
        using var gpu = new GpuLoad(); gpu.Sample(); Thread.Sleep(300);
        sb.AppendLine($"CPU load now: {cpu.Sample():0}%, GPU load now: {gpu.Sample():0}%");
        var (total, avail) = SystemInfo.Memory();
        sb.AppendLine($"Memory: {Fmt.Bytes(total - avail)} used of {Fmt.Bytes(total)} ({100.0 * (total - avail) / Math.Max(1, total):0}%)");
        sb.AppendLine($"Uptime: {Fmt.Duration(TimeSpan.FromMilliseconds(Environment.TickCount64))}");
        foreach (var d in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
            sb.AppendLine($"Drive {d.Name}: {Fmt.Bytes(d.AvailableFreeSpace)} free of {Fmt.Bytes(d.TotalSize)}");

        // Biggest memory users, grouped by app (Chrome runs dozens of processes).
        var procs = Process.GetProcesses();
        var top = procs.GroupBy(p => p.ProcessName, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Name: g.Key, Ram: g.Sum(p => { try { return p.WorkingSet64; } catch { return 0L; } }), Count: g.Count()))
            .OrderByDescending(x => x.Ram).Take(12);
        sb.AppendLine($"Processes running: {procs.Length}. Top memory users:");
        foreach (var (name, ram, count) in top) sb.AppendLine($"  {name}{(count > 1 ? $" ({count} processes)" : "")}: {Fmt.Bytes(ram)}");
        foreach (var p in procs) p.Dispose();

        if (appFindings.Length > 0) sb.AppendLine("PC Optimizer's own checks:").AppendLine(appFindings);
        return sb.ToString();
    }
}

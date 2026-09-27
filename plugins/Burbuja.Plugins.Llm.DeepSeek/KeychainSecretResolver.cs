using System.Collections.Concurrent;
using System.Linq;
using Burbuja.Core.Abstractions.Process;
using Burbuja.Core.Abstractions.Secrets;
using Microsoft.Extensions.Options;

namespace Burbuja.Plugins.Llm.DeepSeek;

/// <summary>
/// Resolves the API key by running a command (never a literal key in configuration). The command
/// runs through <see cref="IProcessRunner"/> under a minted execution grant and the answer is
/// cached for ten minutes.
/// </summary>
public sealed class KeychainSecretResolver(
    IProcessRunner processes,
    IExecutionGrantAuthority grants,
    IOptions<DeepSeekOptions> options) : ISecretResolver
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<string, (string? Value, DateTimeOffset ExpiresAt)> _cache = new(StringComparer.Ordinal);

    public async ValueTask<string?> ResolveAsync(string name, CancellationToken ct)
    {
        // A name this resolver does not own is NOT its secret to hand out. See `SecretNames`: the command
        // below prints one credential, so answering for an arbitrary name would give whoever asked the wrong
        // one — and "wrong secret sent to a third party" is the failure this check exists to prevent.
        if (!options.Value.SecretNames.Contains(name, StringComparer.Ordinal))
            return null;

        if (_cache.TryGetValue(name, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow)
            return cached.Value;

        var command = ExpandHome(options.Value.ApiKeyCommand);
        if (!File.Exists(command))
            return null;

        var grant = grants.Mint("deepseek-key", "secrets:" + name, TimeSpan.FromMinutes(1));
        // RedactOutput: FALSE, and that flag is the whole point of this call.
        //
        // M10 A11 — the defect it closes. This read `true`, so the process runner ran the credential command's
        // stdout through the kernel's redactor before returning it. The redactor is right to have an
        // `sk-[A-Za-z0-9]{32,}` rule — it exists so a key pasted into a log does not survive — and a real
        // DeepSeek key is long enough to match it, so what this resolver received was the literal string
        // `[redacted:api-key]` (18 characters) and what the provider sent as BOTH `x-api-key` and
        // `Authorization: Bearer` was that placeholder. Every live model call on the release host answered
        // `401 authentication_error`, and nothing in the log said why: the redaction is exactly what made the
        // failure unreadable, which is the cost of a rule applied where it does not belong.
        //
        // The boundary this restores: redaction belongs to OUTPUT — logs, transcripts, event payloads — not to
        // the one seam whose job is to resolve a credential for immediate use. The value never reaches a log
        // from here (this method's caller puts it in an HTTP header), and a resolver that cannot read the
        // secret it was asked for is not a resolver.
        var result = await processes.RunAsync(
            new ProcessStartRequest(command, [], Path.GetDirectoryName(command)!, new Dictionary<string, string>(), TimeSpan.FromSeconds(15), 64 * 1024, false),
            grant,
            ct);
        var value = result.ExitCode == 0 && result.StandardOutput.Trim().Length > 0
            ? result.StandardOutput.Trim()
            : null;
        _cache[name] = (value, DateTimeOffset.UtcNow.Add(CacheTtl));
        return value;
    }

    public static bool CommandExists(string command) => File.Exists(ExpandHome(command));

    /// <summary>
    /// `~/` to an absolute path — through <c>HOME</c>, and NOT through
    /// <c>Environment.GetFolderPath(SpecialFolder.UserProfile)</c>.
    ///
    /// <para><b>M10 A11: this one line is why a live host's model was unreachable.</b> The kernel builds with
    /// <c>InvariantGlobalization</c>, and in invariant mode <c>GetFolderPath(UserProfile)</c> returns
    /// <b>empty</b> rather than the home directory — so <c>~/.local/bin/deepseek-key</c> expanded to the
    /// RELATIVE <c>.local/bin/deepseek-key</c>, <c>File.Exists</c> said no against the host's working directory,
    /// the resolver returned null, and the provider sent an empty key: every AKOS turn and every agent turn on
    /// the release host answered <c>401 authentication_error</c> with nothing in the log to say why. Measured
    /// 2026-09-14 by calling the provider directly with the same key (200) and through the plugin (401).</para>
    ///
    /// <para><c>HOME</c> is what the LaunchAgent's own plist sets, so it is the same value the rest of the host
    /// uses to find its state directory — and it is unaffected by the invariant switch.</para>
    /// </summary>
    private static string ExpandHome(string path)
    {
        if (!path.StartsWith("~/", StringComparison.Ordinal))
            return path;
        var home = Environment.GetEnvironmentVariable("HOME");
        if (string.IsNullOrEmpty(home))
            home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
            return path;
        return Path.Combine(home, path[2..]);
    }
}

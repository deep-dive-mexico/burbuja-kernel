using System.Text.RegularExpressions;
using Burbuja.Core.Abstractions.Redaction;

namespace Burbuja.Core.Redaction;

public sealed class CaptureGroupRedactor : IRedactor
{
    internal const int PatternCount = 26;

    internal static readonly (Regex Re, string Label)[] Patterns =
    [
        (new(@"AKIA[0-9A-Z]{16}", RegexOptions.NonBacktracking | RegexOptions.Compiled), "aws-access-key"),
        (new(@"ASIA[0-9A-Z]{16}", RegexOptions.NonBacktracking | RegexOptions.Compiled), "aws-temp-key"),
        (new(@"(aws_secret_access_key\s*[=:]\s*)([A-Za-z0-9/+=]{30,})", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking | RegexOptions.Compiled), "aws-secret"),
        (new(@"(aws_session_token\s*[=:]\s*)([A-Za-z0-9/+=]{60,})", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking | RegexOptions.Compiled), "aws-session"),
        (new(@"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z ]*PRIVATE KEY-----", RegexOptions.Compiled), "pem"),
        (new(@"\bgh[pousr]_[A-Za-z0-9]{30,}\b", RegexOptions.NonBacktracking | RegexOptions.Compiled), "gh-token"),
        (new(@"\bgithub_pat_[A-Za-z0-9_]{60,}\b", RegexOptions.NonBacktracking | RegexOptions.Compiled), "gh-pat"),
        (new(@"\b[MN][A-Za-z\d]{23,}\.[\w-]{6}\.[\w-]{27,}\b", RegexOptions.NonBacktracking | RegexOptions.Compiled), "discord-token"),
        (new(@"\bsk-ant-[A-Za-z0-9_-]{20,}\b", RegexOptions.NonBacktracking | RegexOptions.Compiled), "anthropic-key"),
        (new(@"\bsk-[A-Za-z0-9]{32,}\b", RegexOptions.NonBacktracking | RegexOptions.Compiled), "api-key"),
        (new(@"\bxox[baprs]-[A-Za-z0-9-]{20,}\b", RegexOptions.NonBacktracking | RegexOptions.Compiled), "slack-token"),
        (new(@"\beyJ[A-Za-z0-9_-]{20,}\.[A-Za-z0-9_-]{20,}\.[A-Za-z0-9_-]{20,}\b", RegexOptions.NonBacktracking | RegexOptions.Compiled), "jwt"),
        (new(@"((?:password|passwd|secret|token|api[_-]?key|authorization)\s*[=:]\s*[""']?)([^\s""']{8,})", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking | RegexOptions.Compiled), "kv-secret"),
        (new(@"(Bearer\s)([A-Za-z0-9._~+/-]{20,}=*)", RegexOptions.NonBacktracking | RegexOptions.Compiled), "bearer"),
        (new(@"(Basic\s)([A-Za-z0-9+/]{16,}=*)", RegexOptions.NonBacktracking | RegexOptions.Compiled), "basic-auth"),
        (new(@"(""?(?:SecretAccessKey|SessionToken|accessToken|refreshToken|clientSecret|client_secret|private_key)""?\s*[=:]\s*""?)([^\s"",]{16,})", RegexOptions.NonBacktracking | RegexOptions.Compiled), "json-credential"),
        (new(@"(\b[A-Z0-9_]*(?:SECRET|TOKEN|PASSWORD|PASSWD|API_KEY|PRIVATE_KEY)[A-Z0-9_]*=)([^\s'""]{8,})", RegexOptions.NonBacktracking | RegexOptions.Compiled), "env-secret"),
        (new(@"\bntn_[A-Za-z0-9]{30,}\b", RegexOptions.NonBacktracking | RegexOptions.Compiled), "notion-token"),
        (new(@"\bsecret_[A-Za-z0-9]{30,}\b", RegexOptions.NonBacktracking | RegexOptions.Compiled), "notion-legacy-token"),
        (new(@"\btskey-[A-Za-z0-9-]{20,}\b", RegexOptions.NonBacktracking | RegexOptions.Compiled), "tailscale-key"),
        (new(@"\bAIza[0-9A-Za-z_-]{35}\b", RegexOptions.NonBacktracking | RegexOptions.Compiled), "google-api-key"),
        (new(@"\b[MNO][A-Za-z\d]{23,}\.[\w-]{6}\.[\w-]{27,}\b", RegexOptions.NonBacktracking | RegexOptions.Compiled), "discord-token"),
        (new(@"(://[^\s/:@]{1,64}:)([^\s@/]{6,})(@)", RegexOptions.NonBacktracking | RegexOptions.Compiled), "url-password"),
        (new(@"\b(AKIA|ASIA)[0-9A-Z]{16}\b", RegexOptions.NonBacktracking | RegexOptions.Compiled), "aws-access-key"),
        (new(@"-----BEGIN [A-Z ]*PRIVATE KEY-----", RegexOptions.NonBacktracking | RegexOptions.Compiled), "pem-begin"),
        // M14 R11 (request row X10) — E.164 phone numbers, the one PII shape this list did not carry. The
        // bounds ARE the definition: a `+`, a country code that cannot start with 0, and 7 to 15 digits in
        // total, with the neighbours checked so that a longer digit run (an id, a big integer) is NOT
        // half-redacted into a phone number it never was. Lookarounds need the backtracking engine, so this
        // pattern is the one entry here without `NonBacktracking` — deliberate, and noted because a reader
        // would otherwise "fix" it.
        (new(@"(?<![\d+])\+[1-9]\d{6,14}(?!\d)", RegexOptions.Compiled), "e164-phone"),
    ];

    public string Redact(string input)
    {
        if (string.IsNullOrEmpty(input))
            return input;

        var hits = new List<(int Start, int Length, int Pattern, string Replacement)>();
        for (var i = 0; i < Patterns.Length; i++)
        {
            var (re, label) = Patterns[i];
            foreach (Match m in re.Matches(input))
            {
                string replacement;
                if (m.Groups.Count >= 3 && m.Groups[2].Success)
                {
                    var suffix = m.Groups.Count > 3 && m.Groups[3].Success ? m.Groups[3].Value : "";
                    replacement = m.Groups[1].Value + $"[redacted:{label}]" + suffix;
                }
                else
                {
                    replacement = $"[redacted:{label}]";
                }

                hits.Add((m.Index, m.Length, i, replacement));
            }
        }

        hits.Sort((a, b) =>
        {
            var c = a.Start.CompareTo(b.Start);
            return c != 0 ? c : a.Pattern.CompareTo(b.Pattern);
        });

        var chosen = new List<(int Start, int Length, int Pattern, string Replacement)>();
        foreach (var h in hits)
        {
            if (chosen.Any(c => h.Start < c.Start + c.Length && c.Start < h.Start + h.Length))
                continue;
            chosen.Add(h);
        }

        var text = input;
        foreach (var h in chosen.OrderByDescending(h => h.Start))
            text = string.Concat(text.AsSpan(0, h.Start), h.Replacement, text.AsSpan(h.Start + h.Length));
        return text;
    }
}

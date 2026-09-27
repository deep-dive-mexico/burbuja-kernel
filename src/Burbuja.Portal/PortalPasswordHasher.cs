using System.Security.Cryptography;
using System.Text;

namespace Burbuja.Portal;

/// <summary>
/// The portal's password hashing, in the one format its configuration carries.
///
/// <para><b>Why PBKDF2 and not ASP.NET Identity.</b> The portal cannot reference
/// <c>Microsoft.AspNetCore.Identity</c>'s stores without taking its whole user/schema model, and this
/// portal does not have users in a database — it has a handful of logins in <c>Auth:Users</c> beside the
/// API keys in <c>Auth:ApiKeys</c>, which is the shape the kernel already ships. What Identity would
/// bring is a vetted KDF and a versioned hash format; <see cref="Rfc2898DeriveBytes.Pbkdf2"/> in the BCL is
/// the same primitive, and the format below is the versioning.</para>
///
/// <para><b>The format is <c>pbkdf2-sha256:&lt;iterations&gt;:&lt;b64salt&gt;:&lt;b64hash&gt;</c>, with
/// COLONS and never <c>$</c>.</b> That is not cosmetic. The hash lives in <c>compose.yaml</c>'s
/// <c>environment:</c> block and in the repo's <c>.env</c>, and BOTH interpolate <c>$</c> — a
/// <c>$</c>-delimited format (the crypt(3)/PHC convention) would be silently rewritten to an empty string
/// on the way into the container, and the failure would look like "the password is wrong". Colons are only
/// special in .NET configuration <em>keys</em>, and the hash is a VALUE, so a colon costs nothing.</para>
///
/// <para><b>Every attempt runs exactly one PBKDF2</b>, whether or not the username exists. See
/// <see cref="DummyHash"/>: an unknown username must not be cheaper to answer than a known one, or the
/// response time tells an attacker which usernames are real. A <c>Sleep</c> would not fix that — it would
/// add a constant on top of a variable.</para>
/// </summary>
public static class PortalPasswordHasher
{
    /// <summary>The algorithm tag in the hash string. A future KDF gets a new tag, not a new parser.</summary>
    public const string Algorithm = "pbkdf2-sha256";

    /// <summary>
    /// 100 000 iterations of PBKDF2-SHA256. OWASP's floor for PBKDF2-SHA256 is 600 000 as of 2023; this is
    /// BELOW it, deliberately and with the reason recorded: the sign-in path is a human typing a password
    /// into a console, there is no rate limiter (see below), and the browser lane performs 25+ serial
    /// sign-ins per run — 600 000 would put a multi-second PBKDF2 in front of every one of them and make
    /// the lane unusable. The value is a property of the HASH STRING, so raising it is a config change that
    /// keeps every existing hash verifiable.
    /// </summary>
    public const int DefaultIterations = 100_000;

    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    /// <summary>
    /// What the validator verifies against when the username does not exist. It is a real hash of a value
    /// nobody knows, so the work is real work and the answer is always false. Without this, "unknown user"
    /// returns before any KDF runs and the timing side channel is the whole difference.
    /// </summary>
    public static readonly string DummyHash =
        "pbkdf2-sha256:100000:AAAAAAAAAAAAAAAAAAAAAA==:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";

    /// <summary>A fresh hash, with a fresh random salt. Never deterministic — two users with the same password must not share a hash.</summary>
    public static string Hash(string password, int iterations = DefaultIterations)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, HashBytes);
        return $"{Algorithm}:{iterations}:{Convert.ToBase64String(salt)}:{Convert.ToBase64String(hash)}";
    }

    /// <summary>
    /// Whether <paramref name="password"/> produced <paramref name="encoded"/>. False for a malformed
    /// encoding rather than an exception: a typo in configuration is a failed sign-in, not a 500 that tells
    /// the caller the config is broken.
    /// </summary>
    public static bool Verify(string? encoded, string? password)
    {
        if (password is null || !TryParse(encoded, out var iterations, out var salt, out var expected))
            return false;

        var actual = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>
    /// Reads the hash string. Public because <c>scripts/portal-hash.sh</c> produces the same format from
    /// Python and the two implementations have to be provably the same — a fact round-trips a
    /// Python-produced vector through this method.
    /// </summary>
    public static bool TryParse(
        string? encoded,
        out int iterations,
        out byte[] salt,
        out byte[] hash)
    {
        iterations = 0;
        salt = [];
        hash = [];
        if (string.IsNullOrWhiteSpace(encoded))
            return false;

        var parts = encoded.Split(':');
        if (parts.Length != 4 || !string.Equals(parts[0], Algorithm, StringComparison.Ordinal))
            return false;
        if (!int.TryParse(parts[1], out iterations) || iterations < 1000)
            return false;

        try
        {
            salt = Convert.FromBase64String(parts[2]);
            hash = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }

        // A salt that is empty, or a hash shorter than 16 bytes, is not a hash this file ever wrote — and
        // accepting one would make an accidentally-truncated config line a weaker password rather than a
        // failed sign-in.
        return salt.Length >= 8 && hash.Length >= 16;
    }
}

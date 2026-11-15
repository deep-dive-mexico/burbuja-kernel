using System.Security.Cryptography;
using System.Text;

namespace Burbuja.Cluster.Security;

/// <summary>
/// Message authentication between a node and its controller.
///
/// <para><b>No secret crosses the wire, in either direction.</b> A join token is shown once to an operator and
/// carried to the node out of band. Both sides derive a join key from it, the node signs its join request with
/// that key, and both then derive the node's long-lived key from the join key and two nonces. Every later
/// request is signed with the node key and every response is signed back over the request's nonce, so each
/// side proves it holds the key and a body cannot be altered in transit. This matters because the transport is
/// whatever network the operator has: an overlay that encrypts, or a plain LAN that does not.</para>
///
/// <para><b>It authenticates and protects integrity; it does not hide content.</b> Use a network that encrypts,
/// or HTTPS, when workload specifications are themselves sensitive.</para>
/// </summary>
internal static class ClusterSigner
{
    public const string NodeHeader = "X-Burbuja-Node";
    public const string TimestampHeader = "X-Burbuja-Timestamp";
    public const string NonceHeader = "X-Burbuja-Nonce";
    public const string SignatureHeader = "X-Burbuja-Signature";

    /// <summary>How far a request's clock may differ from the controller's before it is refused as a replay risk.</summary>
    public static readonly TimeSpan MaxSkew = TimeSpan.FromSeconds(90);

    private const string TokenPrefix = "bcj";

    public static string NewSecret(int bytes = 32) => Base64Url(RandomNumberGenerator.GetBytes(bytes));

    public static string NewId(int bytes = 8) => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(bytes));

    /// <summary>A join token: a public id to find the record by and a secret that never leaves the two machines.</summary>
    public static (string Token, string TokenId, byte[] JoinKey) NewJoinToken()
    {
        var id = NewId();
        var secret = NewSecret();
        return ($"{TokenPrefix}_{id}_{secret}", id, JoinKey(secret));
    }

    public static bool TryParseJoinToken(string token, out string tokenId, out byte[] joinKey)
    {
        tokenId = "";
        joinKey = [];
        var parts = token.Trim().Split('_', 3);
        if (parts.Length != 3 || parts[0] != TokenPrefix || parts[1].Length != 16 || parts[2].Length < 32) return false;
        tokenId = parts[1];
        joinKey = JoinKey(parts[2]);
        return true;
    }

    private static byte[] JoinKey(string secret) =>
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), "burbuja-cluster-join-v1"u8);

    public static byte[] NodeKey(byte[] joinKey, string clusterId, string nodeId, string clientNonce, string serverNonce) =>
        HMACSHA256.HashData(joinKey, Encoding.UTF8.GetBytes($"burbuja-cluster-node-v1\n{clusterId}\n{nodeId}\n{clientNonce}\n{serverNonce}"));

    public static string SignRequest(byte[] key, string method, string path, string timestamp, string nonce, ReadOnlySpan<byte> body) =>
        Mac(key, $"request\n{method.ToUpperInvariant()}\n{path}\n{timestamp}\n{nonce}\n{Convert.ToHexStringLower(SHA256.HashData(body))}");

    public static string SignResponse(byte[] key, string requestNonce, int status, ReadOnlySpan<byte> body) =>
        Mac(key, $"response\n{requestNonce}\n{status}\n{Convert.ToHexStringLower(SHA256.HashData(body))}");

    public static bool Matches(string expected, string? actual) =>
        actual is not null && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(actual));

    /// <summary>Compares bearer credentials without revealing where they differ or how long the real one is.</summary>
    public static bool BearerMatches(string expectedToken, string? authorizationHeader) =>
        CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes("Bearer " + expectedToken)),
            SHA256.HashData(Encoding.UTF8.GetBytes(authorizationHeader ?? "")));

    private static string Mac(byte[] key, string message) => Base64Url(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(message)));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>
/// Remembers recent request nonces for one node so a captured request cannot be sent again inside the clock
/// window. Bounded: the window is short, so the oldest entries are the ones that have already expired.
/// </summary>
internal sealed class NonceWindow(int capacity = 512)
{
    private readonly Dictionary<string, DateTimeOffset> _seen = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();

    public bool TryAccept(string nonce, DateTimeOffset now)
    {
        if (nonce.Length is < 16 or > 128 || !_seen.TryAdd(nonce, now)) return false;
        _order.Enqueue(nonce);
        while (_order.Count > capacity) _seen.Remove(_order.Dequeue());
        return true;
    }
}

/// <summary>Owner-only files for credentials and state.</summary>
internal static class PrivateFiles
{
    public static void EnsureDirectory(string path)
    {
        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    /// <summary>Replace a file atomically: a reader sees the old content or the new, never a partial write.</summary>
    public static void WriteAtomic(string path, string content)
    {
        var temporary = path + "." + ClusterSigner.NewId(4) + ".tmp";
        File.WriteAllText(temporary, content);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>Read a token file, creating it with a fresh secret when it does not exist yet.</summary>
    public static string ReadOrCreateToken(string path)
    {
        if (!File.Exists(path)) WriteAtomic(path, ClusterSigner.NewSecret());
        var token = File.ReadAllText(path).Trim();
        if (token.Length < 32) throw new InvalidOperationException($"The token in '{path}' must be at least 32 characters.");
        return token;
    }
}

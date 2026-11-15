using Burbuja.Cluster.Security;

namespace Burbuja.Cluster;

/// <summary>
/// The operator's bearer token for the controller API. It lives in a file only the node's account can read;
/// when the operator names none, one is created in the state directory so a fresh install is never open.
/// </summary>
internal sealed class ClusterTokens
{
    public ClusterTokens(ClusterOptions options)
    {
        var file = options.Controller.OperatorTokenFile;
        if (file.Length == 0 && options.StateDirectory.Length > 0)
        {
            PrivateFiles.EnsureDirectory(options.StateDirectory);
            file = Path.Combine(options.StateDirectory, "operator.token");
        }

        OperatorTokenFile = file.Length > 0 ? file : null;
        OperatorToken = file.Length > 0 ? PrivateFiles.ReadOrCreateToken(file) : ClusterSigner.NewSecret();
    }

    public string OperatorToken { get; }
    public string? OperatorTokenFile { get; }
}

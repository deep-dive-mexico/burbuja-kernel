using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Abstractions.Json;
using Burbuja.Core.Arm;

namespace Burbuja.Core.Sdk.Applications;

public sealed record ApplicationRuntimeCall(ArmClientRequest Request, ArmPrincipal Principal);

public sealed partial class ConnectedKernelClient
{
    /// <summary>The principal is resolved per call from the application's authenticated entry.</summary>
    public IArmClient Arm(Func<ArmPrincipal> principal) => new ApplicationArmClient(this, principal);

    private sealed class ApplicationArmClient(ConnectedKernelClient connection, Func<ArmPrincipal> principal) : IArmClient
    {
        public async ValueTask<ArmClientResponse> SendAsync(ArmClientRequest request, CancellationToken ct)
        {
            if (!connection._verified) throw new InvalidOperationException("Connect first.");
            using var message = connection.Message(HttpMethod.Post, "runtime/arm");
            message.Content = new StringContent(JsonSerializer.Serialize(new ApplicationRuntimeCall(request, principal()),
                JsonWire.Options), Encoding.UTF8, "application/json");
            try
            {
                using var response = await connection._http.SendAsync(message, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                return await JsonSerializer.DeserializeAsync<ArmClientResponse>(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false),
                    JsonWire.Options, ct).ConfigureAwait(false) ?? throw new JsonException();
            }
            catch (HttpRequestException error) { throw new KernelUnavailableException("runtime.arm", request.Headers?.GetValueOrDefault("x-ms-client-request-id"), error); }
        }

        public async IAsyncEnumerable<ArmSseEvent> StreamAsync(string path, string? lastEventId, [EnumeratorCancellation] CancellationToken ct)
        {
            if (!connection._verified) throw new InvalidOperationException("Connect first.");
            var headers = new Dictionary<string, string> { ["Accept"] = "text/event-stream" };
            if (lastEventId is not null) headers["Last-Event-ID"] = lastEventId;
            using var message = connection.Message(HttpMethod.Post, "runtime/stream");
            message.Content = new StringContent(JsonSerializer.Serialize(new ApplicationRuntimeCall(
                new ArmClientRequest("GET", path, Headers: headers), principal()), JsonWire.Options), Encoding.UTF8, "application/json");
            HttpResponseMessage response;
            try
            {
                response = await connection._http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException error) { throw new KernelUnavailableException("runtime.stream", null, error); }
            using (response)
            {
                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException(await response.Content.ReadAsStringAsync(ct), null, response.StatusCode);
                await foreach (var evt in HttpArmClient.ReadEventsAsync(response, ct)) yield return evt;
            }
        }
    }
}

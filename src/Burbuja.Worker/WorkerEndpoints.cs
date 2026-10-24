using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Burbuja.Worker;

public static class WorkerEndpoints
{
    public static void MapWorker(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<WorkerOptions>();
        var token = SHA256.HashData(Encoding.UTF8.GetBytes("Bearer " + File.ReadAllText(options.TokenFile).Trim()));
        app.Use(async (context, next) =>
        {
            var actual = SHA256.HashData(Encoding.UTF8.GetBytes(context.Request.Headers.Authorization.ToString()));
            if (!CryptographicOperations.FixedTimeEquals(actual, token)) { context.Response.StatusCode = 401; return; }
            await next(context);
        });
        app.MapGet("/workloads/v1/status", (FileWorkloadRunner runner) => Results.Json(runner.Status));
        app.MapPost("/workloads/v1/{name}/run", async (string name, HttpContext context, FileWorkloadRunner runner) =>
        {
            try
            {
                var header = context.Request.Headers["X-Workload-Parameters"].ToString();
                if (header.Length > 16384) return Results.BadRequest();
                var parameters = string.IsNullOrEmpty(header) ? new Dictionary<string, string>() :
                    JsonSerializer.Deserialize<Dictionary<string, string>>(Convert.FromBase64String(header));
                if (parameters is null || parameters.Any(p => p.Value is null)) return Results.BadRequest();
                using var stopped = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, app.Lifetime.ApplicationStopping);
                var result = await runner.RunAsync(name, context.Request.Body, parameters, stopped.Token);
                return Results.Bytes(result, "application/json");
            }
            catch (WorkloadBusyException) { context.Response.Headers.RetryAfter = "15"; return Results.StatusCode(429); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (Exception e) when (e is ArgumentException or FormatException or JsonException or InvalidDataException)
            { return Results.BadRequest(new { error = "invalid_workload_request" }); }
            catch (OperationCanceledException) { return Results.StatusCode(504); }
            catch (IOException) { return Results.StatusCode(502); }
        });
    }
}

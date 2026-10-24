using Burbuja.Worker;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:18510");
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 256 * 1024 * 1024);
builder.Logging.ClearProviders(); // No request options, audio, credentials or transcripts in logs.
var options = builder.Configuration.GetSection("Worker").Get<WorkerOptions>() ?? new();
options.Validate();
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<FileWorkloadRunner>();
var app = builder.Build();
app.MapWorker();
app.Run();

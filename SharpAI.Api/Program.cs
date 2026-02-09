
namespace SharpAI.Api
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            // Read appsettings configuration as Appsettings DTO
            var appsettingsSection = builder.Configuration.GetSection("Appsettings");
            var appsettings = appsettingsSection.Get<SharpAI.Shared.Appsettings>() ?? new SharpAI.Shared.Appsettings();
            bool autoLoadLlama = builder.Configuration.GetValue<bool>("AutoLoadLlama");

            // CORS policy
            const string CorsPolicy = "AllowApi";
            builder.Services.AddCors(options =>
            {
                options.AddPolicy(CorsPolicy, policy =>
                {
                    policy
                        .AllowAnyHeader()
                        .AllowAnyMethod()
                        .AllowCredentials()
                        .SetIsOriginAllowed(_ => true);
                });
            });

            // Add services to the container.
            builder.Services.AddSingleton(appsettings);
            builder.Services.AddSingleton<SharpAI.Runtime.LlamaService>(sp => new Runtime.LlamaService(appsettings.LlamaModelDirectories.ToArray(), appsettings?.AutoLoadLlama == true ? appsettings?.DefaultLlamaModel : null, appsettings?.PreferredLlamaBackend ?? "CPU", appsettings != null ? appsettings.MaxContextTokens : 1024, appsettings?.DefaultContext, string.Join(" ", appsettings?.SystemPrompts.Select(p => p.Trim()) ?? [])));
            builder.Services.AddSingleton<SharpAI.Runtime.OnnxService>(sp => new Runtime.OnnxService(appsettings.WhisperModelDirectories.ToArray()));
            builder.Services.AddSingleton<SharpAI.Core.ImageCollection>(sp => new SharpAI.Core.ImageCollection(false, appsettings.RessourceImagePaths.ToArray()));
            builder.Services.AddSingleton<SharpAI.Core.AudioHandling>(sp => new SharpAI.Core.AudioHandling(appsettings.CustomAudioExportDirectory, appsettings.RessourceAudioPaths.ToArray()));
            builder.Services.AddSingleton<SharpAI.Core.LmStudioService>();
            builder.Services.AddSingleton<SharpAI.Runtime.WhisperService>();
            builder.Services.AddSingleton<SharpAI.StableDiffusion.StableDiffusionService>();

            builder.Services.AddControllers();
            builder.Services.AddSignalR();
            builder.Services.AddHostedService<SharpAI.Api.Services.LogBroadcastService>();
            builder.Services.AddEndpointsApiExplorer();
            builder.Services.AddSwaggerGen();

            var app = builder.Build();

            // Configure the HTTP request pipeline.
            if (app.Environment.IsDevelopment())
            {
                app.UseSwagger();
                app.UseSwaggerUI();
            }

            app.UseHsts();
            app.UseHttpsRedirection();

            app.UseCors(CorsPolicy);

            // Logging Middleware — noisy polling endpoints only go to console, not the UI log
            app.Use(async (context, next) =>
            {
                try
                {
                    var req = context.Request;
                    var qs = string.IsNullOrEmpty(req.QueryString.Value) ? string.Empty : req.QueryString.Value;
                    var path = req.Path.Value ?? "";
                    // Suppress frequent polling endpoints from UI log (still printed to console)
                    bool suppress = path.Contains("/status", StringComparison.OrdinalIgnoreCase)
                                 || path.Contains("/onnx-status", StringComparison.OrdinalIgnoreCase)
                                 || path.Contains("/loghub", StringComparison.OrdinalIgnoreCase)
                                 || path.Contains("/log/binding", StringComparison.OrdinalIgnoreCase)
                                 || path.Contains("/negotiate", StringComparison.OrdinalIgnoreCase);
                    var incoming = $"[API] Incoming: {req.Method} {path}{qs} from {context.Connection.RemoteIpAddress}";
                    Console.WriteLine(incoming);
                    if (!suppress)
                    {
                        SharpAI.Core.StaticLogger.Log(incoming);
                    }

                    await next();
                    var outgoing = $"[API] Response: {context.Response.StatusCode} for {req.Method} {path}{qs}";
                    Console.WriteLine(outgoing);
                    if (!suppress)
                    {
                        SharpAI.Core.StaticLogger.Log(outgoing);
                    }
                }
                catch (Exception ex)
                {
                    var error = $"[API] Request logging middleware error: {ex}";
                    Console.WriteLine(error);
                    SharpAI.Core.StaticLogger.Log(error);
                    throw;
                }
            });

            app.UseAuthorization();
            app.MapControllers();
            app.MapHub<SharpAI.Api.Hubs.LogHub>("/loghub");

            app.Run();
        }
    }
}

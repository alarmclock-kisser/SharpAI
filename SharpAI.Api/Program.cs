
namespace SharpAI.Api
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

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
            builder.Services.AddSingleton<SharpAI.Runtime.LlamaService>();
            builder.Services.AddSingleton < SharpAI.Core.ImageCollection>();

            builder.Services.AddControllers();
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

            // Logging Middleware hinzufügen
            app.Use(async (context, next) =>
            {
                try
                {
                    var req = context.Request;
                    var qs = string.IsNullOrEmpty(req.QueryString.Value) ? string.Empty : req.QueryString.Value;
                    Console.WriteLine($"[API] Incoming: {req.Method} {req.Path}{qs} from {context.Connection.RemoteIpAddress}");
                    await next();
                    Console.WriteLine($"[API] Response: {context.Response.StatusCode} for {req.Method} {req.Path}{qs}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[API] Request logging middleware error: {ex}");
                    throw;
                }
            });

            app.UseAuthorization();
            app.MapControllers();

            app.Run();
        }
    }
}

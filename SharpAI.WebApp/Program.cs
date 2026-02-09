using SharpAI.Client;
using SharpAI.WebApp.Components;
using SharpAI.WebApp.ViewModels;

namespace SharpAI.WebApp
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            // CORS für API-Zugriff
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

            // HttpClient für API
            var apiBase = builder.Configuration.GetValue<string>("ApiBaseUrl") ?? "https://localhost:7105/";
            var timeout = builder.Configuration.GetValue<int?>("MaxTimeout") ?? 300;
            var httpClient = new HttpClient
            {
                BaseAddress = new Uri(apiBase),
                Timeout = TimeSpan.FromSeconds(timeout)
            };
            builder.Services.AddSingleton<ApiClient>(provider => new ApiClient(httpClient));

            // HTTPS-Umleitung und HSTS aktivieren
            builder.Services.AddHttpsRedirection(options =>
            {
                options.RedirectStatusCode = StatusCodes.Status308PermanentRedirect;
            });

            builder.Services.AddHsts(options =>
            {
                options.Preload = true;
                options.IncludeSubDomains = true;
                options.MaxAge = TimeSpan.FromDays(365);
            });

            // Antiforgery-Cookie für die Sicherstellung von SameSite-Attributen
            builder.Services.AddAntiforgery(options =>
            {
                options.Cookie.SameSite = SameSiteMode.None;
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                options.Cookie.HttpOnly = true;
                options.HeaderName = "X-CSRF-TOKEN";
            });

            builder.Services.AddRazorPages();
            builder.Services.AddServerSideBlazor();
            builder.Services.AddSignalR();
            builder.Services.AddScoped<ChatViewModel>();
            builder.Services.AddScoped<ModelViewModel>();
            builder.Services.AddScoped<ContextViewModel>();
            builder.Services.AddScoped<ImageViewModel>();
            builder.Services.AddScoped<AudioViewModel>();
            builder.Services.AddScoped<OnnxWhisperViewModel>();
            builder.Services.AddScoped<WhisperViewModel>();

            var app = builder.Build();

            // HTTP-Pipeline konfigurieren
            if (!app.Environment.IsDevelopment())
            {
                app.UseExceptionHandler("/Error");
            }

            app.UseHsts();
            app.UseHttpsRedirection();

            app.UseStaticFiles();

            app.UseRouting();

            app.UseCors(CorsPolicy);

            app.UseAntiforgery();

            // WebSockets für Blazor verwenden
            app.UseWebSockets();
            app.UseAuthorization();

            // Blazor Server-Endpunkte
            app.MapBlazorHub();
            app.MapFallbackToPage("/_Host");

            app.Run();
        }
    }
}

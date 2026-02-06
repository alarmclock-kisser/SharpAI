using System;
using System.Collections.Generic;
using System.Text;
using SharpAI.Shared;

namespace SharpAI.Core
{
    /// <summary>
    /// This class uses local LM Studio REST-API to manage models, generate text, and handle other interactions with the LM Studio backend. It serves as a bridge between the application and the LM Studio API, providing methods to perform various operations such as loading models, generating responses, and managing settings.
    /// </summary>
    public class LmStudioService
    {
        public readonly string LmStudioApiBaseUrl = "http://localhost:1234/api";

        private readonly HttpClient Http;


        public LmStudioService(string? differentApiUrl = null, int timeoutSeconds = 300)
        {
            if (!string.IsNullOrEmpty(differentApiUrl))
            {
                LmStudioApiBaseUrl = differentApiUrl;
            }

            Http = new HttpClient()
            {
                BaseAddress = new Uri(LmStudioApiBaseUrl),
                Timeout = TimeSpan.FromSeconds(timeoutSeconds),
                MaxResponseContentBufferSize = 1024 * 1024 * 64 // 64 MB
            };
        }

        public async Task<List<LmStudioModel>> GetModelsAsync()
        {
            try
            {
                var response = await Http.GetAsync("/models");
                response.EnsureSuccessStatusCode();
                var content = await response.Content.ReadAsStringAsync();
                var models = System.Text.Json.JsonSerializer.Deserialize<List<LmStudioModel>>(content, new System.Text.Json.JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
                return models ?? new List<LmStudioModel>();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error fetching models: {ex.Message}");
                return new List<LmStudioModel>();

            }
        }


    }
}

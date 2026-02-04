using SharpAI.Shared;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;

namespace SharpAI.Client
{
    public class ApiClient
    {
        //private readonly InternalApiClient internalClient;
        private readonly HttpClient httpClient;
        private readonly string baseUrl;
        private readonly InternalClient internalClient;


        public string? LastErrorMessage { get; private set; }


        public string BaseUrl => this.baseUrl;


        public ApiClient(HttpClient httpClient)
        {
            this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            this.baseUrl = httpClient.BaseAddress?.ToString().TrimEnd('/') ?? throw new InvalidOperationException("HttpClient.BaseAddress is not set. Configure it in DI registration.");
            this.internalClient = new InternalClient(this.baseUrl, this.httpClient);
        }




        // ImageController
        public async Task<List<ImageObjInfo>?> ListImagesAsync()
        {
            try
            {
                var resp = await this.internalClient.ListAsync();
                return resp?.ToList();
            }
            catch (ApiException)
            {
                return null;
            }
        }

        public async Task<ImageObjInfo?> UploadImageAsync(FileParameter fParam)
        {
            try
            {
                await using var contentStream = fParam.Data;
                using var content = new MultipartFormDataContent();
                var streamContent = new StreamContent(contentStream);
                if (!string.IsNullOrWhiteSpace(fParam.ContentType))
                {
                    streamContent.Headers.ContentType = new MediaTypeHeaderValue(fParam.ContentType);
                }
                content.Add(streamContent, "file", fParam.FileName);

                var resp = await this.httpClient.PostAsync("api/Image/upload-file", content);
                if (!resp.IsSuccessStatusCode)
                {
                    return null;
                }
                return await resp.Content.ReadFromJsonAsync<ImageObjInfo>();
            }
            catch (Exception)
            {
                return null;
            }
        }

        public async Task<bool> DeleteImageAsync(Guid id)
        {
            try
            {
                var primary = await this.httpClient.DeleteAsync($"api/Image/{id}");
                if (primary.IsSuccessStatusCode)
                {
                    return true;
                }
                var fallback = await this.httpClient.DeleteAsync($"api/Image/delete?id={id}");
                return fallback.IsSuccessStatusCode;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public async Task<string> GetThumbnailAsync(string base64imageData, int pxDiagonal = 128)
        {
            try
            {
                string thumbnail = await this.internalClient.ThumbnailFromDataAsync(base64imageData, pxDiagonal);
                return thumbnail;
            }
            catch (ApiException ex)
            {
                return "Error: " + ex.ToString();
            }
        }

        public async Task<ImageObjData?> GetImageDataAsync(Guid id)
        {
            try
            {
                return await this.internalClient.DataAsync(id);
            }
            catch (Exception)
            {
                return null;
            }
        }


        // Llama Models, load & unload
        public async Task<LlamaModelFile?> GetModelStatusAsync(CancellationToken ct = default)
        {
            try
            {
                return await this.internalClient.StatusAsync(ct);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                return null;
            }
        }

        public async Task<ICollection<LlamaModelFile>?> GetLlamaModelFilesAsync()
        {
            try
            {
                return await this.internalClient.List2Async();
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                return null;
            }
        }

        public async Task<LlamaModelFile?> LoadModelAsync(LlamaModelLoadRequest loadRequest, bool fuzzyMatch = false, bool forceUnload = true, CancellationToken ct = default)
        {
            try
            {
                this.LastErrorMessage = null;
                return await this.internalClient.LoadAsync(fuzzyMatch, forceUnload, loadRequest, ct);
            }
            catch (Exception ex)
            {
                this.LastErrorMessage = GetExceptionMessage(ex);
                Console.WriteLine(ex);
                return null;
            }
        }

        private static string GetExceptionMessage(Exception ex)
        {
            var responseProperty = ex.GetType().GetProperty("Response");
            if (responseProperty?.GetValue(ex) is string response && !string.IsNullOrWhiteSpace(response))
            {
                return response;
            }

            return ex.Message;
        }

        public async Task<bool> UnloadModelAsync()
        {
            try
            {
                var ok = await this.internalClient.UnloadAsync();
                return ok ?? false;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                return false;
            }
        }


        // Llama contexts get, create, load, save, delete
        public async Task<LlamaContextData?> GetCurrentContextAsync()
        {
            try
            {
                var response = await this.internalClient.CurrentAsync();
                return response;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                return null;
            }
        }

        public async Task<ICollection<LlamaContextData>?> GetAllContextsAsync()
        {
            try
            {
                var response = await this.internalClient.List3Async();
                return response;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                return null;
            }
        }

        public async Task<string?> CreateContextAsync(bool use = true, bool save = false)
        {
            try
            {
                var response = await this.internalClient.CreateAsync(use, save);
                return response;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                return null;
            }
        }

        public async Task<LlamaContextData?> LoadContextAsync(string contextNameOrPath)
        {
            try
            {
                var response = await this.internalClient.Load2Async(contextNameOrPath);
                return response;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                return null;
            }
        }

        public async Task<string?> SaveContextAsync(string? differentNameOrPath = null)
        {
            try
            {
                var ok = await this.internalClient.SaveAsync(differentNameOrPath);
                return ok;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                return null;
            }
        }

        public async Task<bool> DeleteContextAsync(string contextNameOrPath)
        {
            try
            {
                var ok = await this.internalClient.Delete2Async(contextNameOrPath);
                return ok ?? false;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                return false;
            }
        }


        // Llama generate, text (+ simple overloads)
        public async Task<ICollection<string>?> GenerateAsync(LlamaGenerationRequest generateRequest, CancellationToken ct = default)
        {
            try
            {
                var response = await this.internalClient.GenerateAsync(generateRequest, ct);
                return response;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                return null;
            }
        }

        public async Task<ICollection<string>?> GenerateSimpleAsync(string prompt, int maxTokens = 1024, float temperature = 0.7f, float topP = 0.95f, CancellationToken ct = default)
        {
            try
            {
                var response = await this.internalClient.SimpleAllAsync(prompt, maxTokens, temperature, topP, ct);
                return response;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                return null;
            }
        }

        public async Task<string?> GenerateTextAsync(LlamaGenerationRequest generateRequest, CancellationToken ct = default)
        {
            try
            {
                var response = await this.internalClient.TextAsync(generateRequest, ct);
                return response;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                return null;
            }
        }

        public async Task<string?> GenerateTextSimpleAsync(string prompt, int maxTokens = 1024, float temperature = 0.7f, float topP = 0.95f, CancellationToken ct = default)
        {
            try
            {
                var response = await this.internalClient.SimplePOST2Async(prompt, maxTokens, temperature, topP, ct);
                return response;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                return null;
            }
        }


        // Llama SSE streaming generate
        public async IAsyncEnumerable<string> GenerateStreamAsync(LlamaGenerationRequest generateRequest, [EnumeratorCancellation] CancellationToken ct = default)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "generate/stream")
            {
                Content = JsonContent.Create(generateRequest)
            };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

            HttpResponseMessage? response = null;
            var failed = false;
            try
            {
                response = await this.httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                response?.Dispose();
                failed = true;
            }

            if (failed || response == null)
            {
                yield break;
            }

            using (response)
            {
                await foreach (var item in ReadSseAsync(response, ct).ConfigureAwait(false))
                {
                    yield return item;
                }
            }
        }

        public async IAsyncEnumerable<string> GenerateStreamSimpleAsync(string prompt, int maxTokens = 1024, float temperature = 0.7f, float topP = 0.95f, [EnumeratorCancellation] CancellationToken ct = default)
        {
            var url = $"generate/stream/simple?prompt={Uri.EscapeDataString(prompt)}&maxTokens={maxTokens}&temperature={temperature.ToString(CultureInfo.InvariantCulture)}&topP={topP.ToString(CultureInfo.InvariantCulture)}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

            HttpResponseMessage? response = null;
            var failed = false;
            try
            {
                response = await this.httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                response?.Dispose();
                failed = true;
            }

            if (failed || response == null)
            {
                yield break;
            }

            using (response)
            {
                await foreach (var item in ReadSseAsync(response, ct).ConfigureAwait(false))
                {
                    yield return item;
                }
            }
        }

        private static async IAsyncEnumerable<string> ReadSseAsync(HttpResponseMessage response, [EnumeratorCancellation] CancellationToken ct)
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(stream);
            var dataBuilder = new StringBuilder();

            string? line;
            while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) != null)
            {
                ct.ThrowIfCancellationRequested();

                if (line.Length == 0)
                {
                    if (dataBuilder.Length > 0)
                    {
                        yield return dataBuilder.ToString();
                        dataBuilder.Clear();
                    }
                    continue;
                }

                if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    var payload = line.Length > 5 ? line[5..] : string.Empty;
                    if (payload.StartsWith(" ", StringComparison.Ordinal))
                    {
                        payload = payload[1..];
                    }

                    dataBuilder.Append(payload);
                }
            }

            if (dataBuilder.Length > 0)
            {
                yield return dataBuilder.ToString();
            }
        }



        // Backends
        public async Task<ICollection<string>?> GetAvailableBackendsAsync()
        {
            try
            {
                var response = await this.internalClient.BackendsAsync();
                return response;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                return null;
            }
        }

        public async Task<string?> GetCudaVersionAsync()
        {
            try
            {
                var response = await this.internalClient.CudaAsync();
                return response;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                return null;
            }
        }

    }
}

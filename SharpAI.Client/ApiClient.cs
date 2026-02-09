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

        private CancellationTokenSource? CtsWhisper = null;
        private Appsettings? cachedAppsettings = null;


        public string? LastErrorMessage { get; private set; }


        public string BaseUrl => this.baseUrl;


        public ApiClient(HttpClient httpClient)
        {
            this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            this.baseUrl = httpClient.BaseAddress?.ToString().TrimEnd('/') ?? throw new InvalidOperationException("HttpClient.BaseAddress is not set. Configure it in DI registration.");
            this.internalClient = new InternalClient(this.baseUrl, this.httpClient);
        }


        // Appsettings
        public async Task<Appsettings?> GetAppsettingsAsync(bool useCache = true)
        {
            if (useCache && this.cachedAppsettings != null)
            {
                return this.cachedAppsettings;
            }

            try
            {
                var response = await this.internalClient.AppsettingsAsync();
                if (useCache)
                {
                    this.cachedAppsettings = response;
                }
                return response;
            }
            catch (ApiException ex) when (ex.StatusCode == 204)
            {
                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                return null;
            }
        }



        // LogController
        public async Task<ICollection<string>?> GetLogsBindingAsync()
        {
            try
            {
                var response = await this.internalClient.BindingAsync();
                return response;
            }
            catch (ApiException ex) when (ex.StatusCode == 204)
            {
                return [];
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                return null;
            }
        }

        public async Task<IDictionary<string, string>?> GetLogsEntriesAsync()
        {
            try
            {
                var response = await this.internalClient.EntriesAsync();
                return response;
            }
            catch (ApiException ex) when (ex.StatusCode == 204)
            {
                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                return null;
            }
        }

        public async Task<bool?> LogMessageAsync(string logMessage)
        {
            try
            {
                var response = await this.internalClient.MessageAsync(logMessage);
                return response;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                return null;
            }
        }


        // AudioController
        public async Task<ICollection<AudioObjInfo>> GetAudiosAsync(int? waveformWidth = null, int? waveformHeight = null)
        {
            try
            {
                // If width and height are null, no waveforms will be included. If both have values, waveforms will be included with the specified dimensions.
                // If only one has value, it will be ignored and no waveforms will be included.
                bool includeWaveforms = waveformWidth.HasValue && waveformHeight.HasValue;
                return await this.internalClient.AudiosAsync(includeWaveforms, waveformWidth, waveformHeight);
            }
            catch (ApiException ex) when (ex.StatusCode == 204)
            {
                return [];
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                return [];
            }
        }

        public async Task<AudioObjData?> GetAudioDataAsync(Guid id, int? sampleRate = null, int? channels = null, int? bitDepth = null)
        {
            try
            {
                string guidString = id.ToString();
                return await this.internalClient.DataAsync(guidString, sampleRate, channels, bitDepth);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                return null;
            }
        }

        public async Task<AudioObjInfo?> UploadAudioAsync(FileParameter fParam)
        {
            try
            {
                var info = await this.internalClient.UploadAsync(fParam);
                return info;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                return null;
            }
        }

        public async Task<bool> DeleteAudioAsync(Guid id)
        {
            try
            {
                string guidString = id.ToString();
                await this.internalClient.DeleteAsync(guidString);
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                return false;
            }
        }

        public async Task<AudioObjInfo?> StartRecordingAsync(int? sampleRate = 16000, int? channels = 1, int? bitDepth = 32)
        {
            try
            {
                var info = await this.internalClient.RecordAsync(sampleRate, channels, bitDepth);
                return info;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                return null;
            }
        }

        public async Task<bool> StopRecordingAsync()
        {
            try
            {
                await this.internalClient.StopAsync();
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                return false;
            }
        }

        public async Task<bool?> GetRecordingStatusAsync()
        {
            try
            {
                var status = await this.internalClient.RecordingAsync();
                return status;
            }
            catch (ApiException ex) when (ex.StatusCode == 204)
            {
                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                return null;
            }
        }

        public async Task<string?> ExportAudioAsync(Guid id, string? exportDir = null, string? fileName = null, int bits = 32)
        {
            try
            {
                string guidString = id.ToString();
                string? filePath = await this.internalClient.ExportAsync(guidString, exportDir, fileName, bits);
                return filePath;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                return null;
            }
        }

        public async Task DownloadAudioAsync(Guid id, int bits = 32)
        {
            try
            {
                string guidString = id.ToString();
                await this.internalClient.DownloadAsync(guidString, bits);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
        }


        // ImageController
        public async Task<List<ImageObjInfo>?> ListImagesAsync()
        {
            try
            {
                var resp = await this.internalClient.ListAsync();
                return resp?.ToList();
            }
            catch (ApiException ex) when (ex.StatusCode == 204)
            {
                return [];
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
                var info = await this.internalClient.UploadFileAsync(fParam);
                return info;
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
                await this.internalClient.Delete2Async(id);
                return true;
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
                return await this.internalClient.Data2Async(id);
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
            catch (ApiException ex) when (ex.StatusCode == 204)
            {
                return null;
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
            catch (ApiException ex) when (ex.StatusCode == 204)
            {
                Console.WriteLine(" INFO: No models files found...");
                return [];
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
            catch (ApiException ex) when (ex.StatusCode == 204)
            {
                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                return null;
            }
        }

        public async Task<ICollection<LlamaContextData>?> GetAllContextsAsync(bool orderByLatest = true)
        {
            try
            {
                var response = await this.internalClient.List3Async(orderByLatest);
                return response;
            }
            catch (ApiException ex) when (ex.StatusCode == 204)
            {
                return [];
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
            if (string.IsNullOrWhiteSpace(contextNameOrPath) || contextNameOrPath.StartsWith("/") || contextNameOrPath.StartsWith("Temporary"))
            {
                return null;
            }

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

        public async Task<string?> RenameContextAsync(string? newNameOrPath = null, string? newName = null)
        {
            try
            {
                var ok = await this.internalClient.RenameAsync(newNameOrPath, newName);
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
                var ok = await this.internalClient.Delete3Async(contextNameOrPath);
                return ok ?? false;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                return false;
            }
        }

        public async Task<string?> GetSystemPromptAsync()
        {
            try
            {
                var response = await this.internalClient.GetAsync();
                return response;
            }
            catch (ApiException ex) when (ex.StatusCode == 204)
            {
                return "";
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                return null;
            }
        }

        public async Task<string?> SetSystemPromptAsync(string newPrompt, bool updateCurrentContext = true)
        {
            try
            {
                var response = await this.internalClient.SetAsync(updateCurrentContext, newPrompt);
                return response;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                return null;
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

        public async Task<ICollection<string>?> GenerateSimpleAsync(string prompt, int maxTokens = 1024, float temperature = 0.7f, float topP = 0.95f, bool useSystemPrompt = true, CancellationToken ct = default)
        {
            try
            {
                var response = await this.internalClient.SimpleAllAsync(prompt, maxTokens, temperature, topP, useSystemPrompt, ct);
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

        public async Task<string?> GenerateTextSimpleAsync(string prompt, int maxTokens = 1024, float temperature = 0.7f, float topP = 0.95f, bool useSystemPrompt = true, CancellationToken ct = default)
        {
            try
            {
                var response = await this.internalClient.SimplePOST2Async(prompt, maxTokens, temperature, topP, useSystemPrompt, ct);
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

        public async IAsyncEnumerable<string> GenerateStreamSimpleAsync(string prompt, int maxTokens = 1024, float temperature = 0.7f, float topP = 0.95f, bool useSystemPrompt = true, [EnumeratorCancellation] CancellationToken ct = default)
        {
            var url = $"generate/simple?prompt={Uri.EscapeDataString(prompt)}&maxTokens={maxTokens}&temperature={temperature.ToString(CultureInfo.InvariantCulture)}&topP={topP.ToString(CultureInfo.InvariantCulture)}&useSystemPrompt={useSystemPrompt}";

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
            catch (ApiException ex) when (ex.StatusCode == 204)
            {
                return "";
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                return null;
            }
        }



        // Online (google translate)
        public async Task<string?> GoogleTranslateAsync(string text, string? originalLanguage = null, string translateLanguage = "en")
        {
            try
            {
                var response = await this.internalClient.GoogleTranslateAsync(text, originalLanguage, translateLanguage);
                return response;
            }
            catch (ApiException ex) when (ex.StatusCode == 204)
            {
                return "";
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                return null;
            }
        }



        // ONNX (Whisper)
        public async Task<WhisperModelInfo?> GetCurrentOnnxWhisperModel()
        {
            try
            {
                var response = await this.internalClient.OnnxStatusAsync();
                return response;
            }
            catch (ApiException ex) when (ex.StatusCode == 204)
            {
                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                return null;
            }
        }

        public async Task<ICollection<WhisperModelInfo>?> GetOnnxWhisperModelsAsync()
        {
            try
            {
                var response = await this.internalClient.OnnxModelsAsync();
                return response;
            }
            catch (ApiException ex) when (ex.StatusCode == 204)
            {
                return [];
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                return null;
            }
        }

        public async Task<bool?> LoadOnnxWhisperModelAsync(WhisperModelInfo? whisperModelInfo = null, int cudaDevice = -1)
        {
            try
            {
                var result = await this.internalClient.OnnxLoadAsync(cudaDevice, whisperModelInfo);
                return result;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                return null;
            }
        }

        public async Task<bool?> DisposeOnnxAsync()
        {
            try
            {
                var result = await this.internalClient.WhisperDisposeAsync();
                return result;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                return null;
            }
        }

        public async Task<double?> GetOnnxWhisperTaskProgressAsync(CancellationToken ct = default)
        {
            try
            {
                using var response = await this.httpClient.GetAsync("whisper-progress", ct).ConfigureAwait(false);
                if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
                {
                    return null;
                }

                response.EnsureSuccessStatusCode();
                return await response.Content.ReadFromJsonAsync<double?>(cancellationToken: ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                return null;
            }
        }

        public async Task<string?> RunOnnxWhisperAsync(string audioId, string? language = null, bool transcribe = false, bool useTimestamps = false, CancellationToken ct = default)
        {
            try
            {
                var result = await this.internalClient.OnnxRunAsync(
                    audioId,
                    language,
                    transcribe,
                    useTimestamps,
                    null,  // isCancellationRequested
                    null,  // canBeCanceled
                    IntPtr.Zero,  // waitHandle_Handle
                    null,  // waitHandle_SafeWaitHandle_IsInvalid
                    null,  // waitHandle_SafeWaitHandle_IsClosed
                    ct);
                return result;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                return null;
            }
            finally
            {
                this.CtsWhisper = null;
            }
        }

        public async IAsyncEnumerable<string> RunOnnxWhisperStreamAsync(string audioId, string? language = null, bool transcribe = false, bool useTimestamps = false, [EnumeratorCancellation] CancellationToken ct = default)
        {
            var url = new StringBuilder("whisper-run-stream?audioId=")
                .Append(Uri.EscapeDataString(audioId ?? string.Empty))
                .Append("&language=")
                .Append(Uri.EscapeDataString(language ?? string.Empty))
                .Append("&transcribe=")
                .Append(transcribe.ToString())
                .Append("&useTimestamps=")
                .Append(useTimestamps.ToString())
                .ToString();

            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(string.Empty, Encoding.UTF8, "text/plain")
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

        public async Task<bool?> CancelOnnxWhisperAsync()
        {
            try
            {
                if (this.CtsWhisper != null)
                {
                    this.CtsWhisper.Cancel();
                    return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                return null;
            }
        }




        // Whisper.net
        public async Task<string?> GetWhisperNetStatusAsync()
        {
            try
            {
                var response = await this.internalClient.WhisperStatusAsync();
                return response;
            }
            catch (ApiException ex) when (ex.StatusCode == 204)
            {
                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                return null;
            }
        }

        public async Task<string[]> GetWhisperNetModelsAsync()
        {
            try
            {
                var response = await this.internalClient.WhisperModelsAsync();
                return response.ToArray() ?? [];
            }
            catch (ApiException ex) when (ex.StatusCode == 204)
            {
                return [];
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                return [];
            }
        }

        public async Task<bool?> LoadWhisperNetModelAsync(string? modelName = null, bool useCuda = false)
        {
            try
            {
                var result = await this.internalClient.WhisperLoadAsync(useCuda, modelName);
                return result;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                return null;
            }
        }

        public async Task<string?> RunWhisperNetAsync(string audioId, string? language = null, bool useCuda = false, CancellationToken ct = default)
        {
            try
            {
                var result = await this.internalClient.WhisperRunAsync(
                    audioId, language, useCuda,
                    null,  // isCancellationRequested
                    null,  // canBeCanceled
                    IntPtr.Zero,  // waitHandle_Handle
                    null,  // waitHandle_SafeWaitHandle_IsInvalid
                    null,  // waitHandle_SafeWaitHandle_IsClosed
                    ct);
                return result;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                return null;
            }
        }

        public async IAsyncEnumerable<string> RunWhisperNetStreamAsync(string audioId, string? language = null, bool useCuda = false, [EnumeratorCancellation] CancellationToken ct = default)
        {
            var url = new StringBuilder("whisper-run-stream?audioId=")
                .Append(Uri.EscapeDataString(audioId ?? string.Empty))
                .Append("&language=")
                .Append(Uri.EscapeDataString(language ?? string.Empty))
                .Append("&useCuda=")
                .Append(useCuda.ToString())
                .ToString();
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(string.Empty, Encoding.UTF8, "text/plain")
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




    }
}

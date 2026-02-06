using Microsoft.AspNetCore.Mvc;
using System.Text;
using SharpAI.Runtime;
using SharpAI.Shared;
using SharpAI.Core;

namespace SharpAI.Api.Controllers
{
    [ApiController]
    public class LlamaController : ControllerBase
    {
        private readonly LlamaService Llama;
        private readonly ImageCollection Images;
        private readonly AudioHandling Audio;

        public LlamaController(LlamaService llamaService, ImageCollection imageCollection, AudioHandling audioHandling)
        {
            this.Llama = llamaService;
            this.Images = imageCollection;
            this.Audio = audioHandling;
        }




        // Status
        [HttpGet("status")]
        public ActionResult<LlamaModelFile?> GetLlamaStatus()
        {
            try
            {
                var status = this.Llama.LoadedModelFile;
                return this.Ok(status);
            }
            catch (Exception ex)
            {
                return this.StatusCode(500, $"Error retrieving Llama status: {ex.Message}");
            }
        }

        [HttpGet("systemprompt/get")]
        public ActionResult<string?> GetSystemPrompt()
        {
            try
            {
                return this.Ok(this.Llama.SystemPrompt);
            }
            catch (Exception ex)
            {
                return this.StatusCode(500, $"Error retrieving system prompt: {ex.Message}");
            }
        }

        [HttpPost("systemprompt/set")]
        public async Task<ActionResult<string?>> SetSystemPromptAsync([FromBody] string? systemPrompt, [FromQuery] bool updateCurrentContext = true)
        {
            try
            {
                await this.Llama.SetSystemPromptAsync(systemPrompt, updateCurrentContext);
                return this.Ok(this.Llama.SystemPrompt);
            }
            catch (Exception ex)
            {
                return this.StatusCode(500, $"Error setting system prompt: {ex.Message}");
            }
        }

        [HttpGet("model/list")]
        public ActionResult<List<LlamaModelFile>?> GetModelsList()
        {
            try
            {
                var models = this.Llama.ModelFiles;
                return this.Ok(models);
            }
            catch (Exception ex)
            {
                return this.StatusCode(500, $"Error retrieving models: {ex.Message}");

            }
        }


        // Load & unload
        [HttpPost("model/load")]
        public async Task<ActionResult<LlamaModelFile?>> LoadModelAsync([FromBody] LlamaModelLoadRequest loadRequest, [FromQuery] bool fuzzyMatch = false, [FromQuery] bool tryMultimodal = true, [FromQuery] bool forceUnload = true, CancellationToken ct = default)
        {
            if (this.Llama.IsModelLoaded)
            {
                if (forceUnload)
                {
                    await this.Llama.UnloadModelAsync();
                }
                else
                {
                    return this.StatusCode(400, "A model is already loaded. Unload the current model before loading a new one.");
                }
            }

            try
            {
                var loadedModel = await this.Llama.LoadModelAsync(loadRequest, fuzzyMatch, tryMultimodal, null, ct);
                if (loadedModel == null)
                {
                    return this.StatusCode(500, "Failed to load model.");
                }
                return this.Ok(loadedModel);
            }
            catch (Exception ex)
            {
                return this.StatusCode(500, $"Error loading model: {ex.Message}");
            }
        }

        [HttpPost("model/load/simple")]
        public async Task<ActionResult<LlamaModelFile?>> LoadModelSimpleAsync([FromQuery] string modelNameOrPath, [FromQuery] bool fuzzyMatch = true, [FromQuery] bool tryMultimodal = true, [FromQuery] bool forceUnload = true, [FromQuery] int maxTokens = 1024, [FromQuery] LlamaBackend backend = LlamaBackend.CUDA, CancellationToken ct = default)
        {
            if (this.Llama.IsModelLoaded)
            {
                if (forceUnload)
                {
                    await this.Llama.UnloadModelAsync();
                }
                else
                {
                    return this.StatusCode(400, "A model is already loaded. Unload the current model before loading a new one.");
                }
            }

            try
            {
                var modelFile = this.Llama.GetModelByNameOrPath(modelNameOrPath, fuzzyMatch);
                if (modelFile == null)
                {
                    return this.StatusCode(404, "Model not found.");
                }

                var loadRequest = new LlamaModelLoadRequest(modelFile, maxTokens, backend);
                var loadedModel = await this.Llama.LoadModelAsync(loadRequest, fuzzyMatch, tryMultimodal, null, ct);
                if (loadedModel == null)
                {
                    return this.StatusCode(500, "Failed to load model.");
                }

                return this.Ok(loadedModel);
            }
            catch (Exception ex)
            {
                return this.StatusCode(500, $"Error loading model: {ex.Message}");
            }
        }

        [HttpDelete("model/unload")]
        public async Task<ActionResult<bool?>> UnloadModelAsync()
        {
            try
            {
                var ok = await this.Llama.UnloadModelAsync();
                return this.Ok(ok);
            }
            catch (Exception ex)
            {
                return this.StatusCode(500, $"Error unloading model: {ex.Message}");
            }
        }


        // Contexts
        [HttpGet("context/list")]
        public async Task<ActionResult<List<LlamaContextData>>?> GetContextsList([FromQuery] bool sortedByLatest = true)
        {
            try
            {
                var contexts = await this.Llama.GetAllContextsAsync();
                if (sortedByLatest)
                {
                    contexts = contexts?.OrderByDescending(c => c.LatestActivityDate).ToList();
                }

                return this.Ok(contexts);
            }
            catch (Exception ex)
            {
                return this.StatusCode(500, $"Error retrieving contexts: {ex.Message}");
            }
        }

        [HttpGet("context/current")]
        public ActionResult<LlamaContextData?> GetCurrentContext()
        {
            try
            {
                var context = this.Llama.CurrentContext;
                return this.Ok(context);
            }
            catch (Exception ex)
            {
                return this.StatusCode(500, $"Error retrieving current context: {ex.Message}");
            }
        }

        [HttpPost("context/create")]
        public async Task<ActionResult<string?>> CreateNewContextAsync([FromQuery] bool use = true, [FromQuery] bool saveJson = false)
        {
            try
            {
                var contextPath = await this.Llama.CreateNewContextAsync(use, saveJson);
                return this.Ok(contextPath);
            }
            catch (Exception ex)
            {
                return this.StatusCode(500, $"Error creating new context: {ex.Message}");
            }
        }

        [HttpPost("context/save")]
        public async Task<ActionResult<string?>> SaveContextAsync([FromQuery] string? differentNameOrPath = null)
        {
            try
            {
                var contextPath = await this.Llama.SaveContextAsync(differentNameOrPath);
                return this.Ok(contextPath);
            }
            catch (Exception ex)
            {
                return this.StatusCode(500, $"Error saving context: {ex.Message}");
            }
        }

        [HttpPost("context/load")]
        public async Task<ActionResult<LlamaContextData?>> LoadContextAsync([FromQuery] string? contextNameOrPath = null)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(contextNameOrPath))
                {
                    return this.Llama.CurrentContext;
                }

                var contextData = await this.Llama.LoadContextAsync(contextNameOrPath);
                if (contextData == null)
                {
                    return this.StatusCode(404, "Context not found or failed to load.");
                }
                return this.Ok(contextData);
            }
            catch (Exception ex)
            {
                return this.StatusCode(500, $"Error loading context: {ex.Message}");
            }
        }

        [HttpPost("context/rename")]
        public async Task<ActionResult<string?>> RenameContextAsync([FromQuery] string? contextNameOrPath = null, [FromQuery] string? newName = null)
        {
            try
            {
                var newPath = await this.Llama.RenameContextAsync(contextNameOrPath, newName);
                return this.Ok(newPath);
            }
            catch (Exception ex)
            {
                return this.StatusCode(500, $"Error renaming context: {ex.Message}");
            }
        }

        [HttpDelete("context/delete")]
        public async Task<ActionResult<bool?>> DeleteContextAsync([FromQuery] string? contextNameOrPath = null)
        {
            try
            {
                var ok = this.Llama.DeleteContextAsync(contextNameOrPath);
                return this.Ok(ok);
            }
            catch (Exception ex)
            {
                return this.StatusCode(500, $"Error deleting context: {ex.Message}");
            }
        }



        // Generation
        [HttpPost("generate")]
        public async Task<ActionResult<IAsyncEnumerable<string>?>> GenerateAsync([FromBody] LlamaGenerationRequest generationRequest, CancellationToken ct = default)
        {
            try
            {
                var stream = this.Llama.GenerateAsync(generationRequest, ct);
                if (stream == null)
                {
                    return this.StatusCode(500, "Failed to start generation. Is a model loaded?");
                }

                return this.Ok(stream);
            }
            catch (Exception ex)
            {
                return this.StatusCode(500, $"Error generating text: {ex.Message}");
            }
        }

        [HttpPost("generate/simple")]
        public async Task<ActionResult<IAsyncEnumerable<string>?>> GenerateSimpleAsync([FromQuery] string prompt, [FromQuery] int maxTokens = 1024, [FromQuery] float temperature = 0.7f, [FromQuery] float topP = 0.95f, [FromQuery] bool useSystemPrompt = true, CancellationToken ct = default)
        {
            try
            {
                var request = new LlamaGenerationRequest
                {
                    Prompt = prompt,
                    MaxTokens = maxTokens,
                    Temperature = temperature,
                    TopP = topP,
                    Stream = true,
                    UseSystemPrompt = useSystemPrompt,
                };

                var stream = this.Llama.GenerateAsync(request, ct);
                if (stream == null)
                {
                    return this.StatusCode(500, "Failed to start generation. Is a model loaded?");
                }

                return this.Ok(stream);
            }
            catch (Exception ex)
            {
                return this.StatusCode(500, $"Error generating text: {ex.Message}");
            }
        }

        [HttpPost("generate/text")]
        public async Task<ActionResult<string?>> GenerateTextAsync([FromBody] LlamaGenerationRequest generationRequest, CancellationToken ct = default)
        {
            try
            {
                var text = await this.Llama.GenerateTextAsync(generationRequest, ct);
                if (text == null)
                {
                    return this.StatusCode(500, "Failed to start generation. Is a model loaded?");
                }

                return this.Ok(text);
            }
            catch (Exception ex)
            {
                return this.StatusCode(500, $"Error generating text: {ex.Message}");
            }
        }

        [HttpPost("generate/text/simple")]
        public async Task<ActionResult<string?>> GenerateTextSimpleAsync([FromQuery] string prompt, [FromQuery] int maxTokens = 1024, [FromQuery] float temperature = 0.7f, [FromQuery] float topP = 0.95f, [FromQuery] bool useSystemPrompt = true, CancellationToken ct = default)
        {
            try
            {
                var request = new LlamaGenerationRequest
                {
                    Prompt = prompt,
                    MaxTokens = maxTokens,
                    Temperature = temperature,
                    TopP = topP,
                    Stream = false,
                    UseSystemPrompt = useSystemPrompt,
                };

                var text = await this.Llama.GenerateTextAsync(request, ct);
                if (text == null)
                {
                    return this.StatusCode(500, "Failed to start generation. Is a model loaded?");
                }

                return this.Ok(text);
            }
            catch (Exception ex)
            {
                return this.StatusCode(500, $"Error generating text: {ex.Message}");
            }
        }


        // Streaming endpoints
        [HttpPost("generate/stream")]
        [Produces("text/event-stream")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> GenerateStreamAsync([FromBody] LlamaGenerationRequest generationRequest, CancellationToken ct = default)
        {
            var stream = this.Llama.GenerateAsync(generationRequest, ct);
            if (stream == null)
            {
                this.Response.StatusCode = StatusCodes.Status500InternalServerError;
                await this.Response.WriteAsync("data: Failed to start generation. Is a model loaded?\n\n", ct);
                return new EmptyResult();
            }

            this.Response.Headers.ContentType = "text/event-stream";
            this.Response.Headers.CacheControl = "no-cache";

            var responseBuilder = new StringBuilder();
            try
            {
                await foreach (var chunk in stream.WithCancellation(ct).ConfigureAwait(false))
                {
                    if (string.IsNullOrEmpty(chunk))
                    {
                        continue;
                    }

                    responseBuilder.Append(chunk);
                    await this.Response.WriteAsync($"data: {chunk}\n\n", ct);
                    await this.Response.Body.FlushAsync(ct);
                }
            }
            catch (OperationCanceledException)
            {
                if (!this.Response.HasStarted)
                {
                    return this.Ok(responseBuilder.ToString());
                }

                if (responseBuilder.Length > 0)
                {
                    await this.Response.WriteAsync($"data: {responseBuilder}\n\n", CancellationToken.None);
                    await this.Response.Body.FlushAsync(CancellationToken.None);
                }
            }

            return new EmptyResult();
        }

        [HttpGet("generate/stream/simple")]
        [Produces("text/event-stream")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> GenerateStreamSimpleAsync([FromQuery] string prompt, [FromQuery] int maxTokens = 1024, [FromQuery] float temperature = 0.7f, [FromQuery] float topP = 0.95f, [FromQuery] bool useSystemPrompt = true, CancellationToken ct = default)
        {
            var request = new LlamaGenerationRequest
            {
                Prompt = prompt,
                MaxTokens = maxTokens,
                Temperature = temperature,
                TopP = topP,
                Stream = true,  
                UseSystemPrompt = useSystemPrompt,
            };

            var stream = this.Llama.GenerateAsync(request, ct);
            if (stream == null)
            {
                this.Response.StatusCode = StatusCodes.Status500InternalServerError;
                await this.Response.WriteAsync("data: Failed to start generation. Is a model loaded?\n\n", ct);
                return new EmptyResult();
            }

            this.Response.Headers.ContentType = "text/event-stream";
            this.Response.Headers.CacheControl = "no-cache";

            var responseBuilder = new StringBuilder();
            try
            {
                await foreach (var chunk in stream.WithCancellation(ct).ConfigureAwait(false))
                {
                    if (string.IsNullOrEmpty(chunk))
                    {
                        continue;
                    }

                    responseBuilder.Append(chunk);
                    await this.Response.WriteAsync($"data: {chunk}\n\n", ct);
                    await this.Response.Body.FlushAsync(ct);
                }
            }
            catch (OperationCanceledException)
            {
                if (!this.Response.HasStarted)
                {
                    return this.Ok(responseBuilder.ToString());
                }

                if (responseBuilder.Length > 0)
                {
                    await this.Response.WriteAsync($"data: {responseBuilder}\n\n", CancellationToken.None);
                    await this.Response.Body.FlushAsync(CancellationToken.None);
                }
            }

            return new EmptyResult();
        }



        // Backends
        [HttpGet("backends")]
        public ActionResult<List<string>> GetAvailableBackends()
        {
            try
            {
                var backends = this.Llama.GetAvailableBackends();
                return this.Ok(backends);
            }
            catch (Exception ex)
            {
                return this.StatusCode(500, $"Error retrieving backends: {ex.Message}");
            }
        }

        [HttpGet("backends/cuda")]
        public async Task<ActionResult<Version>?> GetCudaVersionAsync()
        {
            try
            {
                var version = await this.Llama.GetCudaBackendVersionAsync();
                return this.Ok(version);
            }
            catch (Exception ex)
            {
                return this.StatusCode(500, $"Error retrieving CUDA version: {ex.Message}");
            }
        }




    }
}

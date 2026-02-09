using Microsoft.AspNetCore.Mvc;
using SharpAI.Core;
using SharpAI.Runtime;
using System.IO;

namespace SharpAI.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class WhisperController : ControllerBase
    {
        private readonly WhisperService Whisper;
        private readonly AudioHandling Audio;



        public WhisperController(WhisperService Whisper, AudioHandling Audio)
        {
            this.Whisper = Whisper;
            this.Audio = Audio;
        }



        [HttpGet("whisper-status")]
        public async Task<ActionResult<string>?> GetStatusAsync()
        {
            try
            {
                if (string.IsNullOrEmpty(this.Whisper.CurrentModelFile))
                {
                    // Align with ApiClient: return 204 No Content when no model is loaded
                    return this.NoContent();
                }

                // Return JSON serialized string to ensure the generated client can deserialize it
                return this.Ok(Path.GetFileName(this.Whisper.CurrentModelFile));
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                return this.StatusCode(500, ex.Message);
            }
            finally
            {
                await Task.Yield();
            }
        }

        [HttpGet("whisper-models")]
        public async Task<ActionResult<string[]>?> GetAvailableModelsAsync()
        {
            try
            {
                var models = this.Whisper.ModelFiles.Select(Path.GetFullPath).ToArray();
                return this.Ok(models);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                return this.StatusCode(500, ex.Message);
            }
            finally
            {
                await Task.Yield();
            }
        }

        [HttpPost("whisper-load")]
        public async Task<ActionResult<bool>?> LoadModelAsyncAsync([FromBody] string? modelPath = null, [FromQuery] bool useCuda = false)
        {
            try
            {
                bool result = await this.Whisper.InitializeAsync(modelPath, useCuda);
                if (result)
                {
                    return this.Ok(true);
                }
                else
                {
                    return this.BadRequest("Failed to initialize Whisper with the specified model.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                return this.StatusCode(500, ex.Message);
            }
        }

        [HttpDelete("whisper-dispose")]
        public async Task<ActionResult<bool>?> DisposeWhisperAsync()
        {
            try
            {
                this.Whisper.Dispose();
                return this.Ok(!this.Whisper.IsInitialized);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                return this.StatusCode(500, ex.Message);
            }
            finally
            {
                await Task.Yield();
            }
        }

        [HttpPost("whisper-run")]
        [Produces("application/json")]
        public async Task<ActionResult<string>?> RunWhisperAsync([FromQuery] string audioId, [FromQuery] string? language = null, [FromQuery] bool useCuda = false, [FromQuery] CancellationToken ct = default)
        {
            if (!this.Whisper.IsInitialized)
            {
                Console.WriteLine("Whisper is not initialized.");
                return this.BadRequest("Whisper is not initialized. Load a model first!");
            }
            try
            {
                AudioObj? audioObj = null;
                if (!string.IsNullOrEmpty(audioId) && Guid.TryParse(audioId, out var id))
                {
                    audioObj = this.Audio[id];
                    if (audioObj == null)
                    {
                        this.Response.StatusCode = StatusCodes.Status400BadRequest;
                        await this.Response.WriteAsync($"data: AudioObj with ID {id} not found.\n\n", ct);
                        return this.BadRequest($"AudioObj with ID {id} not found.");
                    }
                }
                else
                {
                    this.Response.StatusCode = StatusCodes.Status400BadRequest;
                    await this.Response.WriteAsync($"data: {audioId} was not parsable as Guid ID.\n\n", ct);
                    return this.BadRequest();
                }

                string? result = await this.Whisper.TranscribeAsync(audioObj, language, useCuda, ct);
                if (result == null)
                {
                    return this.BadRequest("Transcription failed.");
                }

                return new JsonResult(result);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                return this.StatusCode(500, ex.Message);
            }
        }

        [HttpPost("whisper-run-stream")]
        [Produces("text/event-stream")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> RunWhisperStreamAsync([FromQuery] string audioId, [FromQuery] string? language = null, [FromQuery] bool useCuda = false, [FromQuery] CancellationToken ct = default)
        {
            if (!this.Whisper.IsInitialized)
            {
                this.Response.StatusCode = StatusCodes.Status500InternalServerError;
                await this.Response.WriteAsync("data: Onnx is not initialized. Load a Whisper-Model first!\n\n", ct);
                return new EmptyResult();
            }

            AudioObj? audioObj = null;
            if (!string.IsNullOrEmpty(audioId) && Guid.TryParse(audioId, out var id))
            {
                audioObj = this.Audio[id];
                if (audioObj == null)
                {
                    this.Response.StatusCode = StatusCodes.Status400BadRequest;
                    await this.Response.WriteAsync($"data: AudioObj with ID {id} not found.\n\n", ct);
                    return new EmptyResult();
                }
            }
            else
            {
                this.Response.StatusCode = StatusCodes.Status400BadRequest;
                await this.Response.WriteAsync($"data: {audioId} was not parsable as Guid ID.\n\n", ct);
                return new EmptyResult();
            }

            this.Response.Headers.ContentType = "text/event-stream";
            this.Response.Headers.CacheControl = "no-cache";

            try
            {
                await foreach (var chunk in this.Whisper.TranscribeStreamAsync(audioObj, language, useCuda, ct).WithCancellation(ct))
                {
                    if (string.IsNullOrEmpty(chunk))
                    {
                        continue;
                    }

                    await this.Response.WriteAsync($"data: {chunk}\n\n", ct);
                    await this.Response.Body.FlushAsync(ct);
                }
            }
            catch (OperationCanceledException)
            {
                if (!this.Response.HasStarted)
                {
                    return this.Ok();
                }
            }

            return new EmptyResult();
        }
    }
}

using System.Reflection.Metadata.Ecma335;
using Microsoft.AspNetCore.Mvc;
using SharpAI.Core;
using SharpAI.Runtime;
using SharpAI.Shared;

namespace SharpAI.Api.Controllers
{
    public class OnnxController : ControllerBase
    {
        private readonly OnnxService Onnx;
        private readonly AudioHandling Audio;

        public OnnxController(OnnxService Onnx, AudioHandling Audio)
        {
            this.Onnx = Onnx;
            this.Audio = Audio;
        }


        [HttpGet("onnx-status")]
        public ActionResult<WhisperModelInfo?>? GetOnnxStatus()
        {
            try
            {
                if (this.Onnx.CurrentModel != null)
                {
                    return this.Onnx.CurrentModel;
                }
                else
                {
                    return null;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                return this.StatusCode(500, ex.Message);
            }
        }

        [HttpGet("whisper-models")]
        public ActionResult<List<WhisperModelInfo>?> GetWhisperOnnxModels()
        {
            try
            {
                return this.Onnx.AvailableModels;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                return this.StatusCode(500, ex.Message);
            }
        }

        [HttpPost("whisper-load")]
        public async Task<ActionResult<bool>?> LoadWhisperModelAsync([FromBody] WhisperModelInfo? whisperModelInfo = null, [FromQuery] int useCudaDeviceId = -1)
        {
            try
            {
                var result = await this.Onnx.InitializeAsync(whisperModelInfo, useCudaDeviceId);
                return result;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                return this.StatusCode(500, ex.Message);

            }
        }

        [HttpDelete("whisper-dispose")]
        public ActionResult<bool>? DisposeWhisper()
        {
            try
            {
                return this.Onnx.DeInitialize();

            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                return this.StatusCode(500, ex.Message);
            }
        }

        [HttpPost("whisper-run")]
        [Produces("application/json")]
        public async Task<ActionResult<string>?> RunWhisperAsync([FromQuery] string audioId, [FromQuery] string? language = null, [FromQuery] bool transcribe = false, [FromQuery] bool useTimestamps = false, [FromQuery] CancellationToken ct = default)
        {
            if (!this.Onnx.IsInitialized)
            {
                Console.WriteLine("Onnx is not initialized.");
                return this.BadRequest("Onnx is not initialized. Load a Whisper-Model first!");
            }

            try
            {
                AudioObj? audioObj = null;
                if (!string.IsNullOrEmpty(audioId) && Guid.TryParse(audioId, out var id))
                {
                    audioObj = this.Audio[id];
                    if (audioObj == null)
                    {
                        return this.BadRequest($"AudioObj with ID {id.ToString()} not found.");
                    }
                }
                else
                {
                    return this.BadRequest($"{audioId} was not parsable as Guid ID.");
                }


                var result = await this.Onnx.TranscribeAsync(audioObj, language, !transcribe, useTimestamps, null, ct);
                if (string.IsNullOrWhiteSpace(result))
                {
                    return this.BadRequest("Onnx Task returned empty or null String.");
                }

                return this.Ok(result);
            }
            catch (Exception ex)
            {
                return this.StatusCode(500, ex.Message);
            }
        }


        [HttpGet("whisper-progress")]
        public ActionResult<double?> GetWhisperProgress()
        {
            var progress = this.Onnx.CurrentWhisperProgress;
            if (!progress.HasValue)
            {
                return this.NoContent();
            }

            return this.Ok(progress.Value);
        }

        [HttpPost("whisper-run-stream")]
        [Produces("text/event-stream")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> RunWhisperStreamAsync([FromQuery] string audioId, [FromQuery] string? language = null, [FromQuery] bool transcribe = false, [FromQuery] bool useTimestamps = false, CancellationToken ct = default)
        {
            if (!this.Onnx.IsInitialized)
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
                await foreach (var chunk in this.Onnx.TranscribeStreamAsync(audioObj, language, !transcribe, useTimestamps, null, ct).WithCancellation(ct))
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

using Microsoft.AspNetCore.Mvc;
using SharpAI.Core;
using SharpAI.Shared;

namespace SharpAI.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class AudioController : ControllerBase
    {
        private readonly AudioHandling Audio;

        public AudioController(AudioHandling audio)
        {
            Audio = audio;
        }

        [HttpGet("audios")]
        public async Task<ActionResult<List<AudioObjInfo>>?> GetAudiosAsync([FromQuery] bool includeWaveforms = false, [FromQuery] int width = 512, [FromQuery] int height = 64)
        {
            try
            {
                List<AudioObjInfo> infos = this.Audio.Audios.Select(a => new AudioObjInfo(a.Id, a.CreatedAt, a.FilePath, a.Name, a.Duration)).ToList();
                
                if (includeWaveforms)
                {
                    foreach (var info in infos)
                    {
                        if (this.Audio.WaveformCache.TryGetValue(info.Id, out var cached) && cached.Width == width && cached.Height == height && IsPngBase64(cached.Base64DataCache))
                        {
                            info.WaveformPreview = new ImageObjData(info.Id, cached.Base64DataCache ?? "");
                            continue;
                        }

                        var audioObj = this.Audio[info.Id];
                        if (audioObj != null)
                        {
                            var imageObj = await ImageObj.DrawWaveformAsync(audioObj, width, height);
                            if (imageObj != null)
                            {
                                var base64Data = await imageObj.GetPngBase64Async(true);
                                if (!string.IsNullOrEmpty(base64Data))
                                {
                                    imageObj.Base64DataCache = base64Data;
                                    this.Audio.WaveformCache.AddOrUpdate(info.Id, imageObj, (_, __) => imageObj);
                                    info.WaveformPreview = new ImageObjData(info.Id, base64Data);
                                }
                            }
                        }
                    }
                }

                return this.Ok(infos);
            }
            catch (Exception ex)
            {
                StaticLogger.Log($"Error retrieving audio list: {ex.Message}");
                return this.StatusCode(500, $"Error retrieving audio list: {ex.Message}");
            }
        }

        [HttpGet("audios/data")]
        public async Task<ActionResult<AudioObjData>?> GetAudioDataAsync([FromQuery] string guid, [FromQuery] int? sampleRate = null, [FromQuery] int? channels = null, [FromQuery] int? bitDepth = null)
        {
            if (string.IsNullOrEmpty(guid) || !Guid.TryParse(guid, out Guid audioId))
            {
                return this.BadRequest("Invalid or missing 'guid' query parameter.");
            }
            try
            {
                AudioObj? obj = this.Audio[audioId];
                if (obj == null)
                {
                    return this.NotFound("AudioObj not found with Guid: " + guid);
                }

                string? base64Data = await obj.SerializeAsBase64Async(sampleRate, channels, bitDepth);
                if (string.IsNullOrEmpty(base64Data))
                {
                    return this.StatusCode(500, "Failed to serialize audio data to Base64.");
                }

                AudioObjData audioData = new(obj.Id, obj.SampleRate, obj.Channels, obj.BitDepth, obj.Duration, base64Data);
                
                return this.Ok(audioData);
            }
            catch (Exception ex)
            {
                StaticLogger.Log($"Error retrieving audio data for ID {audioId}: {ex.Message}");
                return this.StatusCode(500, $"Error retrieving audio data: {ex.Message}");
            }
        }

        [HttpGet("audios/waveform")]
        public async Task<ActionResult<ImageObjData>?> GetAudioWaveformAsync([FromQuery] string guid, [FromQuery] int width = 512, [FromQuery] int height = 64, [FromQuery] int? samplesPerPixel = null, [FromQuery] int offset = 0, [FromQuery] string backColorHex = "#FFFFFF", [FromQuery] string graphColorHex = "#000000", [FromQuery] bool useCache = true, [FromQuery] int? maxWorkers = null)
        {
            try
            {
                if (string.IsNullOrEmpty(guid) || !Guid.TryParse(guid, out Guid audioId))
                {
                    return this.BadRequest("Invalid or missing 'guid' query parameter.");
                }

                var audioObj = this.Audio[audioId];
                if (audioObj == null)
                {
                    return this.NotFound("AudioObj not found with Guid: " + guid);
                }

                string? base64Data;
                if (useCache && this.Audio.WaveformCache.TryGetValue(audioId, out var cached) && cached.Width == width && cached.Height == height && IsPngBase64(cached.Base64DataCache))
                {
                    base64Data = cached.Base64DataCache;
                    if (string.IsNullOrEmpty(base64Data))
                    {
                        return this.StatusCode(500, "Failed to retrieve cached waveform image data.");
                    }
                }
                else
                {
                    var imageObj = await ImageObj.DrawWaveformAsync(audioObj, width, height, samplesPerPixel, offset, backColorHex, graphColorHex, maxWorkers);
                    if (imageObj == null)
                    {
                        return this.StatusCode(500, "Failed to generate audio waveform image.");
                    }

                    base64Data = await imageObj.GetPngBase64Async(true);
                    if (string.IsNullOrEmpty(base64Data))
                    {
                        return this.StatusCode(500, "Failed to serialize waveform image data to Base64.");
                    }

                    imageObj.Base64DataCache = base64Data;
                    if (useCache)
                    {
                        this.Audio.WaveformCache.AddOrUpdate(audioId, imageObj, (_, __) => imageObj);
                    }
                }

                ImageObjData imageData = new(audioId, base64Data);
                return this.Ok(imageData);
            }
            catch (Exception ex)
            {
                StaticLogger.Log($"Error generating audio waveform: {ex.Message}");
                return this.StatusCode(500, $"Error generating audio waveform: {ex.Message}");
            }
        }

        private static bool IsPngBase64(string? base64)
        {
            if (string.IsNullOrWhiteSpace(base64))
            {
                return false;
            }

            // PNG signature in Base64 starts with iVBORw0KGgo
            return base64.StartsWith("iVBORw0KGgo", StringComparison.Ordinal);
        }

        [HttpPost("audios/upload")]
        [Consumes("multipart/form-data")]
        public async Task<ActionResult<AudioObjInfo>?> UploadAudioAsync(IFormFile file)
        {
            if (file == null || file.Length == 0)
            {
                return this.BadRequest("No file uploaded.");
            }
            try
            {
                string tempFilePath = Path.GetTempFileName();
                using (var stream = new FileStream(tempFilePath, FileMode.Create))
                {
                    await file.CopyToAsync(stream);
                }
                AudioObj? audioObj = await this.Audio.ImportAudioAsync(tempFilePath);
                System.IO.File.Delete(tempFilePath);
                if (audioObj == null)
                {
                    return this.StatusCode(500, "Failed to import audio file.");
                }
                audioObj.Name = Path.GetFileNameWithoutExtension(file.FileName);
                AudioObjInfo info = new(audioObj.Id, audioObj.CreatedAt, audioObj.FilePath, audioObj.Name, audioObj.Duration);
                return this.Ok(info);
            }
            catch (Exception ex)
            {
                StaticLogger.Log($"Error uploading audio file: {ex.Message}");
                return this.StatusCode(500, $"Error uploading audio file: {ex.Message}");
            }
        }

        [HttpGet("audios/record/recording")]
        public ActionResult<bool> IsRecording()
        {
            try
            {
                bool isRecording = this.Audio.IsRecording;
                return this.Ok(isRecording);
            }
            catch (Exception ex)
            {
                StaticLogger.Log($"Error checking recording status: {ex.Message}");
                return this.StatusCode(500, $"Error checking recording status: {ex.Message}");
            }
        }

        [HttpPost("audios/record")]
        public async Task<ActionResult<AudioObjInfo>?> RecordAudioAsync([FromQuery] int sampleRate = 16000, [FromQuery] int channels = 1, [FromQuery] int bitDepth = 32)
        {
            try
            {
                AudioObj? audioObj = await this.Audio.RecordAudioAsync(sampleRate: sampleRate, channels: channels, bitDepth: bitDepth);
                if (audioObj == null)
                {
                    return this.StatusCode(500, "Failed to record audio.");
                }

                this.Audio.AddAudio(audioObj);
                
                AudioObjInfo info = new(audioObj.Id, audioObj.CreatedAt, audioObj.FilePath, audioObj.Name, audioObj.Duration);
                
                return this.Ok(info);
            }
            catch (Exception ex)
            {
                StaticLogger.Log($"Error recording audio: {ex.Message}");
                return this.StatusCode(500, $"Error recording audio: {ex.Message}");
            }
        }

        [HttpPost("audios/record/stop")]
        public ActionResult StopRecording()
        {
            try
            {
                bool ok = this.Audio.StopRecording();
                if (ok)
                {
                    return this.Ok("Audio recording stopped successfully.");

                }
                else
                {
                    return this.NotFound("There was no recording in progress to stop.");
                }
            }
            catch (Exception ex)
            {
                StaticLogger.Log($"Error stopping audio recording: {ex.Message}");
                return this.StatusCode(500, $"Error stopping audio recording: {ex.Message}");
            }
        }

        [HttpGet("audios/resample")]
        public async Task<ActionResult<AudioObjData>?> ResampleAudioAsync([FromQuery] string? guid, [FromQuery] int sampleRate = 16000, [FromQuery] int channels = 1, [FromQuery] int bitDepth = 32)
        {
            if (string.IsNullOrEmpty(guid) || !Guid.TryParse(guid, out Guid audioId))
            {
                return this.BadRequest("Invalid or missing 'guid' query parameter.");
            }
            try
            {
                AudioObj? obj = this.Audio[audioId];
                if (obj == null)
                {
                    return this.NotFound("AudioObj not found with Guid: " + guid);
                }

                bool ok = await obj.ResampleAsync(sampleRate, bitDepth);
                if (!ok)
                {
                    return this.StatusCode(500, "Failed to resample audio.");
                }

                obj = this.Audio[guid];
                if (obj == null)
                {
                    return this.NotFound("AudioObj not found after resampling with Guid: " + guid);
                }
                
                string? base64Data = await obj.SerializeAsBase64Async(sampleRate, channels, bitDepth);
                if (string.IsNullOrEmpty(base64Data))
                {
                    return this.StatusCode(500, "Failed to serialize resampled audio data to Base64.");
                }

                AudioObjData data = new(obj.Id, obj.SampleRate, obj.Channels, obj.BitDepth, obj.Duration, base64Data);

                return this.Ok(data);
            }
            catch (Exception ex)
            {
                StaticLogger.Log($"Error resampling audio with ID {audioId}: {ex.Message}");
                return this.StatusCode(500, $"Error resampling audio: {ex.Message}");
            }
        }

        [HttpGet("audios/rechannel")]
        public async Task<ActionResult<AudioObjData>?> RechannelAudioAsync([FromQuery] string? guid, [FromQuery] int sampleRate = 16000, [FromQuery] int channels = 1, [FromQuery] int bitDepth = 32)
        {
            if (string.IsNullOrEmpty(guid) || !Guid.TryParse(guid, out Guid audioId))
            {
                return this.BadRequest("Invalid or missing 'guid' query parameter.");
            }
            try
            {
                AudioObj? obj = this.Audio[audioId];
                if (obj == null)
                {
                    return this.NotFound("AudioObj not found with Guid: " + guid);
                }

                bool ok = await obj.RechannelAsync(channels);
                if (!ok)
                {
                    return this.StatusCode(500, "Failed to rechannel audio.");
                }

                obj = this.Audio[guid];
                if (obj == null)
                {
                    return this.NotFound("AudioObj not found after rechannelling with Guid: " + guid);
                }
                
                string? base64Data = await obj.SerializeAsBase64Async(sampleRate, channels, bitDepth);
                if (string.IsNullOrEmpty(base64Data))
                {
                    return this.StatusCode(500, "Failed to serialize rechanneled audio data to Base64.");
                }

                AudioObjData data = new(obj.Id, obj.SampleRate, obj.Channels, obj.BitDepth, obj.Duration, base64Data);
                return this.Ok(data);
            }
            catch (Exception ex)
            {
                StaticLogger.Log($"Error rechannelling audio with ID {audioId}: {ex.Message}");
                return this.StatusCode(500, $"Error rechannelling audio: {ex.Message}");
            }
        }

        [HttpGet("audios/export")]
        public async Task<ActionResult<string>?> ExportAudioAsync([FromQuery] string? guid, [FromQuery] string? exportDir = null, [FromQuery] string? fileName = null, [FromQuery] int bits = 32)
        {
            if (string.IsNullOrEmpty(guid) || !Guid.TryParse(guid, out Guid audioId))
            {
                return this.BadRequest("Invalid or missing 'guid' query parameter.");
            }
            try
            {
                AudioObj? obj = this.Audio[audioId];
                if (obj == null)
                {
                    return this.NotFound("AudioObj not found with Guid: " + guid);
                }
                string? filePath = await obj.ExportWavAsync(exportDir, fileName, bits);
                if (string.IsNullOrEmpty(filePath))
                {
                    return this.StatusCode(500, "Failed to export audio to WAV file.");
                }
                return this.Ok(filePath);
            }
            catch (Exception ex)
            {
                StaticLogger.Log($"Error exporting audio with ID {audioId}: {ex.Message}");
                return this.StatusCode(500, $"Error exporting audio: {ex.Message}");
            }
        }

        [HttpGet("audios/download")]
        public Task<IActionResult> DownloadAudioAsync([FromQuery] string? guid, [FromQuery] int bits = 32)
        {
            return this.DownloadAudioInternalAsync(guid, bits);
        }

        [HttpGet("download")]
        public Task<IActionResult> DownloadAudioAltAsync([FromQuery] string? guid, [FromQuery] int bits = 32)
        {
            return this.DownloadAudioInternalAsync(guid, bits);
        }

        private async Task<IActionResult> DownloadAudioInternalAsync(string? guid, int bits)
        {
            if (string.IsNullOrEmpty(guid) || !Guid.TryParse(guid, out Guid audioId))
                return BadRequest("Invalid or missing 'guid' query parameter.");

            try
            {
                var obj = this.Audio[audioId];
                if (obj == null) return NotFound("AudioObj not found with Guid: " + guid);

                var tempFilePath = Path.GetTempFileName();
                string? exportedPath = await obj.ExportWavAsync(Path.GetDirectoryName(tempFilePath), Path.GetFileNameWithoutExtension(tempFilePath), bits);
                if (string.IsNullOrEmpty(exportedPath)) return StatusCode(500, "Failed to export audio to temporary WAV file.");

                var wavStream = new FileStream(exportedPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
                string fileName = $"{obj.Name}_{obj.Id}.wav";
                return File(wavStream, "audio/wav", fileName);
            }
            catch (Exception ex)
            {
                StaticLogger.Log($"Error downloading audio with ID {audioId}: {ex.Message}");
                return StatusCode(500, $"Error downloading audio: {ex.Message}");
            }
        }


        [HttpDelete("audios/delete")]
        public ActionResult DeleteAudio([FromQuery] string? guid)
        {
            if (string.IsNullOrEmpty(guid) || !Guid.TryParse(guid, out Guid audioId))
            {
                return this.BadRequest("Invalid or missing 'guid' query parameter.");
            }
            try
            {
                bool removed = this.Audio.RemoveAudio(audioId);
                if (!removed)
                {
                    return this.NotFound("AudioObj not found with Guid: " + guid);
                }
                return this.Ok($"Audio with ID {audioId} deleted successfully.");
            }
            catch (Exception ex)
            {
                StaticLogger.Log($"Error deleting audio with ID {audioId}: {ex.Message}");
                return this.StatusCode(500, $"Error deleting audio: {ex.Message}");
            }
        }

        [HttpDelete("audios/clear")]
        public ActionResult ClearAudios()
        {
            try
            {
                this.Audio.ClearAudios();
                return this.Ok("All audio objects cleared successfully.");
            }
            catch (Exception ex)
            {
                StaticLogger.Log($"Error clearing audio objects: {ex.Message}");
                return this.StatusCode(500, $"Error clearing audio objects: {ex.Message}");
            }
        }



    }
}

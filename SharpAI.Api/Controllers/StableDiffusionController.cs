using Microsoft.AspNetCore.Mvc;
using SharpAI.Core;
using SharpAI.Runtime;
using SharpAI.Shared;
using SharpAI.StableDiffusion;
using System.Diagnostics;
using System.Net.Mime;
using System.Security.Cryptography.X509Certificates;

namespace SharpAI.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class StableDiffusionController : ControllerBase
    {
        private StableDiffusionService SD;
        private ImageCollection Images;

        public StableDiffusionController(StableDiffusionService stableDiffusionService, ImageCollection imageCollection)
        {
            this.SD = stableDiffusionService;
            this.Images = imageCollection;
        }



        [HttpGet("sd-models")]
        public async Task<ActionResult<List<StableDiffusionModel>>?> GetModelsAsync()
        {
            try
            {
                var models = this.SD.Models;
                return this.Ok(models);
            }
            catch (Exception ex)
            {
                await StaticLogger.LogAsync($"Error in GetModelsAsync: {ex.Message}");
                return this.StatusCode(500, "An error occurred while retrieving models.");
            }
            finally
            {
                await Task.Yield();
            }
        }

        [HttpGet("sd-status")]
        public async Task<ActionResult<StableDiffusionModel?>?> GetStatusAsync()
        {
            try
            {
                var currentModel = this.SD.CurrentModel;
                return this.Ok(currentModel);
            }
            catch (Exception ex)
            {
                await StaticLogger.LogAsync($"Error in GetStatusAsync: {ex.Message}");
                return this.StatusCode(500, "An error occurred while retrieving status.");
            }
            finally
            {
                await Task.Yield();
            }
        }

        [HttpPost("sd-load")]
        public async Task<ActionResult> LoadModelAsync([FromQuery] string? modelRootPath = null)
        {
            if (string.IsNullOrEmpty(modelRootPath))
            {
                modelRootPath = this.SD.Models.FirstOrDefault()?.ModelRootPath;
            }

            if (string.IsNullOrEmpty(modelRootPath))
            {
                return this.BadRequest("No model specified and no default model available.");
            }

            try
            {
                var model = this.SD.Models.FirstOrDefault(m => m.ModelRootPath.Equals(modelRootPath, StringComparison.OrdinalIgnoreCase));
                if (model == null)
                {
                    return this.NotFound($"Model '{modelRootPath}' not found.");
                }
                this.SD.InitializeSessions(model);
                if (!this.SD.IsInitialized)
                {
                    return this.StatusCode(500, "Failed to initialize model sessions.");
                }

                return this.Ok($"Model '{modelRootPath}' loaded successfully.");
            }
            catch (Exception ex)
            {
                await StaticLogger.LogAsync($"Error in LoadModelAsync: {ex.Message}");
                return this.StatusCode(500, "An error occurred while loading the model: " + ex.ToString());
            }
        }

        [HttpDelete("sd-unload")]
        public async Task<ActionResult> UnloadModelAsync()
        {
            try
            {
                this.SD.DisposeSessions();
                if (this.SD.IsInitialized)
                {
                    return this.StatusCode(500, "Failed to unload model sessions.");
                }

                return this.Ok("Model sessions unloaded successfully.");
            }
            catch (Exception ex)
            {
                await StaticLogger.LogAsync($"Error in UnloadModelAsync: {ex.Message}");
                return this.StatusCode(500, "An error occurred while unloading the model.");
            }
        }



        [HttpPost("sd-generate")]
        public async Task<ActionResult<Guid>?> GenerateImageAsync([FromBody] StableDiffusionGenerationRequest request, [FromQuery] bool loadDefault = true)
        {
            try
            {
                if (!this.SD.IsInitialized)
                {
                    if (loadDefault)
                    {
                        var defaultModel = this.SD.Models.FirstOrDefault();
                        if (defaultModel != null)
                        {
                            this.SD.InitializeSessions(defaultModel);
                            if (!this.SD.IsInitialized)
                            {
                                return this.StatusCode(500, "Failed to initialize default model sessions.");
                            }
                        }
                        else
                        {
                            return this.StatusCode(500, "No default model available to load.");
                        }
                    }
                    else
                    {
                        return this.StatusCode(500, "Model sessions are not initialized. Please load a model first.");
                    }
                }
                float[] imageData = await this.SD.GenerateInternalAsync(
                    request.Prompt,
                    request.NegativePrompt,
                    request.Steps,
                    request.GuidanceScale,
                    request.Seed,
                    null);
                var imageObj = new ImageObj(imageData, 512, 512);

                var imageId = this.Images.AddImage(imageObj, false);
                return this.Ok(imageObj.Id);
            }
            catch (Exception ex)
            {
                await StaticLogger.LogAsync($"Error in GenerateImageAsync: {ex.Message}");
                return this.StatusCode(500, "An error occurred while generating the image.");
            }
        }


        [HttpGet("sd-generate-download")]
        public async Task<ActionResult> GenerateAndDownloadAsync([FromQuery] string prompt = "", [FromQuery] string negativePrompt = "text", [FromQuery] int steps = 50, [FromQuery] float guidanceScale = 7.5f, [FromQuery] long seed = -1, [FromQuery] int batch = 1, [FromQuery] string imageFormat = "png", [FromQuery] bool forceInitialize = true)
        {
            string[] defaultPrompts = [
                "A high-quality portrait of a cyberpunk pirate, neon lighting, highly detailed face, futuristic clothing.",
                "A peaceful lake in a pine forest at sunrise, misty atmosphere, reflections on the water surface, cinematic lighting.",
                "A giant floating jellyfish in a desert, starry night sky, glowing tentacles, surreal digital art style.",
                "An ancient Greek temple overgrown with ivy and flowers, marble textures, sunlight filtering through trees, hyperrealistic.",
                "A brave cat knight wearing golden armor, standing on a mountain peak, oil painting style, vibrant colors."
                ];

            try
            {
                if (!this.SD.IsInitialized)
                {
                    if (forceInitialize)
                    {
                        var defaultModel = this.SD.Models.FirstOrDefault();
                        if (defaultModel != null)
                        {
                            this.SD.InitializeSessions(defaultModel);
                            if (!this.SD.IsInitialized)
                            {
                                return this.StatusCode(500, "Failed to initialize default model sessions.");
                            }
                        }
                        else
                        {
                            return this.StatusCode(500, "No default model available to load.");
                        }
                    }
                    else
                    {
                        return this.StatusCode(500, "Model sessions are not initialized. Please load a model first.");
                    }
                }

                List<ImageObj> generatedImages = [];
                var fileBytes = Array.Empty<byte>();
                string? file = null;
                string? contentType = null;
                for (int i = 0; i < batch; i++)
                {
                    await StaticLogger.LogAsync($"Generating image {i + 1} of {batch} with prompt: '{prompt}'");
                    Stopwatch sw = Stopwatch.StartNew();

                    string randomPrompt = defaultPrompts[new Random().Next(defaultPrompts.Length)];
                    if (string.IsNullOrEmpty(prompt))
                    {
                        await StaticLogger.LogAsync($"No prompt provided. Using random default prompt: '{randomPrompt}'");
                    }

                    float[] imageData = await this.SD.GenerateInternalAsync(
                        string.IsNullOrEmpty(prompt) ? randomPrompt : prompt,
                        negativePrompt,
                        steps,
                        guidanceScale,
                        seed == -1 ? new Random().Next() : seed,
                        null);
                    var imageObj = new ImageObj(imageData, 512, 512);
                    this.Images.AddImage(imageObj, false);
                    generatedImages.Add(imageObj);

                    sw.Stop();
                    await StaticLogger.LogAsync($"Image {i + 1} generated with ID: {imageObj.Id} within {sw.ElapsedMilliseconds} ms");
                }

                if (generatedImages.Count == 0)
                {
                    return this.StatusCode(500, "No images were generated.");
                }

                if (batch == 1)
                {
                    var singleImage = generatedImages[0];
                    string tempPath = Path.GetTempPath();
                    string tempFile = Path.Combine(tempPath, $"{singleImage.Id}.{imageFormat}");
                    string? exportFile = await singleImage.ExportAsync(tempFile, imageFormat);
                    if (exportFile == null)
                    {
                        return this.StatusCode(500, "Failed to export image.");
                    }

                    fileBytes = await System.IO.File.ReadAllBytesAsync(exportFile);
                    contentType = imageFormat.ToLower() switch
                    {
                        "png" => "image/png",
                        "jpeg" or "jpg" => "image/jpeg",
                        "bmp" => "image/bmp",
                        _ => "application/octet-stream"
                    };
                }
                else
                {
                    file = await ImageCollection.ExportZipAsync(generatedImages, null, imageFormat);
                    if (file == null)
                    {
                        return this.StatusCode(500, "Failed to export images as ZIP.");
                    }

                    fileBytes = await System.IO.File.ReadAllBytesAsync(file);
                    contentType = "application/zip";
                }

                return this.File(fileBytes, contentType, Path.GetFileName(file));
            }
            catch (Exception ex)
            {
                await StaticLogger.LogAsync($"Error in GenerateImageAsync: {ex.Message}");
                return this.StatusCode(500, "An error occurred while generating the image.");
            }
        }

        [HttpGet("sd-debug-tokenizer")]
        public async Task<IActionResult> DebugTokenizerAsync([FromQuery] string text = "Windows Microsoft XP Great Plains wallpaper, aesthetic, retrowave.", [FromQuery] bool forceInitialize = true)
        {
            try
            {
                if (!this.SD.IsInitialized)
                {
                    if (forceInitialize)
                    {
                        var defaultModel = this.SD.Models.FirstOrDefault();
                        if (defaultModel != null)
                        {
                            this.SD.InitializeSessions(defaultModel);
                            if (!this.SD.IsInitialized)
                            {
                                return this.StatusCode(500, "Failed to initialize default model sessions.");
                            }
                        }
                        else
                        {
                            return this.StatusCode(500, "No default model available to load.");
                        }
                    }
                    else
                    {
                        return this.StatusCode(500, "Model sessions are not initialized. Please load a model first.");
                    }
                }

                await this.SD.DebugTokenizationAsync(text);
                return this.Ok();
            }
            catch (Exception ex)
            {
                await StaticLogger.LogAsync($"Error in DebugTokenizerAsync: {ex.Message}");
                return this.StatusCode(500, "An error occurred while tokenizing the text.");
            }
        }



    }
}

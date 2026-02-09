using SharpAI.Core;
using SharpAI.Shared;
using Microsoft.AspNetCore.Mvc;

namespace SharpAI.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class ImageController : ControllerBase
    {
        private readonly ImageCollection Images;



        public ImageController(ImageCollection images)
        {
            this.Images = images;
        }

        [HttpDelete("delete")]
        public async Task<ActionResult> DeleteImageAsync([FromQuery] Guid id)
        {
            try
            {
                var removed = this.Images.RemoveImage(id);
                Console.WriteLine($"[ImageController] DeleteImage called with id={id}, removed={removed}");
                return removed ? this.NoContent() : this.NotFound();
            }
            catch (Exception ex)
            {
                return this.StatusCode(500, ex.Message);
            }
            finally
            {
                await Task.CompletedTask;
            }
        }

        [HttpDelete("{id:guid}")]
        public async Task<ActionResult> DeleteImageByRouteAsync(Guid id)
        {
            try
            {
                var removed = this.Images.RemoveImage(id);
                Console.WriteLine($"[ImageController] DeleteImageByRoute called with id={id}, removed={removed}");
                return removed ? this.NoContent() : this.NotFound();
            }
            catch (Exception ex)
            {
                return this.StatusCode(500, ex.Message);
            }
            finally
            {
                await Task.CompletedTask;
            }
        }



        [HttpGet("list")]
        public async Task<ActionResult<IEnumerable<ImageObjInfo>>> GetImagesListAsync()
        {
            var list = new List<ImageObjInfo>();
            foreach (var img in this.Images.Images.Values)
            {
                var thumb = await img.GetThumbnailBase64Async();
                list.Add(new ImageObjInfo
                {
                    FilePath = img.FilePath,
                    Id = img.Id,
                    Width = img.Width,
                    Height = img.Height,
                    Channels = img.Channels,
                    BitDepth = img.BitDepth,
                    SizeInKb = img.SizeInKb,
                    ThumbnailBase64 = thumb
                });
            }
            return this.Ok(list);
        }

        [HttpGet("info")]
        public async Task<ActionResult<ImageObjInfo>> GetImageInfoAsync([FromQuery] Guid id)
        {
            if (!this.Images.Images.ContainsKey(id))
            {
                return this.NotFound($"Image with ID '{id}' not found.");
            }
            var img = this.Images.Images[id];
            var thumb = await img.GetThumbnailBase64Async();
            return this.Ok(new ImageObjInfo
            {
                FilePath = img.FilePath,
                Id = img.Id,
                Width = img.Width,
                Height = img.Height,
                Channels = img.Channels,
                BitDepth = img.BitDepth,
                SizeInKb = img.SizeInKb,
                ThumbnailBase64 = thumb
            });
        }

        [HttpGet("data")]
        public async Task<ActionResult<ImageObjData>> GetImageDataAsync([FromQuery] Guid id)
        {
            if (!this.Images.Images.ContainsKey(id))
            {
                return this.NotFound($"Image with ID '{id}' not found.");
            }

            var img = this.Images.Images[id];
            var data = await img.GetBase64ImageDataAsync();
            if (data == null)
            {
                return this.StatusCode(500, "Failed to retrieve image data.");
            }

            return this.Ok(new ImageObjData
            {
                Id = img.Id,
                Data = data
            });
        }

        [HttpGet("download")]
        public async Task<IActionResult> DownloadImageAsync([FromQuery] Guid id, [FromQuery] string format = "jpg")
        {
            if (!this.Images.Images.ContainsKey(id))
            {
                return this.NotFound($"Image with ID '{id}' not found.");
            }
            var img = this.Images.Images[id];

            try
            {
                string tempPath = Path.GetTempPath();
                string tempFile = Path.Combine(tempPath, $"{id}.{format}");
                string? exportFile = await img.ExportAsync(tempFile, format);
                if (exportFile == null)
                {
                    return this.StatusCode(500, "Failed to export image.");
                }
                var fileBytes = await System.IO.File.ReadAllBytesAsync(exportFile);
                var contentType = format.ToLower() switch
                {
                    "png" => "image/png",
                    "jpeg" or "jpg" => "image/jpeg",
                    "bmp" => "image/bmp",
                    _ => "application/octet-stream"
                };

                return this.File(fileBytes, contentType, Path.GetFileName(exportFile));

            }
            catch (Exception ex)
            {
                return this.StatusCode(500, $"Error exporting image: {ex.Message}");
            }
        }

        [HttpPost("upload-file")]
        public async Task<ActionResult<ImageObjInfo>> UploadImageFileAsync(IFormFile file)
        {
            if (file == null || file.Length == 0)
            {
                return this.BadRequest("No file uploaded.");
            }
            try
            {
                return await Task.Run(() =>
                {
                    using var stream = file.OpenReadStream();
                    var img = new ImageObj(stream);
                    ; if (img == null)
                    {
                        return this.StatusCode(500, "Failed to create image from uploaded file.");
                    }
                    this.Images.AddImage(img);
                    return this.Ok(new ImageObjInfo
                    {
                        FilePath = img.FilePath,
                        Id = img.Id,
                        Width = img.Width,
                        Height = img.Height,
                        Channels = img.Channels,
                        BitDepth = img.BitDepth,
                        SizeInKb = img.SizeInKb
                    });
                });
            }
            catch (Exception ex)
            {
                return this.StatusCode(500, $"Error uploading image: {ex.Message}");
            }
        }

        [HttpGet("thumbnail-from-data")]
        public async Task<ActionResult<string>> GetThumbnailFromImageDataAsync([FromQuery] string base64ImageData, [FromQuery] int pxDiagonal = 128)
        {
            try
            {
                var data = await this.Images.GenerateThumbnailFromBase64Async(base64ImageData, pxDiagonal);
                return this.Ok(data);
            }
            catch (Exception ex)
            {
                return this.StatusCode(500, $"Error generating thumbnail: {ex.Message}");
            }



        }





    }
}

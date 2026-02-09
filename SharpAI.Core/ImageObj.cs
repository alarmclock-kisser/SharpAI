using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SharpAI.Core
{
    public class ImageObj : IDisposable
    {
        public readonly Guid Id = Guid.NewGuid();
        public readonly DateTime CreatedAt = DateTime.UtcNow;

        public string FilePath { get; set; } = string.Empty;
        public Image<Rgba32>? Img { get; set; } = null;
        public string? Base64DataCache { get; set; } = null;
        public int Width { get; private set; } = 0;
        public int Height { get; private set; } = 0;

        public int Channels => 4;
        public int BitDepth => 8;

        public double SizeInKb => this.Width * this.Height * 4 / 1024.0;

        public IntPtr Pointer { get; set; } = nint.Zero;
        public bool OnHost => this.Img != null;
        public bool OnDevice => this.Pointer != nint.Zero;


        public ImageObj(Image<Rgba32> img)
        {
            try
            {
                ArgumentNullException.ThrowIfNull(img);
                this.Img = img;
                this.Width = img.Width;
                this.Height = img.Height;
            }
            catch (Exception ex)
            {
                LogException(ex, "Failed to initialize ImageObj from Image instance.");
                throw;
            }
        }

        public ImageObj(float[] floatData, int width, int height)
        {
            try
            {
                if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
                ArgumentNullException.ThrowIfNull(floatData);

                this.Width = width;
                this.Height = height;

                // Wir erstellen ein RGBA32 Ziel-Array (4 Bytes pro Pixel)
                var pixelData = new byte[width * height * 4];

                for (int i = 0; i < width * height; i++)
                {
                    int pixelIdx = i * 4;
                    int floatIdx = i * 3; // Input hat nur R, G, B

                    // Konvertierung von float [0.0, 1.0] zu byte [0, 255]
                    pixelData[pixelIdx + 0] = (byte) Math.Clamp(floatData[floatIdx + 0] * 255, 0, 255); // R
                    pixelData[pixelIdx + 1] = (byte) Math.Clamp(floatData[floatIdx + 1] * 255, 0, 255); // G
                    pixelData[pixelIdx + 2] = (byte) Math.Clamp(floatData[floatIdx + 2] * 255, 0, 255); // B
                    pixelData[pixelIdx + 3] = 255; // Volle Deckkraft (Alpha)
                }

                this.Img = Image.LoadPixelData<Rgba32>(pixelData, width, height);
            }
            catch (Exception ex)
            {
                LogException(ex, "Failed to create ImageObj from interleaved float data.");
                throw;
            }
        }

        public ImageObj(string mime = "image/png", string base64data = "")
        {
            try
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(base64data);
                var imageData = Convert.FromBase64String(base64data);
                using var img = Image.Load<Rgba32>(imageData);
                this.Img = img.Clone();
                this.Width = this.Img.Width;
                this.Height = this.Img.Height;
            }
            catch (Exception ex)
            {
                LogException(ex, "Failed to initialize ImageObj from base64 data.");
                throw;
            }
        }

        public async Task<string?> GetThumbnailBase64Async(int diagonalSize = 128)
        {
            try
            {
                if (diagonalSize <= 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(diagonalSize));
                }

                Image<Rgba32>? source = this.Img;
                Image<Rgba32>? loaded = null;
                try
                {
                    if (source == null && !string.IsNullOrWhiteSpace(this.FilePath) && File.Exists(this.FilePath))
                    {
                        loaded = await Image.LoadAsync<Rgba32>(this.FilePath);
                        source = loaded;
                    }

                    if (source == null || source.Width <= 0 || source.Height <= 0)
                    {
                        return null;
                    }

                    var maxDim = Math.Max(source.Width, source.Height);
                    var scale = diagonalSize / (double) maxDim;
                    var targetWidth = Math.Max(1, (int) Math.Round(source.Width * scale));
                    var targetHeight = Math.Max(1, (int) Math.Round(source.Height * scale));

                    using var thumb = source.Clone(ctx => ctx.Resize(targetWidth, targetHeight));
                    using var ms = new MemoryStream();
                    await thumb.SaveAsPngAsync(ms);
                    return Convert.ToBase64String(ms.ToArray());
                }
                finally
                {
                    loaded?.Dispose();
                }
            }
            catch (Exception ex)
            {
                LogException(ex, "Failed to generate thumbnail.");
                return null;
            }
        }

        public ImageObj(int width, int height, string hexColor = "#000000")
        {
            try
            {
                if (width <= 0 || height <= 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(width), "Width and height must be positive.");
                }

                this.Width = width;
                this.Height = height;
                var color = Color.ParseHex(hexColor).ToPixel<Rgba32>();
                var img = new Image<Rgba32>(width, height);
                img.Mutate(c => c.Clear(color));
                this.Img = img;
            }
            catch (Exception ex)
            {
                LogException(ex, "Failed to initialize ImageObj with color.");
                throw;
            }
        }

        public ImageObj(int width, int height, float[] usages, string backColor = "#2F4F4F", string foreColor = "#32CD32", bool indicateThreadIds = false)
        {
            try
            {
                if (width <= 0 || height <= 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(width), "Width and height must be positive.");
                }
                this.Width = width;
                this.Height = height;
                var img = new Image<Rgba32>(width, height);
                img.Mutate(c => c.Clear(Color.Black));
                if (usages != null && usages.Length > 1)
                {
                    int cpuCount = usages.Length - 1; // First element is total average
                    int columns = cpuCount <= 6 ? 3 : 4;
                    int rows = (int) Math.Ceiling(cpuCount / (double) columns);
                    int cellWidth = Math.Max(1, width / columns);
                    int cellHeight = Math.Max(1, height / rows);
                    int padding = Math.Max(1, Math.Min(cellWidth, cellHeight) / 8);

                    for (int i = 0; i < cpuCount; i++)
                    {
                        int row = i / columns;
                        int col = i % columns;
                        int x = col * cellWidth;
                        int y = row * cellHeight;
                        var cellRect = new Rectangle(x, y, cellWidth, cellHeight);
                        img.Mutate(c => c.Fill(Color.ParseHex(foreColor), cellRect));

                        float usage = Math.Clamp(usages[i + 1], 0f, 100f);
                        int barHeight = (int) ((cellHeight - padding * 2) * (usage / 100f));
                        if (barHeight > 0)
                        {
                            var barRect = new Rectangle(
                                x + padding,
                                y + cellHeight - padding - barHeight,
                                Math.Max(1, cellWidth - padding * 2),
                                barHeight);
                            img.Mutate(c => c.Fill(Color.ParseHex(backColor), barRect));
                        }

                        if (indicateThreadIds)
                        {
                            string threadIdText = $"#{i}";
                            var font = SystemFonts.CreateFont("Arial", Math.Max(8, padding * 2));
                            var textOptions = new TextOptions(font)
                            {
                                HorizontalAlignment = HorizontalAlignment.Center,
                                VerticalAlignment = VerticalAlignment.Top,
                                Origin = new PointF(x + cellWidth / 2f, y + padding / 2f)
                            };
                            img.Mutate(c => c.DrawText(threadIdText, font, Color.White, textOptions.Origin));
                        }
                    }
                }
                this.Img = img;
            }
            catch (Exception ex)
            {
                LogException(ex, "Failed to initialize ImageObj with CPU usages.");
                throw;
            }
        }


        public static async Task<ImageObj?> DrawWaveformAsync(AudioObj audioObj, int width = 256, int height = 64, int? samplesPerPixel = null, int offset = 0, string backColorHex = "#FFFFFF", string graphColorHex = "#000000", int? maxWorkers = null)
        {
            maxWorkers ??= Environment.ProcessorCount;
            maxWorkers = Math.Max(1, maxWorkers.Value);

            try
            {
                // 1. Initialisierung des Zielbildes
                ImageObj imageObj = new(width, height, backColorHex);
                if (imageObj.Img == null)
                {
                    LogException(new Exception("Failed to create ImageObj for waveform."), "Failed to initialize ImageObj for waveform.");
                    return null;
                }

                var graphColor = Color.ParseHex(graphColorHex);

                // 2. Berechnung der Parameter
                // Wir berücksichtigen nur den ersten Channel für die Wellenform-Analyse, 
                // oder wir mitteln die Channels, falls gewünscht.
                int channels = audioObj.Channels;
                int totalSamples = audioObj.Length / channels;
                int availableSamples = totalSamples - offset;

                if (availableSamples <= 0)
                {
                    return imageObj;
                }

                // Wenn samplesPerPixel nicht gesetzt ist, skalieren wir das Audio passend zur Breite
                int spp = samplesPerPixel ?? (int) Math.Ceiling((double) availableSamples / width);

                // 3. Parallelisierung vorbereiten
                // Wir berechnen die Min/Max Werte für jede vertikale Linie im Bild
                float[] minValues = new float[width];
                float[] maxValues = new float[width];

                await Task.Run(() =>
                {
                    Parallel.For(0, width, new ParallelOptions { MaxDegreeOfParallelism = maxWorkers.Value }, x =>
                    {
                        int startSample = offset + (x * spp);
                        if (startSample >= totalSamples)
                        {
                            return;
                        }

                        int endSample = Math.Min(startSample + spp, totalSamples);
                        float min = 0;
                        float max = 0;

                        for (int s = startSample; s < endSample; s++)
                        {
                            // Wir nehmen den Durchschnitt über alle Channels an diesem Sample-Punkt
                            float sampleSum = 0;
                            for (int c = 0; c < channels; c++)
                            {
                                sampleSum += audioObj.Data[s * channels + c];
                            }
                            float value = sampleSum / channels;

                            if (value < min)
                            {
                                min = value;
                            }

                            if (value > max)
                            {
                                max = value;
                            }
                        }

                        minValues[x] = min;
                        maxValues[x] = max;
                    });
                });

                // 4. Zeichnen auf dem ImageSharp Objekt
                imageObj.Img.Mutate(ctx =>
                {
                    float midY = height / 2f;

                    for (int x = 0; x < width; x++)
                    {
                        // Skalierung: Da Float-Samples von -1.0 bis 1.0 gehen, 
                        // multiplizieren wir mit der halben Höhe.
                        float yMax = midY - (maxValues[x] * midY);
                        float yMin = midY - (minValues[x] * midY);

                        // Sicherstellen, dass mindestens ein Pixel gezeichnet wird (für leise Stellen)
                        if (Math.Abs(yMax - yMin) < 1f)
                        {
                            yMin = midY + 0.5f;
                            yMax = midY - 0.5f;
                        }

                        // Zeichne vertikale Linie für diesen Pixel-Slot
                        ctx.DrawLine(graphColor, 1f, new PointF(x, yMin), new PointF(x, yMax));
                    }
                });

                return imageObj;
            }
            catch (Exception ex)
            {
                LogException(ex, "Failed to draw waveform.");
                return null;
            }
        }

        public ImageObj(byte[] imageData, int width, int height)
        {
            try
            {
                if (width <= 0 || height <= 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(width), "Width and height must be positive.");
                }

                ArgumentNullException.ThrowIfNull(imageData);
                var expectedLength = width * height * 4;
                if (imageData.Length != expectedLength)
                {
                    throw new ArgumentException($"Image data length {imageData.Length} does not match expected {expectedLength}.", nameof(imageData));
                }

                this.Width = width;
                this.Height = height;
                this.Img = Image.LoadPixelData<Rgba32>(imageData, width, height);
            }
            catch (Exception ex)
            {
                LogException(ex, "Failed to initialize ImageObj from raw data.");
                throw;
            }
        }

        public ImageObj(string filePath)
        {
            try
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
                if (!File.Exists(filePath))
                {
                    throw new FileNotFoundException("Image file not found.", filePath);
                }

                this.FilePath = filePath;
                this.Img = Image.Load<Rgba32>(filePath);
                this.Width = this.Img.Width;
                this.Height = this.Img.Height;
            }
            catch (Exception ex)
            {
                LogException(ex, $"Failed to load ImageObj from file '{filePath}'.");
                throw;
            }
        }

        public ImageObj(Stream stream)
        {
            try
            {
                ArgumentNullException.ThrowIfNull(stream);
                this.Img = Image.Load<Rgba32>(stream);
                this.Width = this.Img.Width;
                this.Height = this.Img.Height;
            }
            catch (Exception ex)
            {
                LogException(ex, "Failed to load ImageObj from stream.");
                throw;
            }
        }

        public byte[] GetImageData(bool nullImg = true)
        {
            try
            {
                if (this.Img == null || this.Width <= 0 || this.Height <= 0)
                {
                    return Array.Empty<byte>();
                }
                var data = new byte[this.Width * this.Height * 4];
                this.Img.CopyPixelDataTo(data);
                if (nullImg)
                {
                    this.Img.Dispose();
                    this.Img = null;
                    this.Width = 0;
                    this.Height = 0;
                }
                return data;
            }
            catch (Exception ex)
            {
                LogException(ex, "Failed to get image data.");
                return Array.Empty<byte>();
            }
        }

        public async Task<byte[]> GetImageDataAsync(bool nullImg = true)
        {
            try
            {
                if (this.Img == null || this.Width <= 0 || this.Height <= 0)
                {
                    return Array.Empty<byte>();
                }

                var data = new byte[this.Width * this.Height * 4];
                await Task.Run(() => this.Img.CopyPixelDataTo(data));

                if (nullImg)
                {
                    this.Img.Dispose();
                    this.Img = null;
                    this.Width = 0;
                    this.Height = 0;
                }

                return data;
            }
            catch (Exception ex)
            {
                LogException(ex, "Failed to get image data.");
                return Array.Empty<byte>();
            }
        }

        public string? GetBase64ImageData(bool nullImg = true)
        {
            try
            {
                var imageData = this.GetImageData(nullImg);
                if (imageData.Length == 0)
                {
                    return null;
                }
                return Convert.ToBase64String(imageData);
            }
            catch (Exception ex)
            {
                LogException(ex, "Failed to get base64 image data.");
                return null;
            }
        }

        public async Task<string?> GetBase64ImageDataAsync(bool nullImg = true, bool cacheData = false)
        {
            try
            {
                var imageData = await this.GetImageDataAsync(nullImg);
                if (imageData.Length == 0)
                {
                    return null;
                }

                string base64 = Convert.ToBase64String(imageData);
                if (cacheData)
                {
                    this.Base64DataCache = base64;
                }

                return base64;
            }
            catch (Exception ex)
            {
                LogException(ex, "Failed to get base64 image data.");
                return null;
            }
        }

        public async Task<string?> GetPngBase64Async(bool nullImg = true)
        {
            try
            {
                Image<Rgba32>? source = this.Img;
                Image<Rgba32>? loaded = null;
                try
                {
                    if (source == null && !string.IsNullOrWhiteSpace(this.FilePath) && File.Exists(this.FilePath))
                    {
                        loaded = await Image.LoadAsync<Rgba32>(this.FilePath);
                        source = loaded;
                    }

                    if (source == null || source.Width <= 0 || source.Height <= 0)
                    {
                        return null;
                    }

                    using var ms = new MemoryStream();
                    await source.SaveAsPngAsync(ms);
                    var bytes = ms.ToArray();

                    if (nullImg)
                    {
                        // If the source was the internal Img, dispose it.
                        if (ReferenceEquals(source, this.Img) && this.Img != null)
                        {
                            this.Img.Dispose();
                            this.Img = null;
                            this.Width = 0;
                            this.Height = 0;
                        }
                    }

                    return Convert.ToBase64String(bytes);
                }
                finally
                {
                    loaded?.Dispose();
                }
            }
            catch (Exception ex)
            {
                LogException(ex, "Failed to get PNG base64 image data.");
                return null;
            }
        }


        public bool SetImageData(byte[] imageData, bool nullPointer = true)
        {
            try
            {
                if (imageData == null || imageData.Length == 0 || this.Width <= 0 || this.Height <= 0)
                {
                    return false;
                }
                var expectedLength = this.Width * this.Height * 4;
                if (imageData.Length != expectedLength)
                {
                    return false;
                }
                this.Img?.Dispose();
                this.Img = Image.LoadPixelData<Rgba32>(imageData, this.Width, this.Height);
                if (nullPointer)
                {
                    this.Pointer = nint.Zero;
                }
                return true;
            }
            catch (Exception ex)
            {
                LogException(ex, "Failed to set image data.");
                return false;
            }
        }

        public async Task<bool> SetImageDataAsync(byte[] imageData, bool nullPointer = true)
        {
            try
            {
                if (imageData == null || imageData.Length == 0 || this.Width <= 0 || this.Height <= 0)
                {
                    return false;
                }

                var expectedLength = this.Width * this.Height * 4;
                if (imageData.Length != expectedLength)
                {
                    return false;
                }

                this.Img?.Dispose();
                this.Img = await Task.Run(() => Image.LoadPixelData<Rgba32>(imageData, this.Width, this.Height));

                if (nullPointer)
                {
                    this.Pointer = nint.Zero;
                }

                return true;
            }
            catch (Exception ex)
            {
                LogException(ex, "Failed to set image data.");
                return false;
            }
        }

        public bool SetImageBase64Data(string base64Data, bool nullPointer = true)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(base64Data))
                {
                    return false;
                }
                var imageData = Convert.FromBase64String(base64Data);
                return this.SetImageData(imageData, nullPointer);
            }
            catch (Exception ex)
            {
                LogException(ex, "Failed to set image from base64 data.");
                return false;
            }
        }

        public async Task<bool> SetImageBase64DataAsync(string base64Data, bool nullPointer = true)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(base64Data))
                {
                    return false;
                }
                var imageData = Convert.FromBase64String(base64Data);
                return await this.SetImageDataAsync(imageData, nullPointer);
            }
            catch (Exception ex)
            {
                LogException(ex, "Failed to set image from base64 data.");
                return false;
            }
        }


        public string? Export(string path, string format = "png")
        {
            try
            {
                Image<Rgba32>? source = this.Img;
                Image<Rgba32>? loaded = null;
                try
                {
                    if (source == null && !string.IsNullOrWhiteSpace(this.FilePath) && File.Exists(this.FilePath))
                    {
                        loaded = Image.Load<Rgba32>(this.FilePath);
                        source = loaded;
                    }

                    if (source == null)
                    {
                        return null;
                    }

                    switch (format.ToLower())
                    {
                        case "png":
                            source.SaveAsPng(path);
                            break;
                        case "jpeg":
                        case "jpg":
                            source.SaveAsJpeg(path);
                            break;
                        case "bmp":
                            source.SaveAsBmp(path);
                            break;
                        case "gif":
                            source.SaveAsGif(path);
                            break;
                        default:
                            throw new ArgumentException($"Unsupported image format: {format}", nameof(format));
                    }

                    return path;
                }
                finally
                {
                    loaded?.Dispose();
                }
            }
            catch (Exception ex)
            {
                LogException(ex, "Failed to export image.");
                return null;
            }
        }

        public async Task<string?> ExportAsync(string path, string format = "png")
        {
            try
            {
                Image<Rgba32>? source = this.Img;
                Image<Rgba32>? loaded = null;
                try
                {
                    if (source == null && !string.IsNullOrWhiteSpace(this.FilePath) && File.Exists(this.FilePath))
                    {
                        loaded = await Image.LoadAsync<Rgba32>(this.FilePath);
                        source = loaded;
                    }

                    if (source == null)
                    {
                        return null;
                    }

                    await Task.Run(() =>
                    {
                        switch (format.ToLower())
                        {
                            case "png":
                                source.SaveAsPng(path);
                                break;
                            case "jpeg":
                            case "jpg":
                                source.SaveAsJpeg(path);
                                break;
                            case "bmp":
                                source.SaveAsBmp(path);
                                break;
                            case "gif":
                                source.SaveAsGif(path);
                                break;
                            default:
                                throw new ArgumentException($"Unsupported image format: {format}", nameof(format));
                        }
                    });

                    return path;
                }
                finally
                {
                    loaded?.Dispose();
                }
            }
            catch (Exception ex)
            {
                LogException(ex, "Failed to export image.");
                return null;
            }
        }



        public async Task<ImageObj?> CloneAsync()
        {
            try
            {
                if (this.Img == null)
                {
                    return await Task.FromResult<ImageObj?>(null);
                }

                var clonedImg = this.Img.Clone();
                var clone = new ImageObj(clonedImg)
                {
                    Pointer = nint.Zero
                };

                return clone;
            }
            catch (Exception ex)
            {
                LogException(ex, "Failed to clone image.");
                return null;
            }
        }



        public static async Task<string?> SerializeImageFileAsync(byte[] imageFileBytes, double scale = 1.0, string format = "png")
        {
            try
            {
                // 1. Bild asynchron aus dem Byte-Array laden
                using var image = await Image.LoadAsync(new MemoryStream(imageFileBytes));

                // 2. Resize nur anwenden, wenn der Scale-Faktor nicht 1.0 ist
                if (Math.Abs(scale - 1.0) > 0.001)
                {
                    int newWidth = (int) Math.Max(1, image.Width * scale);
                    int newHeight = (int) Math.Max(1, image.Height * scale);

                    image.Mutate(x => x.Resize(newWidth, newHeight, KnownResamplers.Lanczos3));
                }

                // 3. Encoder basierend auf dem Format-String auswählen
                IImageEncoder encoder = format.ToLower() switch
                {
                    "jpg" or "jpeg" => new JpegEncoder(),
                    "png" => new PngEncoder(),
                    "webp" => new WebpEncoder(),
                    _ => new PngEncoder() // Fallback
                };

                // 4. In MemoryStream speichern und als Base64 konvertieren
                using var ms = new MemoryStream();
                await image.SaveAsync(ms, encoder);

                return Convert.ToBase64String(ms.ToArray());
            }
            catch (Exception ex)
            {
                await StaticLogger.LogAsync($"Failed to serialize image file. Exception: {ex}");
                return null;
            }
        }

        public static async Task<string?> SerializeImageFileAsync(string imageFilePath, double scale = 1.0, string format = "png")
        {
            try
            {
                if (!File.Exists(imageFilePath))
                {
                    return null;
                }
                var imageFileBytes = await File.ReadAllBytesAsync(imageFilePath);
                return await SerializeImageFileAsync(imageFileBytes, scale, format);
            }
            catch (Exception ex)
            {
                LogException(ex, "Failed to serialize image file.");
                return null;
            }
        }

        public void Dispose()
        {
            try
            {
                this.Img?.Dispose();
            }
            catch (Exception ex)
            {
                LogException(ex, "Failed to dispose ImageObj.");
            }
            finally
            {
                this.Img = null;
                this.Pointer = nint.Zero;
                GC.SuppressFinalize(this);
            }
        }

        private static void LogException(Exception ex, string message)
        {
            try
            {
                StaticLogger.Log($"{message} Exception: {ex}");
                Console.WriteLine($"{message} Exception: {ex}");
            }
            catch
            {
                // Swallow logging errors to avoid secondary failures
            }
        }
    }
}

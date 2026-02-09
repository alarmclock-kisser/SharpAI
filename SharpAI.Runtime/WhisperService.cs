using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using SharpAI.Core;
using Whisper.net;

namespace SharpAI.Runtime
{
    public class WhisperService : IDisposable
    {
        public List<string> ModelDirectories { get; set; } = [
            "D:/Models/WhisperTranscription"
            ];
        public List<string> ModelFiles { get; private set; } = [];

        public string? CurrentModelFile { get; private set; } = null;

        private WhisperFactory? Factory = null;
        public bool IsInitialized => this.Factory != null;
        private bool _wasCudaRequested = false;



        public WhisperService(string[]? additionalDirectories = null)
        {
            this.GetModelFiles(additionalDirectories);
        }

        public List<string> GetModelFiles(string[]? additionalDirectories = null)
        {
            if (additionalDirectories != null) this.ModelDirectories.AddRange(additionalDirectories);
            this.ModelDirectories = this.ModelDirectories.Where(Directory.Exists).Distinct().ToList();
            this.ModelFiles = this.ModelDirectories.SelectMany(dir => Directory.GetFiles(dir, "*.bin", SearchOption.AllDirectories)).ToList();
            return this.ModelFiles;
        }

        public async Task<bool> InitializeAsync(string? modelNameOrPath = null, bool useCuda = false)
        {
            return await Task.Run(() =>
            {
                modelNameOrPath = ResolveModelPath(modelNameOrPath);
                if (modelNameOrPath == null) return false;

                if (this.IsInitialized && _wasCudaRequested != useCuda)
                {
                    this.Factory?.Dispose();
                    this.Factory = null;
                }

                try
                {
                    this.CurrentModelFile = modelNameOrPath;
                    _wasCudaRequested = useCuda;
                    StaticLogger.Log($"Initializing Whisper (CUDA: {useCuda}) with model: {Path.GetFileName(modelNameOrPath)}");

                    this.Factory = WhisperFactory.FromPath(modelNameOrPath);
                    return true;
                }
                catch (Exception ex)
                {
                    StaticLogger.Log($"Initialization error: {ex.Message}");
                    return false;
                }
            });
        }



        public async Task<string> TranscribeAsync(AudioObj audio, string? language = null, bool useCuda = false, CancellationToken ct = default)
        {
            if (!IsInitialized || _wasCudaRequested != useCuda)
                await InitializeAsync(this.CurrentModelFile, useCuda);

            if (this.Factory == null) return string.Empty;

            await PreProcessAudio(audio);

            try
            {
                var builder = Factory.CreateBuilder()
                    .WithThreads(CalculateThreads(useCuda));

                // Nur wenn eine Sprache explizit mitgegeben wird, nutzen wir sie.
                // Ansonsten triggert Whisper.net automatisch die Erkennung.
                if (!string.IsNullOrEmpty(language))
                {
                    builder.WithLanguage(language);
                }
                else
                {
                    await StaticLogger.LogAsync("No language specified. Whisper will auto-detect language...");
                }

                using var processor = builder.Build();
                var fullText = new StringBuilder();

                await foreach (var segment in processor.ProcessAsync(audio.Data, ct))
                {
                    fullText.Append(segment.Text);
                }

                return fullText.ToString().Trim();
            }
            catch (Exception ex)
            {
                await StaticLogger.LogAsync($"Transcription error: {ex.Message}");
                return string.Empty;
            }
        }

        public async IAsyncEnumerable<string> TranscribeStreamAsync(AudioObj audio, string? language = null, bool useCuda = false, [EnumeratorCancellation] CancellationToken ct = default)
        {
            if (!IsInitialized || _wasCudaRequested != useCuda)
                await InitializeAsync(this.CurrentModelFile, useCuda);

            if (this.Factory == null) yield break;

            await PreProcessAudio(audio);

            var builder = Factory.CreateBuilder()
                .WithThreads(CalculateThreads(useCuda));

            if (!string.IsNullOrEmpty(language))
            {
                builder.WithLanguage(language);
            }

            using var processor = builder.Build();

            await foreach (var segment in processor.ProcessAsync(audio.Data))
            {
                if (ct.IsCancellationRequested) yield break;

                yield return segment.Text;
            }
        }



        private int CalculateThreads(bool useCuda)
        {
            // Bei CUDA entlasten wir die CPU, bei reinem CPU-Mode nutzen wir fast alle Kerne.
            return useCuda ? 2 : Math.Max(1, Environment.ProcessorCount - 1);
        }

        private async Task PreProcessAudio(AudioObj audio)
        {
            if (audio.SampleRate != 16000) await audio.ResampleAsync(16000);
            if (audio.Channels != 1) await audio.RechannelAsync(1);
        }

        private string? ResolveModelPath(string? modelNameOrPath)
        {
            if (modelNameOrPath == null) return ModelFiles.FirstOrDefault();
            if (File.Exists(modelNameOrPath)) return modelNameOrPath;
            return ModelFiles.FirstOrDefault(f => Path.GetFileName(f).Contains(modelNameOrPath, StringComparison.OrdinalIgnoreCase));
        }

        public void Dispose()
        {
            this.Factory?.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
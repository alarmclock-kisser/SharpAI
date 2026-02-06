using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using SharpAI.Core;
using SharpAI.Shared;

namespace SharpAI.Runtime
{
    public partial class OnnxService : IDisposable
    {
        private InferenceSession? _encoderSession;
        private InferenceSession? _decoderSession;

        public bool Initialized => this._encoderSession != null && this._decoderSession != null;

        // Liste der Basis-Verzeichnisse, in denen nach Modell-Unterordnern gesucht wird
        public List<string> SearchDirectories { get; set; } = new() { @"D:\Models\WhisperTranscription\" };

        // Liste der tatsächlich gefundenen, validen Modelle
        public List<WhisperModelInfo> AvailableModels { get; private set; } = new();

        // Das aktuell geladene Modell
        public WhisperModelInfo? CurrentModel { get; private set; }

        public bool IsInitialized => this._encoderSession != null && this._decoderSession != null;

        public OnnxService(string[]? additionalDirectories = null)
        {
            if (additionalDirectories != null)
            {
                this.SearchDirectories.AddRange(additionalDirectories);
            }

            // Sofortiger Scan nach verfügbaren Modellen
            this.DiscoverModels();
        }

        public void DiscoverModels()
        {
            this.AvailableModels.Clear();

            foreach (var baseDir in this.SearchDirectories)
            {
                if (!Directory.Exists(baseDir)) continue;

                var subOps = new EnumerationOptions { RecurseSubdirectories = false };
                var subDirectories = Directory.GetDirectories(baseDir, "*", subOps);

                foreach (var modelDir in subDirectories)
                {
                    var info = this.TryGetModelInfo(modelDir);
                    if (info != null)
                    {
                        this.AvailableModels.Add(info);
                        StaticLogger.Log($"Whisper model found: {info.Name}");
                    }
                }
            }
        }

        private WhisperModelInfo? TryGetModelInfo(string directory)
        {
            // Unterstütze mehrere Varianten der Dateinamen (.onnx, .onnx_data, mit/ohne .json oder _config)
            string? encoder = FindFirstExisting(directory, new[] { "encoder_model.onnx", "encoder_model.onnx_data" });
            string? decoder = FindFirstExisting(directory, new[] { "decoder_model_merged.onnx", "decoder_model_merged.onnx_data" });
            string? tokenizer = FindFirstExisting(directory, new[] { "tokenizer.json", "tokenizer_config.json", "tokenizer_config", "tokenizer" });
            string? preprocessor = FindFirstExisting(directory, new[] { "preprocessor_config.json", "preprocessor_config" });
            string? config = FindFirstExisting(directory, new[] { "config.json", "config" });
            string? generation = FindFirstExisting(directory, new[] { "generation_config.json", "generation_config" });

            // Prüfen, ob alle benötigten Dateien gefunden wurden
            if (encoder == null || decoder == null || tokenizer == null || preprocessor == null || config == null || generation == null)
                return null;

            return new WhisperModelInfo(
                name: Path.GetFileName(directory),
                rootPath: directory,
                configPath: config,
                decoderPath: decoder,
                encoderPath: encoder,
                generationConfigPath: generation,
                preprocessorConfigPath: preprocessor,
                tokenizerPath: tokenizer
            );
        }

        private static string? FindFirstExisting(string directory, string[] candidates)
        {
            foreach (var name in candidates)
            {
                var path = Path.Combine(directory, name);
                if (File.Exists(path)) return path;
            }

            // Fallback: suche nach Dateien, deren Name (ohne Pfad) mit einem Kandidaten beginnt (z.B. "tokenizer_config" ohne .json)
            var files = Directory.GetFiles(directory);
            foreach (var candidate in candidates)
            {
                var baseName = Path.GetFileNameWithoutExtension(candidate);
                var match = files.FirstOrDefault(f => Path.GetFileName(f).StartsWith(baseName, StringComparison.OrdinalIgnoreCase));
                if (match != null) return match;
            }

            return null;
        }

        public async Task<bool> InitializeAsync(WhisperModelInfo? model = null)
        {
            if (model == null || !Directory.Exists(model.RootPath) || !File.Exists(model.EncoderPath))
            {
                if (this.AvailableModels.Count >= 1)
                {
                    model = this.AvailableModels.First();
                }
                else
                {
                    await StaticLogger.LogAsync("No whisper model available.");
                    return false;
                }
            }

            try
            {
                // Falls bereits ein Modell geladen ist, Ressourcen frei machen
                this.Dispose();

                var options = new SessionOptions();
                options.AppendExecutionProvider_CPU();
                options.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;

                await Task.Run(() =>
                {
                    this._encoderSession = new InferenceSession(model.EncoderPath, options);
                    this._decoderSession = new InferenceSession(model.DecoderPath, options);
                });

                this.CurrentModel = model;
                StaticLogger.Log($"Whisper ONNX Model '{model.Name}' loaded successfully.");
                return true;
            }
            catch (Exception ex)
            {
                StaticLogger.Log($"Failed to initialize Whisper model: {model.Name}");
                StaticLogger.Log(ex);
                return false;
            }
        }

        // Überladung für Standard-Initialisierung (nimmt das erste gefundene Modell)
        public async Task<bool> InitializeAsync()
        {
            if (this.IsInitialized) return true;
            if (this.AvailableModels.Count == 0) this.DiscoverModels();
            if (this.AvailableModels.Count == 0)
            {
                StaticLogger.Log("No valid Whisper models found in SearchDirectories.");
                return false;
            }
            return await this.InitializeAsync(this.AvailableModels[0]);
        }

        public bool DeInitialize()
        {
            this._encoderSession?.Dispose();
            this._encoderSession = null;
            this._decoderSession?.Dispose();
            this._decoderSession = null;
            this.CurrentModel = null;

            if (!this.IsInitialized)
            {
                return true;
            }

            return false;
        }

        public void Dispose()
        {
            this._encoderSession?.Dispose();
            this._decoderSession?.Dispose();
            this._encoderSession = null;
            this._decoderSession = null;
            this.CurrentModel = null;
        }
    }
}
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

        public bool Initialized => _encoderSession != null && _decoderSession != null;

        // Liste der Basis-Verzeichnisse, in denen nach Modell-Unterordnern gesucht wird
        public List<string> SearchDirectories { get; set; } = new() { @"D:\Models\WhisperTranscription\" };

        // Liste der tatsächlich gefundenen, validen Modelle
        public List<WhisperModelInfo> AvailableModels { get; private set; } = new();

        // Das aktuell geladene Modell
        public WhisperModelInfo? CurrentModel { get; private set; }

        public bool IsInitialized => _encoderSession != null && _decoderSession != null;

        public OnnxService(string[]? additionalDirectories = null)
        {
            if (additionalDirectories != null)
            {
                this.SearchDirectories.AddRange(additionalDirectories);
            }

            // Sofortiger Scan nach verfügbaren Modellen
            DiscoverModels();
        }

        public void DiscoverModels()
        {
            this.AvailableModels.Clear();

            foreach (var baseDir in SearchDirectories)
            {
                if (!Directory.Exists(baseDir)) continue;

                var subOps = new EnumerationOptions { RecurseSubdirectories = false };
                var subDirectories = Directory.GetDirectories(baseDir, "*", subOps);

                foreach (var modelDir in subDirectories)
                {
                    var info = TryGetModelInfo(modelDir);
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
            string[] requiredFiles = {
                "encoder_model.onnx",
                "decoder_model_merged.onnx",
                "tokenizer.json",
                "preprocessor_config.json",
                "config.json",
                "generation_config.json"
            };

            // Prüfen, ob alle Dateien existieren
            if (!requiredFiles.All(f => File.Exists(Path.Combine(directory, f))))
                return null;

            return new WhisperModelInfo(
                name: Path.GetFileName(directory),
                rootPath: directory,
                configPath: Path.Combine(directory, "config.json"),
                decoderPath: Path.Combine(directory, "decoder_model_merged.onnx"),
                encoderPath: Path.Combine(directory, "encoder_model.onnx"),
                generationConfigPath: Path.Combine(directory, "generation_config.json"),
                preprocessorConfigPath: Path.Combine(directory, "preprocessor_config.json"),
                tokenizerPath: Path.Combine(directory, "tokenizer.json")
            );
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
                Dispose();

                var options = new SessionOptions();
                options.AppendExecutionProvider_CPU();
                options.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;

                await Task.Run(() =>
                {
                    _encoderSession = new InferenceSession(model.EncoderPath, options);
                    _decoderSession = new InferenceSession(model.DecoderPath, options);
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
            if (IsInitialized) return true;
            if (AvailableModels.Count == 0) DiscoverModels();
            if (AvailableModels.Count == 0)
            {
                StaticLogger.Log("No valid Whisper models found in SearchDirectories.");
                return false;
            }
            return await InitializeAsync(AvailableModels[0]);
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
            _encoderSession?.Dispose();
            _decoderSession?.Dispose();
            _encoderSession = null;
            _decoderSession = null;
            CurrentModel = null;
        }
    }
}
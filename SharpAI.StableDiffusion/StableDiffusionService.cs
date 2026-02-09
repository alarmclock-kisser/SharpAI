using Microsoft.ML.OnnxRuntime;
using SharpAI.Core;
using SharpAI.Shared;
using System;
using System.Collections.Generic;
using System.Text;

namespace SharpAI.StableDiffusion
{
    public partial class StableDiffusionService : IDisposable
    {
        private StableDiffusionModel? _config = null;
        private InferenceSession? _textEncoderSession = null;
        private InferenceSession? _unetSession = null;
        private InferenceSession? _vaeDecoderSession = null;

        public StableDiffusionModel? CurrentModel => this._config;
        public bool IsInitialized => this._textEncoderSession != null && this._unetSession != null && this._vaeDecoderSession != null;


        public List<string> ModelDirectories { get; set; } = [
            "D:/Models/StableDiffusion/SD_v1.5"
            ];
        public List<StableDiffusionModel> Models { get; private set; } = [];




        public StableDiffusionService(string[]? additionalDirectories = null)
        {
            this.GetStableDiffusionModels(additionalDirectories);
        }




        public List<StableDiffusionModel> GetStableDiffusionModels(string[]? additionalDirectories = null)
        {
            if (additionalDirectories != null)
            {
                this.ModelDirectories.AddRange(additionalDirectories);
            }

            // Verify each dir
            this.ModelDirectories = this.ModelDirectories.Where(dir => Directory.Exists(dir)).ToList();

            // Load models from each directory
            this.Models.Clear();
            foreach (var dir in this.ModelDirectories)
            {
                var model = new StableDiffusionModel(dir);
                try
                {
                    model.Validate();
                    this.Models.Add(model);
                }
                catch (Exception ex)
                {
                    StaticLogger.Log($"Error loading model from {dir}: {ex.Message}");
                    continue;
                }
            }

            return this.Models;
        }

        public void InitializeSessions(StableDiffusionModel? model = null, bool forceUnload = true)
        {
            model ??= this.Models.FirstOrDefault();
            if (model == null)
            {
                StaticLogger.Log("No valid Stable Diffusion model found.");
                return;
            }

            this._config = model; // WICHTIG: Hier die Config setzen!

            if (forceUnload && (this._textEncoderSession != null || this._unetSession != null || this._vaeDecoderSession != null))
            {
                this.DisposeSessions(); // Hilfsmethode statt ganzes Dispose
            }

            var options = new SessionOptions();
            options.AppendExecutionProvider_DML(0);
            options.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;

            // Nutze jetzt this._config statt model direkt für Konsistenz
            this._textEncoderSession = new InferenceSession(this._config.TextEncoderOnnxPath, options);
            this._unetSession = new InferenceSession(this._config.UnetOnnxPath, options);
            this._vaeDecoderSession = new InferenceSession(this._config.VaeDecoderOnnxPath, options);

            Task.Run(async () => {
                await this.LoadTokenizerAsync();
                await this.LoadSchedulerAsync();
            }).Wait();

            StaticLogger.Log("All sessions initialized successfully.");
        }

        public void DisposeSessions()
        {
            this._textEncoderSession?.Dispose(); this._textEncoderSession = null;
            this._unetSession?.Dispose(); this._unetSession = null;
            this._vaeDecoderSession?.Dispose(); this._vaeDecoderSession = null;
        }

        public void Dispose()
        {
            this._textEncoderSession?.Dispose();
            this._unetSession?.Dispose();
            this._vaeDecoderSession?.Dispose();
            GC.SuppressFinalize(this);
        }


    }
}

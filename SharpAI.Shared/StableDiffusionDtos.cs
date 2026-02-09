using System;
using System.Collections.Generic;
using System.Text;

namespace SharpAI.Shared
{
    public class StableDiffusionModel
    {
        public string ModelRootPath { get; set; }

        // Onnx model paths
        public string TextEncoderOnnxPath => Path.Combine(this.ModelRootPath, "text_encoder", "model.onnx");
        public string UnetOnnxPath => Path.Combine(this.ModelRootPath, "unet", "model.onnx");
        public string VaeDecoderOnnxPath => Path.Combine(this.ModelRootPath, "vae_decoder", "model.onnx");
        public string VaeEncoderOnnxPath => Path.Combine(this.ModelRootPath, "vae_encoder", "model.onnx");

        // Tokenizer paths
        public string TokenizerVocabPath => Path.Combine(this.ModelRootPath, "tokenizer", "vocab.json");
        public string TokenizerMergesPath => Path.Combine(this.ModelRootPath, "tokenizer", "merges.txt");

        // Scheduler config path
        public string SchedulerConfigPath => Path.Combine(this.ModelRootPath, "scheduler", "scheduler_config.json");




        public StableDiffusionModel(string rootPath)
        {
            if (!Directory.Exists(rootPath))
            {
                throw new DirectoryNotFoundException($"Das Verzeichnis {rootPath} wurde nicht gefunden.");
            }
            this.ModelRootPath = rootPath;
        }

        public void Validate()
        {
            var criticalFiles = new[]
            {
            this.TextEncoderOnnxPath, this.UnetOnnxPath, this.VaeDecoderOnnxPath,
            this.TokenizerVocabPath, this.TokenizerMergesPath
            };

            foreach (var file in criticalFiles)
            {
                if (!File.Exists(file))
                {
                    throw new FileNotFoundException($"Kritische Modell-Datei fehlt: {file}");
                }
            }
        }
    }


    public class StableDiffusionGenerationRequest
    {
        public string Prompt { get; set; } = string.Empty;
        public string NegativePrompt { get; set; } = string.Empty;
        public int Steps { get; set; } = 50;
        public float GuidanceScale { get; set; } = 6.1f;
        public long Seed { get; set; } = DateTime.Now.Ticks;


    }


}

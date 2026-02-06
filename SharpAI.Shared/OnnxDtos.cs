using System;
using System.Collections.Generic;
using System.Text;

namespace SharpAI.Shared
{
    public class WhisperModelInfo
    {
        public string Name { get; set; }
        public string RootPath { get; set; }

        public string ModelName => Path.GetFileName(this.RootPath) ?? this.Name;

        public double SizeInMb { get; set; }
        public string EncoderPath { get; set; }
        public string DecoderPath { get; set; }
        public string TokenizerPath { get; set; }
        public string PreprocessorConfigPath { get; set; }
        public string ConfigPath { get; set; }
        public string GenerationConfigPath { get; set; }

        public WhisperModelInfo(string name, string rootPath, string encoderPath, string decoderPath, string tokenizerPath, string preprocessorConfigPath, string configPath, string generationConfigPath)
        {
            this.Name = name;
            this.RootPath = rootPath;
            this.EncoderPath = encoderPath;
            this.DecoderPath = decoderPath;
            this.TokenizerPath = tokenizerPath;
            this.PreprocessorConfigPath = preprocessorConfigPath;
            this.ConfigPath = configPath;
            this.GenerationConfigPath = generationConfigPath;

            if (!File.Exists(this.EncoderPath) || !File.Exists(this.DecoderPath))
            {
                return;
            }

            this.SizeInMb = (new FileInfo(this.EncoderPath).Length + new FileInfo(this.DecoderPath).Length) / (1024.0 * 1024.0);
        }
    }

}

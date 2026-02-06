using System;
using System.Collections.Generic;
using System.Text;

namespace SharpAI.Shared
{
    public class Appsettings
    {
        public List<string> LlamaModelDirectories { get; set; } = [];
        public List<string> WhisperModelDirectories { get; set; } = [];

        public string? DefaultLlamaModel { get; set; }
        public string? DefaultWhistperModel { get; set; }

        public string PreferredLlamaBackend { get; set; } = "CPU";

        public List<string> RessourceImagePaths { get; set; } = [];
        public List<string> RessourceAudioPaths { get; set; } = [];

        public string? CustomAudioExportDirectory { get; set; }

        public string SystemPrompt { get; set; } = string.Empty;
        public string? DefaultContext { get; set; }

        public int MaxContextTokens { get; set; } = 4096;
        public int MaxResponseTokens { get; set; } = 1024;
        public double Temperature { get; set; } = 0.7;
        public double TopP { get; set; } = 0.9;

        public bool AutoLoadLlama { get; set; } = false;


    }
}

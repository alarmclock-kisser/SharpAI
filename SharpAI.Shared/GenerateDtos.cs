using System;
using System.Collections.Generic;
using System.Text;

namespace SharpAI.Shared
{
    public class LlamaGenerationRequest
    {
        public string Prompt { get; set; } = string.Empty;
        public int MaxTokens { get; set; } = 512;
        public float Temperature { get; set; } = 0.8f;
        public float TopP { get; set; } = 0.95f;
        public bool Stream { get; set; } = true;
        public List<string> Base64Images { get; set; } = new();

        public bool UseSystemPrompt { get; set; } = true;
        public bool Isolated { get; set; } = false;



    }

}

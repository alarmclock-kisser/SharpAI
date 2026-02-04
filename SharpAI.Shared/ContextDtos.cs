using System;
using System.Collections.Generic;
using System.Text;

namespace SharpAI.Shared
{
    public class LlamaContextData
    {
        public string FilePath { get; set; } = string.Empty;

        public List<LlamaContextMessage> Messages { get; set; } = new();
    }


    public class LlamaContextMessage
    {
        public string Role { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;

        public string[] Images { get; set; } = [];

        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public LlamaContextStats Stats { get; set; } = new();
    }



    public class LlamaContextStats
    {
        public int TokensUsed { get; set; }
        public double SecondsElapsed { get; set; }
        public double TokensPerSecond => this.SecondsElapsed > 0 ? this.TokensUsed / this.SecondsElapsed : 0;
    }
}

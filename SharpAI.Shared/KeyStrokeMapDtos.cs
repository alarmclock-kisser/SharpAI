using System;
using System.Collections.Generic;
using System.Text;

namespace SharpAI.Shared
{
    public class KeyStrokeMapDto
    {
        public List<int> Timings { get; set; } = [];

        public List<string> Keys { get; set; } = [];



        public KeyStrokeMapDto(Dictionary<long, string> keyStrokes)
        {
            this.Timings = keyStrokes.Keys.Select(k => (int)k).ToList();
            this.Keys = keyStrokes.Values.ToList();
        }


        public override string ToString()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("KeyStrokeMapDto:");
            for (int i = 0; i < Timings.Count; i++)
            {
                sb.AppendLine($"  Timing: {Timings[i]} ms, Key: {Keys[i]}");
            }
            return sb.ToString();
        }


    }
}

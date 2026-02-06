using System;
using System.Collections.Generic;
using System.Text;

namespace SharpAI.Shared
{
    public class AudioObjData
    {
        public Guid Id { get; set; }

        public int SampleRate { get; set; }
        public int Channels { get; set; }
        public int BitDepth { get; set; }
        public double DurationInSeconds { get; set; }

        public string Data { get; set; } = string.Empty;
        public int Length => this.Data.Length;


        public AudioObjData(Guid id, int sampleRate, int channels, int bitDepth, TimeSpan durationTimeSpan, string serializedData)
        {
            this.Id = id;
            this.SampleRate = sampleRate;
            this.Channels = channels;
            this.BitDepth = bitDepth;
            this.DurationInSeconds = durationTimeSpan.TotalSeconds;
            this.Data = serializedData;
        }


    }
}

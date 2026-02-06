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
        public int Length => Data.Length;


        public AudioObjData(Guid id, int sampleRate, int channels, int bitDepth, TimeSpan durationTimeSpan, string serializedData)
        {
            Id = id;
            SampleRate = sampleRate;
            Channels = channels;
            BitDepth = bitDepth;
            DurationInSeconds = durationTimeSpan.TotalSeconds;
            Data = serializedData;
        }


    }
}

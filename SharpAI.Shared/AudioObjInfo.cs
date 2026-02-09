using System;
using System.Collections.Generic;
using System.Text;

namespace SharpAI.Shared
{
    public class AudioObjInfo
    {
        public Guid Id { get; set; }
        public DateTime CreatedAt { get; set; }
        public string FilePath { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;

        public int SampleRate { get; set; }
        public int Channels { get; set; }
        public string ChannelString => this.Channels == 1 ? "Mono" : this.Channels == 2 ? "Stereo" : $"{this.Channels} Channels";

        public double DurationInSeconds { get; set; }

        public ImageObjData? WaveformPreview { get; set; }



        public AudioObjInfo(Guid id, DateTime createdAt, string filePath, string name, TimeSpan durationTimeSpan, int sampleRate = 0, int channels = 0)
        {
            this.Id = id;
            this.CreatedAt = createdAt;
            this.FilePath = filePath;
            this.Name = string.IsNullOrEmpty(name) ? id.ToString() : name;
            this.DurationInSeconds = durationTimeSpan.TotalSeconds;
            this.SampleRate = sampleRate;
            this.Channels = channels;
        }
    }
}

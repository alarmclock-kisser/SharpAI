using System;
using System.Collections.Generic;
using System.Text;

namespace SharpAI.Shared
{
    public class AudioObjInfo
    {
        public Guid Id {  get; set; }
        public DateTime CreatedAt { get; set; }
        public string FilePath { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;

        public double DurationInSeconds { get; set; }

        public ImageObjData? WaveformPreview { get; set; }



        public AudioObjInfo(Guid id, DateTime createdAt, string filePath, string name, TimeSpan durationTimeSpan)
        {
            Id = id;
            CreatedAt = createdAt;
            FilePath = filePath;
            Name = string.IsNullOrEmpty(name) ? id.ToString() : name;
            DurationInSeconds = durationTimeSpan.TotalSeconds;
        }
    }
}

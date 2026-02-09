using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace SharpAI.Shared
{
    public class ImageObjInfo
    {
        public Guid Id { get; set; }
        public string FilePath { get; set; } = string.Empty;
        public int Width { get; set; }
        public int Height { get; set; }
        public int Channels { get; set; }
        public int BitDepth { get; set; }
        public double SizeInKb { get; set; }
        public string? ThumbnailBase64 { get; set; }




        [JsonConstructor]
        public ImageObjInfo()
        {

        }


        public ImageObjInfo(Guid id, string filePath, int width, int height, int channels = 4, int bitDepth = 8, double sizeInKb = 0, string? thumbnailBase64 = null)
        {
            this.Id = id;
            this.FilePath = filePath;
            this.Width = width;
            this.Height = height;
            this.Channels = channels;
            this.BitDepth = bitDepth;
            this.SizeInKb = sizeInKb;
            this.ThumbnailBase64 = thumbnailBase64;
        }


    }
}

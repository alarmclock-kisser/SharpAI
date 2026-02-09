using System;
using System.Collections.Generic;
using System.Text;

namespace SharpAI.Shared
{
    public class LmStudioModel
    {
        public string Name { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public long Size { get; set; }
        public DateTime LastModified { get; set; }

        public double ParamsB { get; set; }
        public string Quantization { get; set; } = string.Empty;
        public string QuantizationType { get; set; } = string.Empty;
    }

    public class LmStudioModelLoadRequest
    {
        public LmStudioModel Model { get; set; }



    }


    public enum LmStudioBackend
    {
        CPU,
        CUDA,
        OpenVINO,
        DirectML,
        OpenCL
    }

}

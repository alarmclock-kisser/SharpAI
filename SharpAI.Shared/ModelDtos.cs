using System;
using System.Collections.Generic;
using System.Text;

namespace SharpAI.Shared
{
    public class LlamaModelFile
    {
        public string FilePath { get; set; } = string.Empty;
        public string ModelName { get; set; } = string.Empty;
        public double FileSizeMb { get; set; }
        public DateTime LastModified { get; set; }

        public LlamaModelFile(string filePath = "")
        {
            this.FilePath = filePath;
            this.ModelName = System.IO.Path.GetFileNameWithoutExtension(filePath);
            new System.IO.FileInfo(filePath);
            this.FileSizeMb = Math.Round((new System.IO.FileInfo(filePath).Length / 1024.0) / 1024.0, 2);
            this.LastModified = System.IO.File.GetLastWriteTime(filePath);
        }
    }

    public class LlamaModelLoadRequest
    {
        public LlamaModelFile ModelFile { get; set; }
        public int ContextSize { get; set; }
        public LlamaBackend Backend { get; set; } = LlamaBackend.CPU;


        public LlamaModelLoadRequest(LlamaModelFile modelFile, int contextSize = 1024, LlamaBackend backend = LlamaBackend.CPU)
        {
            this.ModelFile = modelFile;
            this.ContextSize = contextSize;
            this.Backend = backend;
        }
    }



    public enum LlamaBackend
    {
        CPU,
        CUDA,
        OpenCL
    }
}

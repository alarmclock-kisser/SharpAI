using SharpAI.Shared;
using System;
using System.Collections.Generic;
using System.Text;

namespace SharpAI.Runtime
{
    public partial class LlamaService
    {
        public List<string> ModelDirectories { get; private set; } = [
            "%APPDATA%\\.lmstudio\\models\\",
            "D:\\Models\\"
            ];

        public List<LlamaModelFile> ModelFiles { get; private set; } = [];





        public LlamaService(string[]? additionalDirectories = null)
        {
            this.LoadModelFiles(additionalDirectories);



        }




        public string[] LoadModelFiles(string[]? additionalDirectories = null)
        {
            if (additionalDirectories != null)
            {
                this.ModelDirectories.AddRange(additionalDirectories);
            }

            // Verify each directory exists
            this.ModelDirectories = this.ModelDirectories.Where(dir => Directory.Exists(Environment.ExpandEnvironmentVariables(dir))).ToList();

            // Get ModelFile dtos
            this.ModelFiles = this.ModelDirectories.SelectMany(dir => Directory.GetFiles(Environment.ExpandEnvironmentVariables(dir), "*.gguf", SearchOption.AllDirectories))
                .Select(filePath => new LlamaModelFile(filePath))
                .ToList();

            return this.ModelFiles.Select(mf => Path.GetFullPath(mf.FilePath)).ToArray();
        }


        public LlamaModelFile? GetModelFileByName(string modelName, bool fuzzy = false)
        {
            var match = this.ModelFiles.FirstOrDefault(mf => string.Equals(mf.ModelName, modelName, StringComparison.OrdinalIgnoreCase));

            if (match == null && fuzzy)
            {
                match = this.ModelFiles.FirstOrDefault(mf => mf.ModelName.IndexOf(modelName, StringComparison.OrdinalIgnoreCase) >= 0);
            }

            return match;
        }

        public LlamaModelFile? GetModelFileByPath(string modelPath)
        {
            return this.ModelFiles.FirstOrDefault(mf => string.Equals(Path.GetFullPath(mf.FilePath), Path.GetFullPath(modelPath), StringComparison.OrdinalIgnoreCase));
        }

        public LlamaModelFile? GetModelByNameOrPath(string modelNameOrPath, bool fuzzy = false)
        {
            if (Path.IsPathRooted(modelNameOrPath) || modelNameOrPath.Contains(Path.DirectorySeparatorChar))
            {
                return this.GetModelFileByPath(modelNameOrPath);
            }
            else
            {
                return this.GetModelFileByName(modelNameOrPath, fuzzy);
            }
        }

    }
}

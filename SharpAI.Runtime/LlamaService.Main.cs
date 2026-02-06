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





        public LlamaService(string[]? additionalDirectories = null, string? defaultModel = null, string preferredBackend = "CPU", int maxContextSize = 2048, string? defaultContext = null, string? systemPrompt = null)
        {
            this.LoadModelFiles(additionalDirectories);
            if (!string.IsNullOrEmpty(systemPrompt))
            {
                this.SystemPrompt = systemPrompt;
            }

            if (!string.IsNullOrEmpty(defaultContext))
            {
                if (defaultContext.StartsWith("/recent"))
                {
                    var mostRecentContext = this.GetAllContextsAsync().GetAwaiter().GetResult()?.OrderByDescending(c => c.LatestActivityDate).FirstOrDefault();
                    if (mostRecentContext != null)
                    {
                        defaultContext = mostRecentContext.FilePath;
                    }
                }

                this.CurrentContext = this.LoadContextAsync(defaultContext).GetAwaiter().GetResult();
            }

            if (!string.IsNullOrEmpty(defaultModel))
            {
                var modelFile = this.GetModelByNameOrPath(defaultModel, fuzzy: true);
                if (modelFile != null)
                {
                    LlamaBackend backend = preferredBackend switch
                    {
                        "CPU" => LlamaBackend.CPU,
                        "CUDA" => LlamaBackend.CUDA,
                        // "DirectML" => LlamaBackend.DirectML, // not installed
                        // "OpenVINO" => LlamaBackend.OpenVINO, // not installed
                        _ => LlamaBackend.CPU
                    };

                    LlamaModelLoadRequest request = new LlamaModelLoadRequest(modelFile, maxContextSize, backend);
                    var loadedModel = this.LoadModelAsync(request).GetAwaiter().GetResult();
                }
            }
        }

        public LlamaService(Appsettings appsettings)
        {
            
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

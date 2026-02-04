using LLama;
using LLama.Common;
using SharpAI.Core;
using SharpAI.Shared;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace SharpAI.Runtime
{
    public partial class LlamaService
    {
        private LlamaModelFile? loadedModelFile;
        private LlamaModelLoadRequest? loadedModelRequest;
        private LLamaWeights? llamaWeights;
        private LLamaContext? llamaContext;
        private InteractiveExecutor? llamaExecutor;
        private LlamaContextData? primedContext;
        private int primedMessageCount;

        public LlamaModelFile? LoadedModelFile => this.loadedModelFile;
        public bool IsModelLoaded => this.LoadedModelFile != null;





        public async Task<LlamaModelFile?> LoadModelAsync(LlamaModelLoadRequest loadRequest, bool fuzzyMatch = false, IProgress<double>? progress = null, CancellationToken ct = default)
        {
            // If a model is already loaded, unload it first
            if (loadRequest == null || loadRequest.ModelFile == null)
            {
                StaticLogger.Log("LoadModelAsync called with an invalid request.");
                return null;
            }

            if (!File.Exists(loadRequest.ModelFile.FilePath))
            {
                var mf = fuzzyMatch ? this.GetModelByNameOrPath(loadRequest.ModelFile.ModelName, fuzzyMatch) : null;
                if (mf != null)
                {
                    loadRequest.ModelFile = mf;
                    StaticLogger.Log($"Fuzzy matched model file: {mf.FilePath}");
                }
                else
                {
                    StaticLogger.Log($"Model file not found: {loadRequest.ModelFile.FilePath}");
                    return null;
                }

                if (!File.Exists(loadRequest.ModelFile.FilePath))
                {
                    StaticLogger.Log($"Model file still not found after fuzzy matching: {loadRequest.ModelFile.FilePath}");
                    return null;
                }
            }

            ct.ThrowIfCancellationRequested();

            if (this.IsModelLoaded)
            {
                await this.UnloadModelAsync(ct).ConfigureAwait(false);
            }

            ct.ThrowIfCancellationRequested();

            progress?.Report(0);
            await Task.Yield();

            var backend = loadRequest.Backend;
            if (backend == LlamaBackend.OpenCL)
            {
                StaticLogger.Log("OpenCL backend is not supported by LLamaSharp. Falling back to CPU.");
                backend = LlamaBackend.CPU;
            }

            var modelParams = new ModelParams(loadRequest.ModelFile.FilePath)
            {
                ContextSize = (uint)Math.Max(128, loadRequest.ContextSize),
                GpuLayerCount = backend == LlamaBackend.CUDA ? -1 : 0
            };

            progress?.Report(0.25);
            await Task.Yield();

            this.llamaWeights = LLamaWeights.LoadFromFile(modelParams);
            this.llamaContext = this.llamaWeights.CreateContext(modelParams);
            this.llamaExecutor = new InteractiveExecutor(this.llamaContext);
            this.primedContext = null;
            this.primedMessageCount = 0;

            this.loadedModelFile = loadRequest.ModelFile;
            this.loadedModelRequest = loadRequest;

            progress?.Report(1);

            // Use StaticLogger to log the loading process or errors
            StaticLogger.Log($"Loaded model '{loadRequest.ModelFile.ModelName}' with context size {modelParams.ContextSize} using {backend}.");
            return this.loadedModelFile;
        }

        public async Task<bool?> UnloadModelAsync(CancellationToken ct = default)
        {
            // If no model is loaded, return null
            if (!this.IsModelLoaded)
            {
                StaticLogger.Log("UnloadModelAsync called but no model is loaded.");
                return null;
            }

            ct.ThrowIfCancellationRequested();

            await Task.Yield();

            // Unload the current model, return true if successful
            var unloadedModel = this.loadedModelFile;
            this.loadedModelFile = null;
            this.loadedModelRequest = null;

            this.llamaExecutor = null;
            this.llamaContext?.Dispose();
            this.llamaContext = null;
            this.llamaWeights?.Dispose();
            this.llamaWeights = null;
            this.primedContext = null;
            this.primedMessageCount = 0;

            // Use StaticLogger to log the unloading process or errors
            StaticLogger.Log($"Unloaded model '{unloadedModel?.ModelName}'.");
            return true;
        }

        private bool TryResetExecutor()
        {
            if (this.llamaWeights == null || this.loadedModelRequest?.ModelFile == null)
            {
                return false;
            }

            var backend = this.loadedModelRequest.Backend;
            if (backend == LlamaBackend.OpenCL)
            {
                backend = LlamaBackend.CPU;
            }

            var modelParams = new ModelParams(this.loadedModelRequest.ModelFile.FilePath)
            {
                ContextSize = (uint)Math.Max(128, this.loadedModelRequest.ContextSize),
                GpuLayerCount = backend == LlamaBackend.CUDA ? -1 : 0
            };

            this.llamaContext?.Dispose();
            this.llamaContext = this.llamaWeights.CreateContext(modelParams);
            this.llamaExecutor = new InteractiveExecutor(this.llamaContext);
            this.primedContext = null;
            this.primedMessageCount = 0;
            return true;
        }




    }
}

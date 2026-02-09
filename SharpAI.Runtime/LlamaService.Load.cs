using LLama;
using LLama.Abstractions;
using LLama.Common;
using LLama.Native;
using SharpAI.Core;
using SharpAI.Shared;
using System;
using System.Collections.Generic;
using System.Linq;
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
        private LLavaWeights? llamaVlWeights;
        private LLamaContext? llamaContext;
        private ILLamaExecutor? llamaExecutor;
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

            // If the selected LlamaModelFile already has an associated MMProjFilePath (discovered
            // during model enumeration), use it unless an explicit MMProjPath was provided in the request.
            if (string.IsNullOrEmpty(loadRequest.MMProjPath) && !string.IsNullOrEmpty(loadRequest.ModelFile?.MmprojFilePath) && loadRequest.TryLoadMultimodal)
            {
                var mmproj = loadRequest.ModelFile?.MmprojFilePath;
                if (File.Exists(mmproj))
                {
                    loadRequest.MMProjPath = mmproj;
                    StaticLogger.Log($"Using associated MMProj from model listing: {mmproj}");
                }
            }

            // If multimodal was requested and no projector path was provided,
            // try to discover a suitable .mmproj file in the same directory as the model.
            if (loadRequest.TryLoadMultimodal && string.IsNullOrEmpty(loadRequest.MMProjPath))
            {
                try
                {
                    var modelDir = Path.GetDirectoryName(loadRequest?.ModelFile?.FilePath) ?? string.Empty;
                    StaticLogger.Log($"tryMultimodal requested. Searching for .mmproj in '{modelDir}'...");

                    // Prefer a projector with the same base name as the model file (e.g. model.gguf -> model.mmproj)
                    var sameBase = Path.Combine(modelDir, Path.GetFileNameWithoutExtension(loadRequest?.ModelFile?.FilePath) + ".mmproj");
                    if (File.Exists(sameBase))
                    {
                        loadRequest?.MMProjPath = sameBase;
                        StaticLogger.Log($"Auto-detected matching mmproj next to model: {sameBase}");
                    }
                    else
                    {
                        var mmprojCandidates = Directory.GetFiles(modelDir, "*.mmproj");
                        if (mmprojCandidates.Length == 1)
                        {
                            loadRequest?.MMProjPath = mmprojCandidates[0];
                            StaticLogger.Log($"Auto-detected single mmproj in model directory: {loadRequest?.MMProjPath}");
                        }
                        else if (mmprojCandidates.Length > 1)
                        {
                            // Try to fuzzy match by base name
                            var baseName = Path.GetFileNameWithoutExtension(loadRequest?.ModelFile?.FilePath);
                            var tagged = mmprojCandidates.FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).IndexOf(baseName ?? "xyz", StringComparison.OrdinalIgnoreCase) >= 0);
                            if (!string.IsNullOrEmpty(tagged))
                            {
                                loadRequest?.MMProjPath = tagged;
                                StaticLogger.Log($"Auto-detected mmproj by name match: {tagged}");
                            }
                            else
                            {
                                StaticLogger.Log($"Multiple mmproj files found in '{modelDir}', not auto-selecting. Candidates: {string.Join(", ", mmprojCandidates)}");
                            }
                        }
                        else
                        {
                            StaticLogger.Log($"No mmproj files found in '{modelDir}'.");
                        }
                    }
                }
                catch (Exception ex)
                {
                    StaticLogger.Log("Error while searching for mmproj: ");
                    StaticLogger.Log(ex);
                }
            }

            this.llamaWeights = LLamaWeights.LoadFromFile(modelParams);
            if (!string.IsNullOrEmpty(loadRequest?.MMProjPath) && File.Exists(loadRequest.MMProjPath) && loadRequest.TryLoadMultimodal)
            {
                try
                {
                    StaticLogger.Log($"Loading Vision Projector: {loadRequest.MMProjPath}");
                    StaticLogger.Log("Note: LLamaSharp 0.24 uses the LLaVA/CLIP pipeline. Only LLaVA-compatible mmproj files are supported (e.g. LLaVA 1.5/1.6, BakLLaVA). Gemma 3 (SigLIP) and Qwen 2.5 VL require the newer mtmd API and are NOT supported for vision.");
                    this.llamaVlWeights = LLavaWeights.LoadFromFile(loadRequest.MMProjPath);
                    StaticLogger.Log("Vision Projector loaded successfully.");
                }
                catch (Exception ex)
                {
                    StaticLogger.Log($"Failed to load Vision Projector — model will be loaded in text-only mode.");
                    StaticLogger.Log($"This typically means the mmproj file is not compatible with the LLaVA/CLIP pipeline (e.g. Gemma 3 SigLIP, Qwen 2.5 VL).");
                    StaticLogger.Log(ex);
                    this.llamaVlWeights?.Dispose();
                    this.llamaVlWeights = null;
                }
            }
            this.llamaContext = this.llamaWeights.CreateContext(modelParams);
            if (this.llamaVlWeights != null)
            {
                // Übergabe von Context UND Projector (mmproj)
                this.llamaExecutor = new InteractiveExecutor(this.llamaContext, this.llamaVlWeights);
            }
            else
            {
                this.llamaExecutor = new InteractiveExecutor(this.llamaContext);
            }
            this.primedContext = null;
            this.primedMessageCount = 0;

            this.loadedModelFile = loadRequest?.ModelFile;
            this.loadedModelRequest = loadRequest;

            progress?.Report(1);

            // Use StaticLogger to log the loading process or errors
            StaticLogger.Log($"Loaded model '{loadRequest?.ModelFile?.ModelName}' with context size {modelParams.ContextSize} using {backend}.");
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
            this.llamaVlWeights?.Dispose();
            this.llamaVlWeights = null;
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
            if (this.llamaVlWeights != null)
            {
                this.llamaExecutor = new InteractiveExecutor(this.llamaContext, this.llamaVlWeights);
            }
            else
            {
                this.llamaExecutor = new InteractiveExecutor(this.llamaContext);
            }
            this.primedContext = null;
            this.primedMessageCount = 0;
            return true;
        }




    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.Tokenizers;
using SharpAI.Core;
using SharpAI.Shared;

namespace SharpAI.Runtime
{
    public partial class OnnxService : IDisposable
    {
        private InferenceSession? _encoderSession;
        private InferenceSession? _decoderSession;
        private Tokenizer? _tokenizer;
        // Cache shapes for decoder KV-cache inputs (determined on initialization)
        private Dictionary<string, int[]>? _decoderCacheShapes;

        public bool Initialized => this._encoderSession != null && this._decoderSession != null;

        // Liste der Basis-Verzeichnisse, in denen nach Modell-Unterordnern gesucht wird
        public List<string> SearchDirectories { get; set; } = new() { @"D:\Models\WhisperTranscription\" };

        // Liste der tatsächlich gefundenen, validen Modelle
        public List<WhisperModelInfo> AvailableModels { get; private set; } = new();

        // Das aktuell geladene Modell
        public WhisperModelInfo? CurrentModel { get; private set; }

        public WhisperTokenMap? TokenMap { get; private set; }


        public bool IsInitialized => this._encoderSession != null && this._decoderSession != null;

        public OnnxService(string[]? additionalDirectories = null)
        {
            if (additionalDirectories != null)
            {
                this.SearchDirectories.AddRange(additionalDirectories);
            }

            // Sofortiger Scan nach verfügbaren Modellen
            this.DiscoverModels();
        }

        public void DiscoverModels()
        {
            this.AvailableModels.Clear();

            foreach (var baseDir in this.SearchDirectories)
            {
                if (!Directory.Exists(baseDir))
                {
                    continue;
                }

                var subOps = new EnumerationOptions { RecurseSubdirectories = false };
                var subDirectories = Directory.GetDirectories(baseDir, "*", subOps);

                foreach (var modelDir in subDirectories)
                {
                    var info = this.TryGetModelInfo(modelDir);
                    if (info != null)
                    {
                        this.AvailableModels.Add(info);
                        StaticLogger.Log($"Whisper model found: {info.Name}");
                    }
                }
            }
        }

        private WhisperModelInfo? TryGetModelInfo(string directory)
        {
            // Unterstütze mehrere Varianten der Dateinamen (.onnx, .onnx_data, mit/ohne .json oder _config)
            string? encoder = FindFirstExisting(directory, new[] { "encoder_model.onnx", "encoder_model.onnx_data" });
            string? decoder = FindFirstExisting(directory, new[] { "decoder_model_merged.onnx", "decoder_model_merged.onnx_data" });
            string? tokenizer = FindFirstExisting(directory, new[] { "tokenizer.json", "tokenizer_config.json", "tokenizer_config", "tokenizer" });
            string? preprocessor = FindFirstExisting(directory, new[] { "preprocessor_config.json", "preprocessor_config" });
            string? config = FindFirstExisting(directory, new[] { "config.json", "config" });
            string? generation = FindFirstExisting(directory, new[] { "generation_config.json", "generation_config" });

            // Prüfen, ob alle benötigten Dateien gefunden wurden
            if (encoder == null || decoder == null || tokenizer == null || preprocessor == null || config == null || generation == null)
            {
                return null;
            }

            return new WhisperModelInfo(
                name: Path.GetFileName(directory),
                rootPath: directory,
                configPath: config,
                decoderPath: decoder,
                encoderPath: encoder,
                generationConfigPath: generation,
                preprocessorConfigPath: preprocessor,
                tokenizerPath: tokenizer
            );
        }

        private static string? FindFirstExisting(string directory, string[] candidates)
        {
            foreach (var name in candidates)
            {
                var path = Path.Combine(directory, name);
                if (File.Exists(path))
                {
                    return path;
                }
            }

            // Fallback: suche nach Dateien, deren Name (ohne Pfad) mit einem Kandidaten beginnt
            var files = Directory.GetFiles(directory);
            foreach (var candidate in candidates)
            {
                var baseName = Path.GetFileNameWithoutExtension(candidate);
                var match = files.FirstOrDefault(f => Path.GetFileName(f).StartsWith(baseName, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                {
                    return match;
                }
            }

            return null;
        }

        public async Task<bool> InitializeAsync(WhisperModelInfo? model = null, int useCudaDevice = -1)
        {
            if (model == null || !Directory.Exists(model.RootPath) || !File.Exists(model.EncoderPath))
            {
                if (this.AvailableModels.Count >= 1)
                {
                    model = this.AvailableModels.First();
                }
                else
                {
                    await StaticLogger.LogAsync("No whisper model available.");
                    return false;
                }
            }

            try
            {
                this.Dispose();

                var options = new SessionOptions();
                if (useCudaDevice >= 0)
                {
                    try
                    {
                        options.AppendExecutionProvider_CUDA(useCudaDevice);
                        StaticLogger.Log("CUDA Execution Provider enabled.");
                    }
                    catch (Exception ex)
                    {
                        await StaticLogger.LogAsync($"CUDA initialization failed: {ex.Message}");
                        await StaticLogger.LogAsync("CUDA not available. Falling back to CPU.");
                        options.AppendExecutionProvider_CPU();
                    }
                }

                else
                {
                    await StaticLogger.LogAsync("Using ONNX on CPU.");
                    options.AppendExecutionProvider_CPU();
                }
                options.GraphOptimizationLevel = useCudaDevice >= 0 ? GraphOptimizationLevel.ORT_ENABLE_EXTENDED : GraphOptimizationLevel.ORT_ENABLE_ALL;

                await Task.Run(() =>
                {
                    // 0. Configure feature extractor from model's preprocessor config (if available)
                    try
                    {
                        WhisperFeatureExtractor.ConfigureFromPreprocessor(model.PreprocessorConfigPath);
                        StaticLogger.Log("WhisperFeatureExtractor configured from preprocessor config.");
                    }
                    catch (Exception cfgEx)
                    {
                        StaticLogger.Log($"Warning: Failed to configure WhisperFeatureExtractor from preprocessor: {cfgEx.Message}");
                        // Weiter mit Defaults
                    }

                    // 1. Sessions laden
                    this._encoderSession = new InferenceSession(model.EncoderPath, options);
                    this._decoderSession = new InferenceSession(model.DecoderPath, options);

                    // Determine decoder KV-cache shapes once during initialization
                    try
                    {
                        var cacheInputNames = this._decoderSession.InputMetadata.Keys.Where(k => k.StartsWith("past_key_values")).ToList();
                        var shapes = new Dictionary<string, int[]>();

                        foreach (var name in cacheInputNames)
                        {
                            int batch = 1;
                            int numHeads = -1;
                            int headDim = -1;

                            if (this._decoderSession.InputMetadata.TryGetValue(name, out var im) && im.Dimensions != null && im.Dimensions.Length >= 4)
                            {
                                batch = im.Dimensions[0] > 0 ? im.Dimensions[0] : batch;
                                numHeads = im.Dimensions[1] > 0 ? im.Dimensions[1] : numHeads;
                                headDim = im.Dimensions[3] > 0 ? im.Dimensions[3] : headDim;
                            }

                            var presentName = name.Replace("past_key_values", "present");
                            if (this._decoderSession.OutputMetadata != null && this._decoderSession.OutputMetadata.TryGetValue(presentName, out var om) && om.Dimensions != null && om.Dimensions.Length >= 4)
                            {
                                numHeads = numHeads > 0 ? numHeads : (om.Dimensions[1] > 0 ? om.Dimensions[1] : numHeads);
                                headDim = headDim > 0 ? headDim : (om.Dimensions[3] > 0 ? om.Dimensions[3] : headDim);
                                batch = batch > 0 ? batch : (om.Dimensions[0] > 0 ? om.Dimensions[0] : batch);
                            }

                            if (numHeads > 0 && headDim > 0)
                            {
                                shapes[name] = new[] { batch, numHeads, 0, headDim };
                            }
                            else
                            {
                                // Leave placeholder; will be inferred on first decoder run
                                shapes[name] = new[] { batch, -1, 0, -1 };
                                StaticLogger.Log($"Decoder cache shape for '{name}' could not be fully determined at initialize time. Will infer on first run.");
                            }
                        }

                        this._decoderCacheShapes = shapes;
                    }
                    catch (Exception ex)
                    {
                        StaticLogger.Log($"Warning: Failed to precompute decoder cache shapes: {ex.Message}");
                        this._decoderCacheShapes = null;
                    }

                    // --- Synchronisiere FeatureExtractor mit Modell-Shape ---
                    if (this._encoderSession.InputMetadata.TryGetValue("input_features", out var meta) && meta.Dimensions != null && meta.Dimensions.Length >= 3)
                    {
                        int modelNMels = meta.Dimensions[1];
                        int modelNFrames = meta.Dimensions[2];

                        // Nur setzen wenn Model konkrete positive Werte liefert (nicht -1 oder 0)
                        WhisperFeatureExtractor.SetParameters(
                            nMels: modelNMels > 0 ? modelNMels : (int?) null,
                            nFrames: modelNFrames > 0 ? modelNFrames : (int?) null
                        );

                        StaticLogger.Log($"WhisperFeatureExtractor parameters set from encoder metadata: n_mels={WhisperFeatureExtractor.NMels}, n_frames={WhisperFeatureExtractor.NFrames}");
                    }

                    // 2. Tokenizer korrekt laden (Fix für "Tokenizer is abstract")
                    if (File.Exists(model.TokenizerPath))
                    {
                        if (string.IsNullOrEmpty(model.VocabPath) || string.IsNullOrEmpty(model.MergesPath))
                        {
                            throw new FileNotFoundException("Required tokenizer files are missing.");
                        }

                        // Die Methode Create parst tokenizer.json automatisch
                        this._tokenizer = BpeTokenizer.Create(model.VocabPath, model.MergesPath);

                        // 3. IDs dynamisch auslesen (aus generation_config.json + vocab.json)
                        this.TokenMap = new WhisperTokenMap(this._tokenizer, model.GenerationConfigPath, model.VocabPath);
                    }
                });

                this.CurrentModel = model;
                StaticLogger.Log($"Whisper Model '{model.Name}' initialized. SOT Token ID: {this.TokenMap?.Sot}");
                return true;
            }
            catch (Exception ex)
            {
                StaticLogger.Log($"Failed to initialize Whisper model: {model.Name}");
                StaticLogger.Log(ex);
                return false;
            }
        }

        // Überladung für Standard-Initialisierung (nimmt das erste gefundene Modell)
        public async Task<bool> InitializeAsync()
        {
            if (this.IsInitialized)
            {
                return true;
            }

            if (this.AvailableModels.Count == 0)
            {
                this.DiscoverModels();
            }

            if (this.AvailableModels.Count == 0)
            {
                StaticLogger.Log("No valid Whisper models found in SearchDirectories.");
                return false;
            }
            return await this.InitializeAsync(this.AvailableModels[0]);
        }

        public bool DeInitialize()
        {
            this._encoderSession?.Dispose();
            this._encoderSession = null;
            this._decoderSession?.Dispose();
            this._decoderSession = null;
            this.CurrentModel = null;
            this.TokenMap = null;

            if (!this.IsInitialized)
            {
                return true;
            }

            return false;
        }

        public void Dispose()
        {
            this._encoderSession?.Dispose();
            this._decoderSession?.Dispose();
            this._encoderSession = null;
            this._decoderSession = null;
            this.CurrentModel = null;
            GC.SuppressFinalize(this);
        }
    }

    // Hilfsklasse zum Speichern der dynamischen IDs
    public class WhisperTokenMap
    {
        public int Sot { get; }             // <|startoftranscript|>
        public int Eot { get; }             // <|endoftext|>
        public int Translate { get; }       // <|translate|>
        public int Transcribe { get; }      // <|transcribe|>
        public int NoTimestamps { get; }    // <|notimestamps|>
        public int English { get; }         // <|en|>

        // Vocab lookup for special tokens (token string -> id)
        private readonly Dictionary<string, int> _vocabLookup = new();
        private readonly Tokenizer _tokenizer;

        public WhisperTokenMap(Tokenizer tokenizer, string? generationConfigPath = null, string? vocabPath = null)
        {
            this._tokenizer = tokenizer;

            // Build vocab lookup from vocab.json for reliable special token resolution
            if (!string.IsNullOrEmpty(vocabPath) && File.Exists(vocabPath))
            {
                try
                {
                    var vocabText = File.ReadAllText(vocabPath);
                    using var doc = System.Text.Json.JsonDocument.Parse(vocabText);
                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        if (prop.Value.ValueKind == System.Text.Json.JsonValueKind.Number)
                        {
                            this._vocabLookup[prop.Name] = prop.Value.GetInt32();
                        }
                    }
                    StaticLogger.Log($"WhisperTokenMap: loaded {this._vocabLookup.Count} entries from vocab.json");
                }
                catch (Exception ex)
                {
                    StaticLogger.Log($"WhisperTokenMap: failed to parse vocab.json: {ex.Message}");
                }
            }

            // Try to read decoder_start_token_id / eos_token_id from generation_config.json
            int genSot = -1, genEot = -1;
            if (!string.IsNullOrEmpty(generationConfigPath) && File.Exists(generationConfigPath))
            {
                try
                {
                    var genText = File.ReadAllText(generationConfigPath);
                    using var doc = System.Text.Json.JsonDocument.Parse(genText);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("decoder_start_token_id", out var dstProp) && dstProp.ValueKind == System.Text.Json.JsonValueKind.Number)
                    {
                        genSot = dstProp.GetInt32();
                    }

                    if (root.TryGetProperty("eos_token_id", out var eosProp) && eosProp.ValueKind == System.Text.Json.JsonValueKind.Number)
                    {
                        genEot = eosProp.GetInt32();
                    }

                    StaticLogger.Log($"WhisperTokenMap: generation_config decoder_start={genSot}, eos={genEot}");
                }
                catch (Exception ex)
                {
                    StaticLogger.Log($"WhisperTokenMap: failed to parse generation_config.json: {ex.Message}");
                }
            }

            this.Sot = genSot > 0 ? genSot : this.GetSpecialTokenId("<|startoftranscript|>", 50258);
            this.Eot = genEot > 0 ? genEot : this.GetSpecialTokenId("<|endoftext|>", 50257);
            this.Translate = this.GetSpecialTokenId("<|translate|>", 50358);
            this.Transcribe = this.GetSpecialTokenId("<|transcribe|>", 50359);
            this.NoTimestamps = this.GetSpecialTokenId("<|notimestamps|>", 50363);
            this.English = this.GetSpecialTokenId("<|en|>", 50259);

            StaticLogger.Log($"WhisperTokenMap resolved: SOT={this.Sot}, EOT={this.Eot}, Transcribe={this.Transcribe}, Translate={this.Translate}, NoTimestamps={this.NoTimestamps}, En={this.English}");
        }

        private int GetSpecialTokenId(string text, int fallback)
        {
            // 1. Try vocab.json lookup (most reliable)
            if (this._vocabLookup.TryGetValue(text, out int vocabId))
            {
                return vocabId;
            }

            // 2. Try tokenizer encode (works if tokenizer knows special tokens)
            try
            {
                var ids = this._tokenizer.EncodeToIds(text);
                if (ids.Count == 1)
                {
                    return ids[0];
                }
            }
            catch { }

            // 3. Fallback for known Whisper offsets
            StaticLogger.Log($"Warning: Special token '{text}' not found in vocab or tokenizer. Using fallback: {fallback}");
            return fallback;
        }

        public int GetLanguageId(string langCode)
        {
            string token = $"<|{langCode.ToLower()}|>";

            // Try vocab lookup first
            if (this._vocabLookup.TryGetValue(token, out int id))
            {
                return id;
            }

            try
            {
                var ids = this._tokenizer.EncodeToIds(token);
                if (ids.Count == 1)
                {
                    return ids[0];
                }
            }
            catch { }

            return this.English;
        }

        public bool HasToken(string token)
        {
            if (string.IsNullOrEmpty(token))
            {
                return false;
            }

            if (this._vocabLookup.ContainsKey(token))
            {
                return true;
            }

            try
            {
                var ids = this._tokenizer.EncodeToIds(token);
                return ids.Count == 1;
            }
            catch { return false; }
        }
    }
}
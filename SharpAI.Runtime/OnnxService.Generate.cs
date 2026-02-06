using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using MathNet.Numerics.IntegralTransforms;
using SharpAI.Core;

namespace SharpAI.Runtime
{
    public partial class OnnxService
    {
        public double? CurrentWhisperProgress { get; private set; }

        public async Task<string?> TranscribeAsync(AudioObj audio, string? language = null, bool taskTranslate = false, bool useTimestamps = false, bool useOverlap = true, double chunkDuration = 20, IProgress<double>? progress = null, CancellationToken ct = default)
        {
            if (!IsInitialized && !await InitializeAsync(CurrentModel)) return null;
            if (CurrentModel == null) return null;

            try
            {
                // 1. Audio-Vorbereitung (Whisper Standard: 16kHz, Mono, 30s)
                StaticLogger.Log($"Whisper: preparing audio (sr={audio.SampleRate}, ch={audio.Channels}, samples={audio.Data.Length}).");
                float[] pcmData = await PrepareAudioForWhisperAsync(audio);
                StaticLogger.Log($"Whisper: prepared PCM length={pcmData.Length}.");

                if (ct.IsCancellationRequested)
                {
                    return null;
                }

                // Whisper encoder expects exactly 30s of mel frames (3000 frames at hop=160, sr=16000)
                // so chunkSamples is always 480000 regardless of chunkDuration.
                // chunkDuration controls the stride (how much we advance per chunk).
                const int chunkSamples = 480000;
                int featureSize = GetFeatureSizeFromConfig();
                int durationSamples = Math.Max(SampleRate, (int)(chunkDuration * SampleRate));
                int effectiveChunkForStride = Math.Min(durationSamples, chunkSamples);
                int overlapSamples = useOverlap ? Math.Min(effectiveChunkForStride / 2, SampleRate * 2) : 0;
                int strideSamples = Math.Max(1, effectiveChunkForStride - overlapSamples);
                int totalChunks = (int)Math.Ceiling(pcmData.Length / (double)strideSamples);
                StaticLogger.Log($"Whisper: chunkDuration={chunkDuration}s, strideSamples={strideSamples}, totalChunks={totalChunks}.");
                var combinedResult = new StringBuilder();
                UpdateWhisperProgress(0, progress);

                for (int chunkIndex = 0; chunkIndex < totalChunks; chunkIndex++)
                {
                    if (ct.IsCancellationRequested)
                    {
                        return null;
                    }

                    int offset = chunkIndex * strideSamples;
                    int remaining = Math.Max(0, pcmData.Length - offset);
                    int copyLength = Math.Min(chunkSamples, remaining);
                    var chunk = new float[chunkSamples];
                    Array.Copy(pcmData, offset, chunk, 0, copyLength);
                    StaticLogger.Log($"Whisper: chunk {chunkIndex + 1}/{totalChunks}, samples={chunk.Length}, copied={copyLength}.");

                    var melFeatures = ComputeMelSpectrogram(chunk, featureSize);
                    StaticLogger.Log($"Whisper: mel features shape=[{string.Join(",", melFeatures.Dimensions.ToArray())}].");

                    if (ct.IsCancellationRequested)
                    {
                        return null;
                    }

                    var encoderInputs = new List<NamedOnnxValue>
                    {
                        NamedOnnxValue.CreateFromTensor("input_features", melFeatures)
                    };

                    using var encoderResults = _encoderSession!.Run(encoderInputs);
                    var hiddenStates = encoderResults.First().Value as DenseTensor<float>;
                    if (hiddenStates == null) return null;
                    StaticLogger.Log($"Whisper: encoder hidden states shape=[{string.Join(",", hiddenStates.Dimensions.ToArray())}].");

                    float[] hiddenDataCopy = hiddenStates.ToArray();
                    var hiddenStatesCopy = new DenseTensor<float>(hiddenDataCopy, hiddenStates.Dimensions);

                    try
                    {
                        var chunkText = await Task.Run(() => RunDecoderLoop(hiddenStatesCopy, language, taskTranslate, useTimestamps, chunkDuration, ct));
                        if (!string.IsNullOrWhiteSpace(chunkText))
                        {
                            if (combinedResult.Length > 0)
                            {
                                combinedResult.Append("\n");
                            }
                            combinedResult.Append(chunkText.Trim());
                        }
                        else if (combinedResult.Length > 0)
                        {
                            combinedResult.Append("\n");
                        }
                    }
                    finally
                    {
                        ;
                    }

                    UpdateWhisperProgress((chunkIndex + 1) / (double)totalChunks, progress);
                }

                UpdateWhisperProgress(1, progress);
                return combinedResult.ToString();
            }
            catch (Exception ex)
            {
                StaticLogger.Log("Transcription failed.");
                StaticLogger.Log(ex);
                return null;
            }
            finally
            {
                UpdateWhisperProgress(null, progress);
                _encoderSession?.Dispose();
                _decoderSession?.Dispose();
                _encoderSession = null;
                _decoderSession = null;
            }
        }

        public async IAsyncEnumerable<string> TranscribeStreamAsync(AudioObj audio, string? language = null, bool taskTranslate = false, bool useTimestamps = false, bool useOverlap = true, double chunkDuration = 20, IProgress<double>? progress = null, [EnumeratorCancellation] CancellationToken ct = default)
        {
            if (!IsInitialized && !await InitializeAsync(CurrentModel)) yield break;
            if (CurrentModel == null) yield break;

            try
            {
                StaticLogger.Log($"Whisper: preparing audio (sr={audio.SampleRate}, ch={audio.Channels}, samples={audio.Data.Length}).");
                float[] pcmData = await PrepareAudioForWhisperAsync(audio);
                StaticLogger.Log($"Whisper: prepared PCM length={pcmData.Length}.");

                if (ct.IsCancellationRequested)
                {
                    yield break;
                }

                // Whisper encoder expects exactly 30s of mel frames (3000 frames at hop=160, sr=16000)
                // so chunkSamples is always 480000 regardless of chunkDuration.
                // chunkDuration controls the stride (how much we advance per chunk).
                const int chunkSamples = 480000;
                int featureSize = GetFeatureSizeFromConfig();
                int durationSamples = Math.Max(SampleRate, (int)(chunkDuration * SampleRate));
                int effectiveChunkForStride = Math.Min(durationSamples, chunkSamples);
                int overlapSamples = useOverlap ? Math.Min(effectiveChunkForStride / 2, SampleRate * 2) : 0;
                int strideSamples = Math.Max(1, effectiveChunkForStride - overlapSamples);
                int totalChunks = (int)Math.Ceiling(pcmData.Length / (double)strideSamples);
                StaticLogger.Log($"Whisper: chunkDuration={chunkDuration}s, strideSamples={strideSamples}, totalChunks={totalChunks}.");
                UpdateWhisperProgress(0, progress);

                for (int chunkIndex = 0; chunkIndex < totalChunks; chunkIndex++)
                {
                    if (ct.IsCancellationRequested)
                    {
                        yield break;
                    }

                    int offset = chunkIndex * strideSamples;
                    int remaining = Math.Max(0, pcmData.Length - offset);
                    int copyLength = Math.Min(chunkSamples, remaining);
                    var chunk = new float[chunkSamples];
                    Array.Copy(pcmData, offset, chunk, 0, copyLength);
                    StaticLogger.Log($"Whisper: chunk {chunkIndex + 1}/{totalChunks}, samples={chunk.Length}, copied={copyLength}.");

                    var melFeatures = ComputeMelSpectrogram(chunk, featureSize);
                    StaticLogger.Log($"Whisper: mel features shape=[{string.Join(",", melFeatures.Dimensions.ToArray())}].");

                    if (ct.IsCancellationRequested)
                    {
                        yield break;
                    }

                    var encoderInputs = new List<NamedOnnxValue>
                    {
                        NamedOnnxValue.CreateFromTensor("input_features", melFeatures)
                    };

                    using var encoderResults = _encoderSession!.Run(encoderInputs);
                    var hiddenStates = encoderResults.First().Value as DenseTensor<float>;
                    if (hiddenStates == null) yield break;
                    StaticLogger.Log($"Whisper: encoder hidden states shape=[{string.Join(",", hiddenStates.Dimensions.ToArray())}].");

                    float[] hiddenDataCopy = hiddenStates.ToArray();
                    var hiddenStatesCopy = new DenseTensor<float>(hiddenDataCopy, hiddenStates.Dimensions);

                    var chunkText = await Task.Run(() => RunDecoderLoop(hiddenStatesCopy, language, taskTranslate, useTimestamps, chunkDuration, ct));
                    if (!string.IsNullOrWhiteSpace(chunkText))
                    {
                        if (chunkIndex > 0)
                        {
                            yield return "\n";
                        }
                        yield return chunkText.Trim();
                    }
                    else if (chunkIndex > 0)
                    {
                        yield return "\n";
                    }

                    UpdateWhisperProgress((chunkIndex + 1) / (double)totalChunks, progress);
                }

                UpdateWhisperProgress(1, progress);
            }
            finally
            {
                UpdateWhisperProgress(null, progress);
                _encoderSession?.Dispose();
                _decoderSession?.Dispose();
                _encoderSession = null;
                _decoderSession = null;
            }
        }

        private async Task<float[]> PrepareAudioForWhisperAsync(AudioObj audio)
        {
            // Kopie erstellen für Transformation
            var processed = new AudioObj(audio.Data, audio.SampleRate, audio.Channels, audio.BitDepth);

            // Schritt 1: Mono
            if (processed.Channels > 1)
            {
                StaticLogger.Log($"Whisper: rechannel from {processed.Channels} to 1.");
                await processed.RechannelAsync(1);
            }

            // Schritt 2: 16.000 Hz
            if (processed.SampleRate != 16000)
            {
                StaticLogger.Log($"Whisper: resample from {processed.SampleRate} to 16000.");
                await processed.ResampleAsync(16000);
            }

            var finalPcm = processed.Data.ToArray();
            StaticLogger.Log($"Whisper: prepared PCM samples={finalPcm.Length}.");

            return finalPcm;
        }

        private string RunDecoderLoop(DenseTensor<float> encoderHiddenStates, string? lang, bool translate, bool useTimestamps, double chunkDuration = 20, CancellationToken ct = default)
        {
            EnsureTokenizerLoaded();

            try
            {
                // Build the SOT (start-of-transcript) prompt.
                // Whisper multilingual models expect: <|startoftranscript|> <|lang|> <|task|> [<|notimestamps|>]
                // The forced_decoder_ids from generation_config.json define the expected positions.
                List<int> tokens = new List<int> { GetTokenIdOrDefault("<|startoftranscript|>", 50258) };
                StaticLogger.Log($"Whisper: decoder start, language='{lang ?? ""}', translate={translate}, timestamps={useTimestamps}.");

                // Try to read forced_decoder_ids from generation_config.json for correct defaults
                var forcedIds = ReadForcedDecoderIds();

                // Language token — required for multilingual models at position 1
                if (!string.IsNullOrWhiteSpace(lang))
                {
                    var langToken = GetTokenIdOrDefault($"<|{lang}|>", -1);
                    if (langToken >= 0)
                    {
                        tokens.Add(langToken);
                    }
                    else
                    {
                        // Language string not found in vocab; try forced_decoder_ids or default to <|en|>
                        tokens.Add(GetForcedIdAtPosition(forcedIds, 1, GetTokenIdOrDefault("<|en|>", 50259)));
                    }
                }
                else
                {
                    // No language specified — use forced_decoder_ids or default to <|en|>
                    tokens.Add(GetForcedIdAtPosition(forcedIds, 1, GetTokenIdOrDefault("<|en|>", 50259)));
                }

                // Task token at position 2
                // Use task_to_id from generation_config.json when available (handles vocab shifts in
                // models like large-v3-turbo). Fall back to forced_decoder_ids, then vocab lookup.
                {
                    string taskName = translate ? "translate" : "transcribe";
                    if (_configTaskToId != null && _configTaskToId.TryGetValue(taskName, out var configTaskId))
                    {
                        tokens.Add(configTaskId);
                        StaticLogger.Log($"Whisper: using task_to_id['{taskName}']={configTaskId} for position 2.");
                    }
                    else if (!translate && forcedIds.TryGetValue(2, out var forcedTaskId))
                    {
                        tokens.Add(forcedTaskId);
                        StaticLogger.Log($"Whisper: using forced_decoder_id {forcedTaskId} for task position 2.");
                    }
                    else
                    {
                        var taskToken = translate ? "<|translate|>" : "<|transcribe|>";
                        var taskTokenId = GetTokenIdOrDefault(taskToken, -1);
                        if (taskTokenId >= 0)
                        {
                            tokens.Add(taskTokenId);
                        }
                        else
                        {
                            tokens.Add(GetForcedIdAtPosition(forcedIds, 2, 50359));
                        }
                    }
                }

                // Timestamps control at position 3
                // Use no_timestamps_token_id from generation_config.json when available,
                // because models like large-v3-turbo shift this token (50364 instead of 50363).
                if (!useTimestamps)
                {
                    int noTsToken = _configNoTimestampsTokenId
                        ?? GetTokenIdOrDefault("<|notimestamps|>", 50363);
                    tokens.Add(noTsToken);
                }

                StaticLogger.Log($"Whisper: initial tokens=[{string.Join(",", tokens)}], count={tokens.Count}.");

                if (ct.IsCancellationRequested)
                {
                    return string.Empty;
                }

                // Read stop tokens from config
                int eotToken = GetTokenIdOrDefault("<|endoftext|>", 50257);
                int sotToken = GetTokenIdOrDefault("<|startoftranscript|>", 50258);

                const int maxTokenCount = 448;
                // Heuristic token budget: ~5 tokens/sec of audio is generous for speech
                int tokenBudget = Math.Max(80, (int)(chunkDuration * 6));
                if (tokenBudget > maxTokenCount) tokenBudget = maxTokenCount;
                StaticLogger.Log($"Whisper: token budget={tokenBudget} for chunkDuration={chunkDuration}s.");

                // KV cache: maps input names (past_key_values.*) to cached tensors from previous step
                Dictionary<string, DenseTensor<float>>? kvCache = null;
                int promptTokenCount = tokens.Count;

                for (int i = 0; i < maxTokenCount; i++)
                {
                    if (ct.IsCancellationRequested)
                    {
                        return string.Empty;
                    }

                    if (tokens.Count >= maxTokenCount)
                    {
                        StaticLogger.Log($"Whisper: hit max token count {maxTokenCount}, stopping.");
                        break;
                    }

                    bool useCache = kvCache != null;

                    // First step: pass all prompt tokens. Subsequent steps: pass only the last token.
                    long[] inputIdsArray;
                    if (!useCache)
                    {
                        inputIdsArray = tokens.Select(t => (long)t).ToArray();
                    }
                    else
                    {
                        inputIdsArray = new[] { (long)tokens[^1] };
                    }
                    var inputIds = new DenseTensor<long>(inputIdsArray, new[] { 1, inputIdsArray.Length });

                    var namedInputs = new List<NamedOnnxValue>();
                    int encoderSeqLength = (int)encoderHiddenStates.Dimensions[1];
                    foreach (var kv in _decoderSession!.InputMetadata)
                    {
                        var name = kv.Key;
                        var meta = kv.Value;

                        if (string.Equals(name, "input_ids", StringComparison.Ordinal))
                        {
                            namedInputs.Add(NamedOnnxValue.CreateFromTensor("input_ids", inputIds));
                            continue;
                        }
                        if (string.Equals(name, "encoder_hidden_states", StringComparison.Ordinal))
                        {
                            namedInputs.Add(NamedOnnxValue.CreateFromTensor("encoder_hidden_states", encoderHiddenStates));
                            continue;
                        }

                        bool isPastKV = name.Contains("past_key_values", StringComparison.OrdinalIgnoreCase) || name.Contains("past_key");
                        bool isUseCacheBranch = string.Equals(name, "use_cache_branch", StringComparison.OrdinalIgnoreCase);

                        if (isUseCacheBranch)
                        {
                            namedInputs.Add(NamedOnnxValue.CreateFromTensor(name,
                                new DenseTensor<bool>(new[] { useCache }, new[] { 1 })));
                            continue;
                        }

                        if (isPastKV)
                        {
                            if (useCache && kvCache!.TryGetValue(name, out var cached))
                            {
                                namedInputs.Add(NamedOnnxValue.CreateFromTensor(name, cached));
                            }
                            else
                            {
                                // Empty cache for first step: variable dims get batch=1, seq=0
                                var rawDims = meta.Dimensions ?? Array.Empty<int>();
                                var dims = rawDims.Select(d => d <= 0 ? 1 : d).ToArray();
                                for (int idx = 0; idx < rawDims.Length; idx++)
                                {
                                    if (rawDims[idx] <= 0)
                                        dims[idx] = (idx == 0) ? 1 : 0;
                                }
                                int totalSize = dims.Aggregate(1, (a, b) => a * b);
                                namedInputs.Add(NamedOnnxValue.CreateFromTensor(name,
                                    new DenseTensor<float>(new float[totalSize], dims)));
                            }
                            continue;
                        }

                        // Handle other generic inputs (attention masks, etc.)
                        var rawDimsG = meta.Dimensions ?? Array.Empty<int>();
                        var dimsG = rawDimsG.Select(d => d <= 0 ? 1 : d).ToArray();
                        bool isAttentionMask = name.Contains("attention_mask", StringComparison.OrdinalIgnoreCase);
                        bool isEncoderAttentionMask = name.Contains("encoder_attention", StringComparison.OrdinalIgnoreCase);
                        for (int idx = 0; idx < rawDimsG.Length; idx++)
                        {
                            if (rawDimsG[idx] <= 0)
                            {
                                if (isAttentionMask)
                                {
                                    dimsG[idx] = isEncoderAttentionMask ? encoderSeqLength : inputIdsArray.Length;
                                }
                                else
                                {
                                    dimsG[idx] = 1;
                                }
                                if (!isAttentionMask) break;
                            }
                        }
                        int totalSizeG = dimsG.Aggregate(1, (a, b) => a * b);

                        if (meta.ElementType == typeof(float) || meta.ElementType == typeof(Single))
                        {
                            var buf = new float[totalSizeG];
                            if (isAttentionMask) Array.Fill(buf, 1f);
                            namedInputs.Add(NamedOnnxValue.CreateFromTensor(name, new DenseTensor<float>(buf, dimsG)));
                        }
                        else if (meta.ElementType == typeof(long) || meta.ElementType == typeof(Int64))
                        {
                            var buf = new long[totalSizeG];
                            if (isAttentionMask) Array.Fill(buf, 1L);
                            namedInputs.Add(NamedOnnxValue.CreateFromTensor(name, new DenseTensor<long>(buf, dimsG)));
                        }
                        else if (meta.ElementType == typeof(int) || meta.ElementType == typeof(Int32))
                        {
                            var buf = new int[totalSizeG];
                            if (isAttentionMask) Array.Fill(buf, 1);
                            namedInputs.Add(NamedOnnxValue.CreateFromTensor(name, new DenseTensor<int>(buf, dimsG)));
                        }
                        else if (meta.ElementType == typeof(bool))
                        {
                            var buf = new bool[totalSizeG];
                            if (isAttentionMask) Array.Fill(buf, true);
                            namedInputs.Add(NamedOnnxValue.CreateFromTensor(name, new DenseTensor<bool>(buf, dimsG)));
                        }
                        else
                        {
                            var buf = new float[totalSizeG];
                            namedInputs.Add(NamedOnnxValue.CreateFromTensor(name, new DenseTensor<float>(buf, dimsG)));
                        }
                    }

                    try
                    {
                        // Validate rank on first step only
                        if (i == 0)
                        {
                            foreach (var kv2 in _decoderSession.InputMetadata)
                            {
                                var provided = namedInputs.FirstOrDefault(n => string.Equals(n.Name, kv2.Key, StringComparison.Ordinal));
                                if (provided == null) { StaticLogger.Log($"Whisper: missing input '{kv2.Key}'."); continue; }

                                int[]? pDims = provided.Value switch
                                {
                                    Tensor<float> tf => tf.Dimensions.ToArray(),
                                    Tensor<long> tl => tl.Dimensions.ToArray(),
                                    Tensor<int> ti => ti.Dimensions.ToArray(),
                                    Tensor<bool> tb => tb.Dimensions.ToArray(),
                                    _ => null
                                };
                                if (pDims == null) continue;
                                var eDims = kv2.Value.Dimensions ?? Array.Empty<int>();
                                StaticLogger.Log($"Whisper: input '{kv2.Key}' meta=[{string.Join(",", eDims)}] provided=[{string.Join(",", pDims)}]");
                                if (eDims.Length != pDims.Length)
                                {
                                    StaticLogger.Log($"Whisper: rank mismatch for '{kv2.Key}'. Aborting.");
                                    return string.Empty;
                                }
                            }
                        }

                        using var decoderResults = _decoderSession.Run(namedInputs);

                        // Find logits output
                        var logitsResult = decoderResults.FirstOrDefault(r =>
                            r.Name.Equals("logits", StringComparison.OrdinalIgnoreCase)) ?? decoderResults.First();
                        var logits = logitsResult.AsTensor<float>();
                        if (i == 0)
                        {
                            StaticLogger.Log($"Whisper: logits shape=[{string.Join(",", logits.Dimensions.ToArray())}], outputs={decoderResults.Count}.");
                        }
                        int genCountBefore = tokens.Count - promptTokenCount;
                        int minTokens = Math.Min(60, Math.Max(10, (int)(chunkDuration * 2)));
                        int? bannedToken = (genCountBefore < minTokens) ? eotToken : null;
                        int nextToken = GetNextTokenGreedy(logits, bannedToken);
                        if (bannedToken.HasValue && nextToken != eotToken)
                        {
                            StaticLogger.Log($"Whisper: suppressed early EOT at step {i} (minTokens={minTokens}).");
                        }

                        // Log first tokens and stop tokens
                        if (i < 5 || nextToken == eotToken)
                        {
                            string tokenName = (_tokenIdToString != null && _tokenIdToString.TryGetValue(nextToken, out var tn)) ? tn : "?";
                            StaticLogger.Log($"Whisper: step {i}, nextToken={nextToken} ('{tokenName}').");
                        }

                        // Capture KV cache from "present.*" outputs for next iteration
                        var newCache = new Dictionary<string, DenseTensor<float>>();
                        foreach (var output in decoderResults)
                        {
                            if (output.Name.StartsWith("present", StringComparison.OrdinalIgnoreCase))
                            {
                                string suffix = output.Name.Substring("present".Length);
                                string inputName = "past_key_values" + suffix;
                                var tensor = output.AsTensor<float>();
                                var data = tensor.ToArray();
                                newCache[inputName] = new DenseTensor<float>(data, tensor.Dimensions.ToArray());
                            }
                        }
                        if (newCache.Count > 0)
                        {
                            if (i == 0)
                            {
                                StaticLogger.Log($"Whisper: KV cache captured, {newCache.Count} tensors.");
                            }
                            kvCache = newCache;
                        }
                        else if (i == 0)
                        {
                            StaticLogger.Log($"Whisper: WARNING — no 'present.*' outputs found. KV cache disabled (slow).");
                        }

                        // --- Stop conditions ---
                        // 1. End of text
                        if (nextToken == eotToken)
                        {
                            StaticLogger.Log($"Whisper: EOT at step {i}.");
                            break;
                        }

                        // 2. SOT token appearing mid-sequence means the model is looping
                        if (nextToken == sotToken && i > 0)
                        {
                            StaticLogger.Log($"Whisper: SOT token detected at step {i}, stopping (loop detected).");
                            break;
                        }

                        tokens.Add(nextToken);
                        int genCount = tokens.Count - promptTokenCount;

                        // 3. Token budget exceeded — stop to prevent runaway generation
                        if (genCount >= tokenBudget)
                        {
                            StaticLogger.Log($"Whisper: token budget {tokenBudget} exceeded at step {i}. Stopping.");
                            break;
                        }

                        // 4. Multi-window repetition detection
                        //    Check window sizes 2..8; if any window repeats 2+ times, it's a loop.
                        bool loopDetected = false;
                        for (int ws = 2; ws <= 8 && !loopDetected; ws++)
                        {
                            if (genCount < ws * 3) continue; // need 3 repetitions worth of tokens
                            bool allMatch = true;
                            // Check if the last 'ws' tokens equal the 'ws' tokens before them,
                            // and those equal the 'ws' tokens before those (3 consecutive identical blocks)
                            for (int rep = 1; rep <= 2 && allMatch; rep++)
                            {
                                for (int w = 0; w < ws && allMatch; w++)
                                {
                                    if (tokens[tokens.Count - 1 - w] != tokens[tokens.Count - 1 - w - (ws * rep)])
                                        allMatch = false;
                                }
                            }
                            if (allMatch)
                            {
                                loopDetected = true;
                                // Trim all repeated blocks except the first occurrence
                                int trimCount = ws * 2;
                                if (tokens.Count - trimCount >= promptTokenCount)
                                {
                                    tokens.RemoveRange(tokens.Count - trimCount, trimCount);
                                }
                                StaticLogger.Log($"Whisper: repetition loop (window={ws}) at step {i}, trimmed {trimCount} tokens. Remaining gen={tokens.Count - promptTokenCount}.");
                            }
                        }
                        if (loopDetected) break;
                    }
                    catch (Exception valEx)
                    {
                        StaticLogger.Log("Whisper: decoder run failed.");
                        StaticLogger.Log(valEx);
                        return string.Empty;
                    }
                }

                StaticLogger.Log($"Whisper: decoder produced {tokens.Count} tokens total.");
                var decoded = DecodeTokens(tokens);
                StaticLogger.Log($"Whisper: decoded text length={decoded.Length}.");
                return decoded;
            }
            catch (Exception ex)
            {
                StaticLogger.Log("Decoder loop failed.");
                StaticLogger.Log(ex);

                // Clear inference-related state to free RAM; only reload the model as a last resort
                _tokenIdToString = null;
                _tokenStringToId = null;
                _byteDecoder = null;

                GC.Collect();
                GC.WaitForPendingFinalizers();

                return string.Empty;
            }
        }

        private int GetNextTokenGreedy(Tensor<float> logits, int? bannedToken = null)
        {
            // Logits Shape ist meist [batch, sequence, vocab_size]
            // Wir nehmen den letzten Token der Sequenz
            int vocabSize = logits.Dimensions[2];
            int lastIndex = (logits.Dimensions[1] - 1) * vocabSize;

            float maxVal = float.MinValue;
            int maxIdx = 0;

            for (int i = 0; i < vocabSize; i++)
            {
                if (bannedToken.HasValue && i == bannedToken.Value)
                {
                    continue;
                }
                if (logits.GetValue(lastIndex + i) > maxVal)
                {
                    maxVal = logits.GetValue(lastIndex + i);
                    maxIdx = i;
                }
            }
            return maxIdx;
        }

        /// <summary>
        /// Reads forced_decoder_ids, no_timestamps_token_id, and task_to_id from the model's generation_config.json.
        /// Returns a dictionary mapping position → token_id, or empty if unavailable.
        /// </summary>
        private Dictionary<int, int> ReadForcedDecoderIds()
        {
            var result = new Dictionary<int, int>();
            if (CurrentModel == null || string.IsNullOrWhiteSpace(CurrentModel.GenerationConfigPath) || !File.Exists(CurrentModel.GenerationConfigPath))
            {
                return result;
            }

            try
            {
                using var stream = File.OpenRead(CurrentModel.GenerationConfigPath);
                using var doc = JsonDocument.Parse(stream);

                if (doc.RootElement.TryGetProperty("forced_decoder_ids", out var forcedElement) &&
                    forcedElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var pair in forcedElement.EnumerateArray())
                    {
                        if (pair.ValueKind == JsonValueKind.Array && pair.GetArrayLength() == 2)
                        {
                            // Skip entries where either value is null (large-v3-turbo has null language entries)
                            if (pair[0].ValueKind == JsonValueKind.Null || pair[1].ValueKind == JsonValueKind.Null)
                                continue;
                            if (pair[0].TryGetInt32(out var pos) && pair[1].TryGetInt32(out var tokenId))
                            {
                                result[pos] = tokenId;
                            }
                        }
                    }
                }

                // Read no_timestamps_token_id (differs across models, e.g. 50363 vs 50364)
                if (doc.RootElement.TryGetProperty("no_timestamps_token_id", out var noTsElement) &&
                    noTsElement.TryGetInt32(out var noTsId))
                {
                    _configNoTimestampsTokenId = noTsId;
                    StaticLogger.Log($"Whisper: no_timestamps_token_id={noTsId} from generation_config.json.");
                }
                else
                {
                    _configNoTimestampsTokenId = null;
                }

                // Read task_to_id for correct translate/transcribe token mapping
                if (doc.RootElement.TryGetProperty("task_to_id", out var taskElement) &&
                    taskElement.ValueKind == JsonValueKind.Object)
                {
                    _configTaskToId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    foreach (var prop in taskElement.EnumerateObject())
                    {
                        if (prop.Value.TryGetInt32(out var tid))
                        {
                            _configTaskToId[prop.Name] = tid;
                        }
                    }
                    StaticLogger.Log($"Whisper: task_to_id=[{string.Join(", ", _configTaskToId.Select(kv => $"{kv.Key}:{kv.Value}"))}].");
                }
                else
                {
                    _configTaskToId = null;
                }

                if (result.Count > 0)
                {
                    StaticLogger.Log($"Whisper: read {result.Count} forced_decoder_ids from generation_config.json: [{string.Join(", ", result.Select(kv => $"{kv.Key}:{kv.Value}"))}]");
                }
            }
            catch (Exception ex)
            {
                StaticLogger.Log("Whisper: failed to read generation_config.json for forced_decoder_ids.");
                StaticLogger.Log(ex);
            }

            return result;
        }

        private int? _configNoTimestampsTokenId;
        private Dictionary<string, int>? _configTaskToId;

        private static int GetForcedIdAtPosition(Dictionary<int, int> forcedIds, int position, int fallback)
        {
            return forcedIds.TryGetValue(position, out var id) ? id : fallback;
        }

        private int GetFeatureSizeFromConfig()
        {
            const int defaultSize = 80;
            if (CurrentModel == null || string.IsNullOrWhiteSpace(CurrentModel.PreprocessorConfigPath) || !File.Exists(CurrentModel.PreprocessorConfigPath))
            {
                return defaultSize;
            }

            try
            {
                using var stream = File.OpenRead(CurrentModel.PreprocessorConfigPath);
                using var doc = JsonDocument.Parse(stream);

                if (doc.RootElement.TryGetProperty("n_mels", out var nMelsElement) && nMelsElement.TryGetInt32(out var nMels))
                {
                    return nMels;
                }

                if (doc.RootElement.TryGetProperty("num_mel_bins", out var melBinsElement) && melBinsElement.TryGetInt32(out var melBins))
                {
                    return melBins;
                }

                if (doc.RootElement.TryGetProperty("feature_size", out var featureSizeElement) && featureSizeElement.TryGetInt32(out var featureSize))
                {
                    return featureSize;
                }
            }
            catch (Exception ex)
            {
                StaticLogger.Log("Failed to read whisper preprocessor config for feature size.");
                StaticLogger.Log(ex);
            }

            return defaultSize;
        }


        // Whisper Konstanten
        private const int SampleRate = 16000;
        private const int FFTSize = 400;
        private const int HopLength = 160;
        private int ChunkLength = 30; // Sekunden
        private int MaxFrames => (SampleRate * ChunkLength) / HopLength;

        private DenseTensor<float> ComputeMelSpectrogram(float[] pcm, int nMels)
        {
            // 1. Erstelle die Mel-Filterbank (falls nicht gecached)
            float[][] filters = GetMelFilters(nMels);

            // 2. STFT Vorbereitung
            // Whisper nutzt "Reflect Padding" am Anfang und Ende
            float[] paddedPcm = PadAudio(pcm, FFTSize / 2);

            var melSpectrogram = new float[nMels * MaxFrames];
            float[] window = Enumerable.Range(0, FFTSize)
                .Select(i => 0.5f * (1 - (float)Math.Cos(2 * Math.PI * i / FFTSize)))
                .ToArray(); // Hanning Window

            // 3. STFT Loop
            for (int frame = 0; frame < MaxFrames; frame++)
            {
                int start = frame * HopLength;
                float[] fftBuffer = new float[FFTSize];

                // Windowing anwenden
                for (int i = 0; i < FFTSize; i++)
                {
                    fftBuffer[i] = paddedPcm[start + i] * window[i];
                }

                // FFT berechnen — match torch.stft (unnormalized)
                var fftData = new Complex[FFTSize];
                for (int i = 0; i < FFTSize; i++)
                    fftData[i] = new Complex(fftBuffer[i], 0);
                Fourier.Forward(fftData, FourierOptions.Default);
                // Default applies 1/sqrt(N). Undo to get unnormalized magnitudes.
                // magnitude² with 1/sqrt(N) = |X|²/N. Multiply by N to get |X|².
                double fftScale = FFTSize; // undo 1/sqrt(N) on magnitude²

                // Power Spectrum (Magnituden-Quadrat)
                float[] magnitudes = new float[FFTSize / 2 + 1];
                for (int i = 0; i < magnitudes.Length; i++)
                {
                    double mag = fftData[i].Magnitude;
                    magnitudes[i] = (float)(mag * mag * fftScale);
                }

                // 4. Mel-Filterbank anwenden
                for (int melBin = 0; melBin < nMels; melBin++)
                {
                    float melValue = 0;
                    for (int i = 0; i < magnitudes.Length; i++)
                    {
                        melValue += magnitudes[i] * filters[melBin][i];
                    }

                    // Log-Scaling (Whisper: log10 + Clipping)
                    melValue = Math.Max(melValue, 1e-10f);
                    melValue = (float)Math.Log10(melValue);

                    melSpectrogram[melBin * MaxFrames + frame] = melValue;
                }
            }

            // 5. Log mel statistics for diagnostics (first chunk only)
            float melMax = melSpectrogram.Max();
            float melMin = melSpectrogram.Min();
            float melAvg = melSpectrogram.Average();
            StaticLogger.Log($"Whisper: mel stats BEFORE norm: min={melMin:F2}, max={melMax:F2}, avg={melAvg:F2}.");

            // 6. Normalisierung (Whisper: clamp to max-8, then (x+4)/4)
            NormalizeMel(melSpectrogram, nMels);

            float nMax = melSpectrogram.Max();
            float nMin = melSpectrogram.Min();
            float nAvg = melSpectrogram.Average();
            StaticLogger.Log($"Whisper: mel stats AFTER norm: min={nMin:F2}, max={nMax:F2}, avg={nAvg:F2}.");

            return new DenseTensor<float>(melSpectrogram, new[] { 1, nMels, MaxFrames });
        }

        private float[] PadAudio(float[] pcm, int pad)
        {
            float[] padded = new float[pcm.Length + 2 * pad];
            // Reflect Padding
            for (int i = 0; i < pad; i++)
            {
                padded[pad - 1 - i] = pcm[i + 1];
                padded[padded.Length - pad + i] = pcm[pcm.Length - 2 - i];
            }
            Array.Copy(pcm, 0, padded, pad, pcm.Length);
            return padded;
        }

        private void NormalizeMel(float[] mel, int nMels)
        {
            float max = mel.Max();
            float threshold = max - 8.0f;
            for (int i = 0; i < mel.Length; i++)
            {
                mel[i] = Math.Max(mel[i], threshold);
                mel[i] = (mel[i] + 4.0f) / 4.0f;
            }
        }

        private float[][]? _cachedMelFilters;
        private int _cachedMelFilterNMels;

        private float[][] GetMelFilters(int nMels)
        {
            // Return cached filters if already loaded for this nMels
            if (_cachedMelFilters != null && _cachedMelFilterNMels == nMels)
                return _cachedMelFilters;

            // 1. Try to load pre-computed mel filters from preprocessor_config.json
            //    Whisper models are trained with specific mel filterbanks (librosa Slaney scale).
            //    Using the model's own filters is critical for correct output.
            float[][]? loaded = TryLoadMelFiltersFromConfig(nMels);
            if (loaded != null)
            {
                StaticLogger.Log($"Whisper: loaded {loaded.Length}x{loaded[0].Length} mel filters from preprocessor_config.json.");
                _cachedMelFilters = loaded;
                _cachedMelFilterNMels = nMels;
                return loaded;
            }

            // 2. Fallback: compute mel filterbank using Slaney scale (matches librosa default)
            StaticLogger.Log($"Whisper: computing {nMels} mel filters (Slaney scale, fallback).");
            float[][] filters = ComputeMelFiltersSlaney(nMels);
            _cachedMelFilters = filters;
            _cachedMelFilterNMels = nMels;
            return filters;
        }

        private float[][]? TryLoadMelFiltersFromConfig(int nMels)
        {
            if (CurrentModel == null || string.IsNullOrWhiteSpace(CurrentModel.PreprocessorConfigPath) || !File.Exists(CurrentModel.PreprocessorConfigPath))
                return null;

            try
            {
                using var stream = File.OpenRead(CurrentModel.PreprocessorConfigPath);
                using var doc = JsonDocument.Parse(stream);

                if (!doc.RootElement.TryGetProperty("mel_filters", out var melFiltersElement) ||
                    melFiltersElement.ValueKind != JsonValueKind.Array)
                    return null;

                var rows = new List<float[]>();
                foreach (var row in melFiltersElement.EnumerateArray())
                {
                    if (row.ValueKind != JsonValueKind.Array) continue;
                    var vals = new List<float>();
                    foreach (var val in row.EnumerateArray())
                    {
                        if (val.TryGetSingle(out var f))
                            vals.Add(f);
                        else if (val.TryGetDouble(out var d))
                            vals.Add((float)d);
                    }
                    rows.Add(vals.ToArray());
                }

                if (rows.Count == 0) return null;

                // HuggingFace format: mel_filters is [n_mels, n_fft/2+1]
                if (rows.Count == nMels)
                    return rows.ToArray();

                // Some configs transpose: [n_fft/2+1, n_mels] — detect and transpose
                if (rows.Count == FFTSize / 2 + 1 && rows[0].Length == nMels)
                {
                    var transposed = new float[nMels][];
                    for (int m = 0; m < nMels; m++)
                    {
                        transposed[m] = new float[rows.Count];
                        for (int f = 0; f < rows.Count; f++)
                            transposed[m][f] = rows[f][m];
                    }
                    return transposed;
                }

                StaticLogger.Log($"Whisper: mel_filters shape [{rows.Count},{rows[0].Length}] doesn't match expected [{nMels},{FFTSize / 2 + 1}].");
                return null;
            }
            catch (Exception ex)
            {
                StaticLogger.Log("Whisper: failed to load mel_filters from preprocessor_config.json.");
                StaticLogger.Log(ex);
                return null;
            }
        }

        /// <summary>
        /// Computes mel filterbank using the Slaney mel scale with area normalization,
        /// matching librosa.filters.mel(sr, n_fft, n_mels, norm='slaney', htk=False).
        /// </summary>
        private float[][] ComputeMelFiltersSlaney(int nMels)
        {
            int nFft = FFTSize / 2 + 1;
            double fMin = 0;
            double fMax = SampleRate / 2.0;

            // Slaney mel scale: linear below 1000 Hz, logarithmic above
            double SlaneyFreqToMel(double f)
            {
                const double f_sp = 200.0 / 3.0; // ~66.67 Hz
                double minLogHz = 1000.0;
                double minLogMel = minLogHz / f_sp; // 15.0
                double logstep = Math.Log(6.4) / 27.0;
                if (f < minLogHz)
                    return f / f_sp;
                return minLogMel + Math.Log(f / minLogHz) / logstep;
            }

            double SlaneyMelToFreq(double m)
            {
                const double f_sp = 200.0 / 3.0;
                double minLogHz = 1000.0;
                double minLogMel = minLogHz / f_sp;
                double logstep = Math.Log(6.4) / 27.0;
                if (m < minLogMel)
                    return m * f_sp;
                return minLogHz * Math.Exp(logstep * (m - minLogMel));
            }

            double melMin = SlaneyFreqToMel(fMin);
            double melMax = SlaneyFreqToMel(fMax);

            // n_mels + 2 evenly spaced points in mel space
            double[] melPoints = new double[nMels + 2];
            for (int i = 0; i < melPoints.Length; i++)
                melPoints[i] = melMin + i * (melMax - melMin) / (nMels + 1);

            double[] freqPoints = melPoints.Select(SlaneyMelToFreq).ToArray();

            // FFT bin frequencies
            double[] fftFreqs = new double[nFft];
            for (int i = 0; i < nFft; i++)
                fftFreqs[i] = i * (double)SampleRate / FFTSize;

            float[][] filters = new float[nMels][];
            for (int m = 0; m < nMels; m++)
            {
                filters[m] = new float[nFft];
                double fLow = freqPoints[m];
                double fCenter = freqPoints[m + 1];
                double fHigh = freqPoints[m + 2];

                for (int k = 0; k < nFft; k++)
                {
                    double f = fftFreqs[k];
                    if (f >= fLow && f <= fCenter && fCenter > fLow)
                        filters[m][k] = (float)((f - fLow) / (fCenter - fLow));
                    else if (f > fCenter && f <= fHigh && fHigh > fCenter)
                        filters[m][k] = (float)((fHigh - f) / (fHigh - fCenter));
                }

                // Slaney area normalization: divide by filter width in Hz
                double enorm = 2.0 / (freqPoints[m + 2] - freqPoints[m]);
                for (int k = 0; k < nFft; k++)
                    filters[m][k] *= (float)enorm;
            }

            return filters;
        }

        private Complex[] FastFourierTransform(float[] input)
        {
            var data = input.Select(v => new Complex(v, 0)).ToArray();
            // Whisper's torch.stft uses unnormalized FFT (no 1/N or 1/sqrt(N) scaling).
            // FourierOptions.NoScaling matches this behavior.
            Fourier.Forward(data, FourierOptions.NoScaling);
            return data;
        }

        private void UpdateWhisperProgress(double? value, IProgress<double>? progress)
        {
            CurrentWhisperProgress = value;
            if (value.HasValue)
            {
                progress?.Report(value.Value);
            }
        }

        private Dictionary<int, string>? _tokenIdToString;
        private Dictionary<string, int>? _tokenStringToId;
        private Dictionary<char, byte>? _byteDecoder;

        private void EnsureTokenizerLoaded()
        {
            if (_tokenIdToString != null && _tokenStringToId != null && _byteDecoder != null)
            {
                return;
            }

            if (CurrentModel == null || string.IsNullOrWhiteSpace(CurrentModel.TokenizerPath) || !File.Exists(CurrentModel.TokenizerPath))
            {
                _tokenIdToString = new Dictionary<int, string>();
                _tokenStringToId = new Dictionary<string, int>(StringComparer.Ordinal);
                _byteDecoder = BuildByteDecoder();
                return;
            }

            using var stream = File.OpenRead(CurrentModel.TokenizerPath);
            using var doc = JsonDocument.Parse(stream);

            var vocab = new Dictionary<int, string>();
            var vocabInverse = new Dictionary<string, int>(StringComparer.Ordinal);
            if (doc.RootElement.TryGetProperty("model", out var modelElement) &&
                modelElement.TryGetProperty("vocab", out var vocabElement))
            {
                foreach (var prop in vocabElement.EnumerateObject())
                {
                    if (prop.Value.TryGetInt32(out var id))
                    {
                        vocab[id] = prop.Name;
                        vocabInverse[prop.Name] = id;
                    }
                }
            }

            // Also read added_tokens (special tokens like <|startoftranscript|>, <|translate|>, etc.)
            if (doc.RootElement.TryGetProperty("added_tokens", out var addedTokensElement) &&
                addedTokensElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var tokenEntry in addedTokensElement.EnumerateArray())
                {
                    if (tokenEntry.TryGetProperty("id", out var idElement) &&
                        idElement.TryGetInt32(out var tokenId) &&
                        tokenEntry.TryGetProperty("content", out var contentElement))
                    {
                        var content = contentElement.GetString();
                        if (!string.IsNullOrEmpty(content))
                        {
                            vocab[tokenId] = content;
                            vocabInverse[content] = tokenId;
                        }
                    }
                }
            }

            _tokenIdToString = vocab;
            _tokenStringToId = vocabInverse;
            _byteDecoder = BuildByteDecoder();
        }

        private int GetTokenIdOrDefault(string token, int fallback)
        {
            if (_tokenStringToId == null)
            {
                return fallback;
            }

            if (_tokenStringToId.TryGetValue(token, out var id))
            {
                return id;
            }

            return fallback;
        }

        private string DecodeTokens(IEnumerable<int> tokens)
        {
            if (_tokenIdToString == null || _byteDecoder == null)
            {
                return string.Empty;
            }

            var sb = new StringBuilder();
            foreach (var token in tokens)
            {
                if (_tokenIdToString.TryGetValue(token, out var piece))
                {
                    if (piece.StartsWith("<|", StringComparison.Ordinal) && piece.EndsWith("|>", StringComparison.Ordinal))
                    {
                        continue;
                    }
                    sb.Append(piece);
                }
            }

            return DecodeByteLevel(sb.ToString());
        }

        private string DecodeByteLevel(string text)
        {
            if (_byteDecoder == null)
            {
                return text;
            }

            var bytes = new List<byte>(text.Length);
            foreach (var ch in text)
            {
                if (_byteDecoder.TryGetValue(ch, out var b))
                {
                    bytes.Add(b);
                }
            }
            return Encoding.UTF8.GetString(bytes.ToArray());
        }

        private static Dictionary<char, byte> BuildByteDecoder()
        {
            var bs = new List<int>();
            for (int i = (int)'!'; i <= (int)'~'; i++) bs.Add(i);
            for (int i = 0xA1; i <= 0xAC; i++) bs.Add(i);
            for (int i = 0xAE; i <= 0xFF; i++) bs.Add(i);

            var cs = bs.Select(b => (char)b).ToList();
            int n = 0;
            for (int b = 0; b < 256; b++)
            {
                if (!bs.Contains(b))
                {
                    bs.Add(b);
                    cs.Add((char)(256 + n));
                    n++;
                }
            }

            var dict = new Dictionary<char, byte>();
            for (int i = 0; i < bs.Count; i++)
            {
                dict[cs[i]] = (byte)bs[i];
            }
            return dict;
        }
    }
}
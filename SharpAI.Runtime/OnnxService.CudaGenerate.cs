using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.HighPerformance;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SharpAI.Core;

namespace SharpAI.Runtime
{
    public partial class OnnxService
    {
        public double? CurrentWhisperProgress { get; private set; }

        private const int Channels = 1;
        private int SampleRate => WhisperFeatureExtractor.SampleRate;
        private int SamplesPerChunk => WhisperFeatureExtractor.ChunkLengthSamples;
        private const int MaxTokens = 448;

        // Silence detection: minimum RMS to consider a chunk as containing speech
        private const float MinChunkRms = 0.001f;

        // Repetition penalty (multiplicative, HuggingFace-style)
        private const float RepetitionPenalty = 2.0f;
        private const int RepetitionWindow = 15;

        // N-gram loop detection: abort chunk if last N tokens repeat the previous N
        private const int LoopNgramSize = 3;
        private const int LoopHistorySize = 8; // minimum tokens before checking
        // Minimum number of content tokens to accept EOT for a chunk
        private const int MinAcceptTokensBeforeEot = 3;
        // Sampling parameters for initial steps to avoid getting stuck on symbol tokens
        private const int InitialSamplingTopK = 50;
        private const float InitialSamplingTemperature = 0.8f;
        private static readonly Random _rng = new();

        public async Task<string?> TranscribeAsync(AudioObj audio, string? language = null, bool taskTranslate = false, bool useTimestamps = false, IProgress<double>? progress = null, CancellationToken ct = default)
        {
            var results = new List<string>();
            await foreach (var segment in this.TranscribeStreamAsync(audio, language, taskTranslate, useTimestamps, progress, ct))
            {
                results.Add(segment);
            }
            return string.Join("", results);
        }

        public async IAsyncEnumerable<string> TranscribeStreamAsync(AudioObj audio, string? language = null, bool taskTranslate = false, bool useTimestamps = false, IProgress<double>? progress = null, [EnumeratorCancellation] CancellationToken ct = default)
        {
            if (!this.IsInitialized || this._encoderSession == null || this._decoderSession == null || this._tokenizer == null || this.TokenMap == null)
            {
                await StaticLogger.LogAsync("Model or Tokenizer not initialized.");
                yield break;
            }

            await StaticLogger.LogAsync($"TranscribeStreamAsync: lang={language ?? "(auto)"}, translate={taskTranslate}, timestamps={useTimestamps}");
            await StaticLogger.LogAsync($"TokenMap: SOT={this.TokenMap.Sot}, EOT={this.TokenMap.Eot}, Transcribe={this.TokenMap.Transcribe}, Translate={this.TokenMap.Translate}, NoTimestamps={this.TokenMap.NoTimestamps}, En={this.TokenMap.English}");

            this.UpdateWhisperProgress(0, progress);

            try
            {
                // Audio preparation
                if (audio.Channels != Channels) await audio.RechannelAsync(Channels);
                if (audio.SampleRate != SampleRate) await audio.ResampleAsync(SampleRate);

                float[] audioData = audio.Data;
                int totalSamples = audioData.Length;
                int position = 0;

                await StaticLogger.LogAsync($"Audio: {totalSamples} samples, {totalSamples / (float)SampleRate:F1}s, SampleRate={SampleRate}, ChunkSamples={SamplesPerChunk}");

                // Pre-compute decoder metadata
                var cacheInputNames = _decoderSession.InputMetadata.Keys.Where(k => k.StartsWith("past_key_values")).ToList();
                bool hasCacheBranch = _decoderSession.InputMetadata.ContainsKey("use_cache_branch");
                await StaticLogger.LogAsync($"Decoder: {cacheInputNames.Count} cache inputs, use_cache_branch={hasCacheBranch}");

                int chunkIdx = 0;
                while (position < totalSamples && !ct.IsCancellationRequested)
                {
                    int lengthToTake = Math.Min(totalSamples - position, SamplesPerChunk);
                    float[] chunk = new float[SamplesPerChunk]; // zero-padded to full chunk size
                    Array.Copy(audioData, position, chunk, 0, lengthToTake);

                    // RMS silence check
                    float sumSq = 0;
                    for (int i = 0; i < lengthToTake; i++) sumSq += chunk[i] * chunk[i];
                    float rms = lengthToTake > 0 ? (float)Math.Sqrt(sumSq / lengthToTake) : 0f;

                    await StaticLogger.LogAsync($"Chunk {chunkIdx}: pos={position}/{totalSamples}, samples={lengthToTake}/{SamplesPerChunk}, rms={rms:F6}");

                    if (rms < MinChunkRms)
                    {
                        await StaticLogger.LogAsync($"Chunk {chunkIdx}: silence (rms={rms:F6} < {MinChunkRms}), skipping.");
                        position += SamplesPerChunk;
                        chunkIdx++;
                        this.UpdateWhisperProgress((double)Math.Min(position, totalSamples) / totalSamples, progress);
                        continue;
                    }

                    // 1. Mel spectrogram
                    var swFeat = System.Diagnostics.Stopwatch.StartNew();
                    var melTensor = WhisperFeatureExtractor.ComputeLogMelSpectrogram(chunk);
                    swFeat.Stop();
                    await StaticLogger.LogAsync($"Chunk {chunkIdx}: mel extraction {swFeat.ElapsedMilliseconds}ms, dims=[{string.Join(',', melTensor.Dimensions.ToArray())}]");

                    // Basic mel tensor validation
                    try
                    {
                        var melArr = melTensor.ToArray();
                        bool anyNaN = melArr.Any(float.IsNaN);
                        bool anyInf = melArr.Any(float.IsInfinity);
                        float minF = melArr.Min();
                        float maxF = melArr.Max();
                        double meanF = melArr.Length > 0 ? melArr.Average() : 0.0;
                        await StaticLogger.LogAsync($"Chunk {chunkIdx}: mel stats min={minF:F6} max={maxF:F6} mean={meanF:F6} NaN={anyNaN} Inf={anyInf}");
                        if (anyNaN || anyInf || float.IsNaN(minF) || float.IsNaN(maxF))
                        {
                            await StaticLogger.LogAsync($"Chunk {chunkIdx}: mel tensor invalid (NaN/Inf). Skipping chunk.");
                            position += SamplesPerChunk;
                            chunkIdx++;
                            this.UpdateWhisperProgress((double)Math.Min(position, totalSamples) / totalSamples, progress);
                            continue;
                        }
                        if (Math.Abs(maxF - minF) < 1e-6f)
                        {
                            await StaticLogger.LogAsync($"Chunk {chunkIdx}: mel tensor collapsed (min==max). Skipping chunk.");
                            position += SamplesPerChunk;
                            chunkIdx++;
                            this.UpdateWhisperProgress((double)Math.Min(position, totalSamples) / totalSamples, progress);
                            continue;
                        }
                    }
                    catch (Exception ex)
                    {
                        await StaticLogger.LogAsync($"Chunk {chunkIdx}: mel validation failed: {ex.Message}");
                    }

                    // 2. Encoder
                    var swEnc = System.Diagnostics.Stopwatch.StartNew();
                    DenseTensor<float> lastHiddenState;
                    try
                    {
                        using var encoderResults = this._encoderSession.Run(new[] { NamedOnnxValue.CreateFromTensor("input_features", melTensor) });
                        lastHiddenState = encoderResults.First(x => x.Name == "last_hidden_state").AsTensor<float>().ToDenseTensor();
                        swEnc.Stop();
                        await StaticLogger.LogAsync($"Chunk {chunkIdx}: encoder {swEnc.ElapsedMilliseconds}ms, hidden=[{string.Join(',', lastHiddenState.Dimensions.ToArray())}]");
                    }
                    catch (Exception ex)
                    {
                        swEnc.Stop();
                        await StaticLogger.LogAsync($"Chunk {chunkIdx}: encoder FAILED: {ex.Message}");
                        position += SamplesPerChunk;
                        chunkIdx++;
                        continue;
                    }

                    // 3. Build initial prompt tokens
                    var tokens = new List<long> { this.TokenMap.Sot };
                    string lang = string.IsNullOrEmpty(language) ? "en" : language;
                    tokens.Add(this.TokenMap.GetLanguageId(lang));
                    tokens.Add(taskTranslate ? this.TokenMap.Translate : this.TokenMap.Transcribe);
                    if (!useTimestamps) tokens.Add(this.TokenMap.NoTimestamps);

                    await StaticLogger.LogAsync($"Chunk {chunkIdx}: prompt tokens=[{string.Join(',', tokens)}]");

                    // 4. Decoder loop
                    var kvCache = new Dictionary<string, DenseTensor<float>>();
                    bool chunkFinished = false;
                    var recentTokenIds = new List<int>();
                    var perChunkBanned = new Dictionary<int, int>();

                    while (!chunkFinished && tokens.Count < MaxTokens && !ct.IsCancellationRequested)
                    {
                        string? textToYield = null;
                        try
                        {
                            bool useCache = kvCache.Count > 0;
                            var decoderInputs = new List<NamedOnnxValue>();

                            // input_ids: all tokens on first run, only last token when using cache
                            long[] currentIds = useCache ? new[] { tokens.Last() } : tokens.ToArray();
                            decoderInputs.Add(NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(currentIds, new[] { 1, currentIds.Length })));
                            decoderInputs.Add(NamedOnnxValue.CreateFromTensor("encoder_hidden_states", lastHiddenState));

                            if (hasCacheBranch)
                                decoderInputs.Add(NamedOnnxValue.CreateFromTensor("use_cache_branch", new DenseTensor<bool>(new[] { useCache }, new[] { 1 })));

                            foreach (var name in cacheInputNames)
                            {
                                if (useCache && kvCache.TryGetValue(name, out var cached))
                                    decoderInputs.Add(NamedOnnxValue.CreateFromTensor(name, cached));
                                else
                                    decoderInputs.Add(NamedOnnxValue.CreateFromTensor(name, new DenseTensor<float>(_decoderCacheShapes![name])));
                            }

                            var swDec = System.Diagnostics.Stopwatch.StartNew();
                            using var decoderResults = this._decoderSession.Run(decoderInputs);
                            swDec.Stop();

                            var logits = decoderResults.First(x => x.Name == "logits").AsTensor<float>();
                            int lastIdx = logits.Dimensions[1] - 1;
                            int vocabSize = logits.Dimensions[2];

                            // Copy logits to mutable array for applying penalties
                            float[] scores = new float[vocabSize];
                            for (int v = 0; v < vocabSize; v++)
                                scores[v] = logits[0, lastIdx, v];

                            // *** CRITICAL: Suppress ALL special tokens except EOT ***
                            // Whisper special tokens (SOT, language, translate, transcribe, timestamps, etc.)
                            // must NOT appear in the generated content. Only EOT (end-of-text) is allowed
                            // to signal end of transcription.
                            // Special tokens occupy IDs >= SOT (50258+). EOT is at 50257.
                            int suppressFrom = this.TokenMap.Eot + 1; // everything after EOT
                            for (int v = suppressFrom; v < vocabSize; v++)
                            {
                                scores[v] = float.NegativeInfinity;
                            }

                            // Apply repetition penalty (multiplicative, like HuggingFace)
                            var recentSet = new HashSet<int>(recentTokenIds.Skip(Math.Max(0, recentTokenIds.Count - RepetitionWindow)));
                            foreach (int rid in recentSet)
                            {
                                if (rid >= 0 && rid < suppressFrom) // only penalize content tokens
                                {
                                    if (scores[rid] > 0)
                                        scores[rid] /= RepetitionPenalty;
                                    else
                                        scores[rid] *= RepetitionPenalty;
                                }
                            }

                            // Additional heuristic: blacklist top-k candidates that decode to non-alphanumeric/punctuation garbage
                            try
                            {
                                int topKfilter = Math.Min(64, vocabSize);
                                var cand = Enumerable.Range(0, vocabSize).Select(v => (id: v, score: scores[v])).OrderByDescending(x => x.score).Take(topKfilter).ToArray();
                                int masked = 0;
                                int maskCap = 16; // allow more masking but cap it
                                foreach (var c in cand)
                                {
                                    if (masked >= maskCap) break;
                                    if (c.id >= suppressFrom) continue; // already suppressed
                                    string txt = string.Empty;
                                    try { txt = this._tokenizer.Decode(new[] { (int)c.id }); } catch { txt = string.Empty; }
                                    if (string.IsNullOrEmpty(txt)) continue;
                                    var normalized = txt.Replace('Ġ', ' ').Trim();
                                    int len = normalized.Length;
                                    if (len == 0) continue;
                                    int alpha = normalized.Count(ch => char.IsLetterOrDigit(ch));
                                    int printable = normalized.Count(ch => !char.IsControl(ch));
                                    double alphaRatio = (double)alpha / len;
                                    double printableRatio = (double)printable / len;

                                    bool nonAsciiOnly = normalized.All(ch => ch > 127 && !char.IsLetterOrDigit(ch) && !char.IsPunctuation(ch));

                                    // Mask tokens that are very short and contain no alnum chars, or are non-ASCII symbols, or contain replacement char
                                    if ((len <= 2 && alpha == 0) || nonAsciiOnly || normalized.Any(ch => ch == '\uFFFD'))
                                    {
                                        scores[c.id] = float.NegativeInfinity;
                                        masked++;
                                    }
                                }
                                if (masked > 0) await StaticLogger.LogAsync($"Applied top-k filter: masked {masked} candidate tokens.");
                            }
                            catch { }

                            // Find best token (greedy on penalized scores)
                            int bestTokenId = 0;
                            float maxScore = float.NegativeInfinity;
                            for (int v = 0; v < vocabSize; v++)
                            {
                                if (scores[v] > maxScore) { maxScore = scores[v]; bestTokenId = v; }
                            }

                            // If early in generation or greedy candidate is low-quality (few alnum chars), use sampling to avoid getting stuck on symbol tokens
                            int genStep = Math.Max(0, tokens.Count - 4);
                            bool doSampling = genStep < 3; // always sample for first 3 generated tokens
                            if (!doSampling)
                            {
                                try
                                {
                                    var greedyTxt = this._tokenizer.Decode(new[] { (int)bestTokenId })?.Replace('Ġ', ' ').Trim() ?? string.Empty;
                                    int gLen = greedyTxt.Length;
                                    int gAlpha = greedyTxt.Count(ch => char.IsLetterOrDigit(ch));
                                    double gAlphaRatio = gLen > 0 ? (double)gAlpha / gLen : 0.0;
                                    if (gLen == 0 || gAlphaRatio < 0.25) doSampling = true;
                                }
                                catch { doSampling = false; }
                            }

                            if (doSampling)
                            {
                                try
                                {
                                    int k = Math.Min(InitialSamplingTopK, vocabSize);
                                    var candidates = Enumerable.Range(0, vocabSize).Select(v => (id: v, score: scores[v])).Where(x => !float.IsNegativeInfinity(x.score)).OrderByDescending(x => x.score).Take(k).ToArray();
                                    if (candidates.Length > 0)
                                    {
                                        // apply temperature and sample
                                        double[] exps = new double[candidates.Length];
                                        double sum = 0.0;
                                        for (int i = 0; i < candidates.Length; i++)
                                        {
                                            var s = candidates[i].score / Math.Max(1e-6, InitialSamplingTemperature);
                                            var e = Math.Exp(s);
                                            exps[i] = e; sum += e;
                                        }
                                        double r = _rng.NextDouble() * sum;
                                        double acc = 0.0;
                                        int chosen = candidates.Last().id;
                                        for (int i = 0; i < candidates.Length; i++)
                                        {
                                            acc += exps[i];
                                            if (r <= acc) { chosen = candidates[i].id; break; }
                                        }
                                        bestTokenId = chosen;
                                        await StaticLogger.LogAsync($"Hybrid sampling chosen token id={bestTokenId} at step={genStep}");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    await StaticLogger.LogAsync($"Sampling failed: {ex.Message}");
                                }
                            }

                            // Update KV cache
                            foreach (var outVal in decoderResults.Where(x => x.Name.StartsWith("present")))
                                kvCache[outVal.Name.Replace("present", "past_key_values")] = outVal.AsTensor<float>().ToDenseTensor();

                            // compute top-2 scores to make EOT reselect decision
                            int top1 = -1, top2 = -1; float s1 = float.NegativeInfinity, s2 = float.NegativeInfinity;
                            for (int v = 0; v < vocabSize; v++)
                            {
                                var sc = scores[v];
                                if (sc > s1) { s2 = s1; top2 = top1; s1 = sc; top1 = v; }
                                else if (sc > s2) { s2 = sc; top2 = v; }
                            }
                            float eotScore = (this.TokenMap.Eot >= 0 && this.TokenMap.Eot < vocabSize) ? scores[this.TokenMap.Eot] : float.NegativeInfinity;
                            float nextBestScore = (top1 == this.TokenMap.Eot) ? s2 : s1;
                            int nextBestId = (top1 == this.TokenMap.Eot) ? top2 : top1;

                            // Log top-5 for first 5 and every 20th step
                            int step = tokens.Count - 4; // prompt has ~4 tokens
                            if (step <= 5 || step % 20 == 0)
                            {
                                try
                                {
                                    var top5 = Enumerable.Range(0, vocabSize)
                                        .Select(v => (id: v, score: scores[v]))
                                        .OrderByDescending(x => x.score)
                                        .Take(5)
                                        .Select(t =>
                                        {
                                            string txt;
                                            try { txt = this._tokenizer.Decode(new[] { t.id }); } catch { txt = "?"; }
                                            return $"{t.id}:{t.score:F2}:'{txt}'";
                                        });
                                    await StaticLogger.LogAsync($"  Step {step}: {swDec.ElapsedMilliseconds}ms top5=[{string.Join(", ", top5)}] chosen={bestTokenId}");
                                }
                                catch { }
                            }

                            if (bestTokenId == this.TokenMap.Eot)
                            {
                                if (recentTokenIds.Count < MinAcceptTokensBeforeEot)
                                {
                                    // Look for a single reasonable non-symbol candidate; require at least 40% alphanumeric and length>=2
                                    int newBest = -1; float newMax = float.NegativeInfinity;
                                    for (int v = 0; v < vocabSize; v++)
                                    {
                                        if (scores[v] > newMax) { newMax = scores[v]; newBest = v; }
                                    }

                                    bool acceptReselect = false;
                                    if (newBest > 0 && newMax > float.NegativeInfinity)
                                    {
                                        try
                                        {
                                            var candTxt = this._tokenizer.Decode(new[] { (int)newBest })?.Replace('Ġ', ' ').Trim() ?? string.Empty;
                                            int len = candTxt.Length;
                                            int alpha = candTxt.Count(ch => char.IsLetterOrDigit(ch));
                                            double alphaRatio = len > 0 ? (double)alpha / len : 0.0;
                                            if (len >= 2 && alphaRatio >= 0.4 && !perChunkBanned.ContainsKey(newBest))
                                            {
                                                acceptReselect = true;
                                            }
                                        }
                                        catch { acceptReselect = false; }
                                    }

                                    if (acceptReselect)
                                    {
                                        bestTokenId = newBest;
                                        await StaticLogger.LogAsync($"  Step {step}: Reselected token id={bestTokenId} after suppressing early EOT (quality pass).");
                                    }
                                    else
                                    {
                                        await StaticLogger.LogAsync($"  Step {step}: No viable non-EOT candidate found (best id={newBest}), accepting EOT and aborting chunk.");
                                        chunkFinished = true;
                                    }
                                }
                                else
                                {
                                    await StaticLogger.LogAsync($"  Step {step}: EOT reached, total tokens={tokens.Count}");
                                    chunkFinished = true;
                                }
                            }
                            else
                            {
                                // Avoid repeated selection of short non-alphanumeric tokens (e.g. 'ĳ')
                                var rawText = this._tokenizer.Decode(new[] { bestTokenId });
                                var cleanWord = (rawText ?? "").Replace('Ġ', ' ').Trim();
                                bool isSymbolLike = false;
                                try
                                {
                                    int len = cleanWord.Length;
                                    int alpha = cleanWord.Count(ch => char.IsLetterOrDigit(ch));
                                    double alphaRatio = len > 0 ? (double)alpha / len : 0.0;
                                    if (len <= 3 && alphaRatio < 0.25) isSymbolLike = true;
                                }
                                catch { isSymbolLike = false; }

                                if (isSymbolLike)
                                {
                                    // If the same symbol was just emitted, try to reselect another candidate
                                    bool reselected = false;
                                    if (recentTokenIds.Count > 0 && recentTokenIds.Last() == bestTokenId)
                                    {
                                        // mask this id for reselection
                                        scores[bestTokenId] = float.NegativeInfinity;
                                        // pick next best up to a few attempts
                                        for (int attempt = 0; attempt < 3; attempt++)
                                        {
                                            int candId = -1; float candScore = float.NegativeInfinity;
                                            for (int v = 0; v < vocabSize; v++) if (scores[v] > candScore) { candScore = scores[v]; candId = v; }
                                            if (candId <= 0 || float.IsNegativeInfinity(candScore)) break;
                                            string candTxt = string.Empty;
                                            try { candTxt = this._tokenizer.Decode(new[] { candId })?.Replace('Ġ', ' ').Trim() ?? string.Empty; } catch { candTxt = string.Empty; }
                                            int clen = candTxt.Length; int calpha = candTxt.Count(ch => char.IsLetterOrDigit(ch));
                                            double cratio = clen > 0 ? (double)calpha / clen : 0.0;
                                            if (clen >= 2 && cratio >= 0.35 && !perChunkBanned.ContainsKey(candId))
                                            {
                                                bestTokenId = candId; reselected = true; await StaticLogger.LogAsync($"  Step {step}: Reselected non-symbol token id={bestTokenId} to avoid repeats."); break;
                                            }
                                            // otherwise suppress this candidate and continue
                                            scores[candId] = float.NegativeInfinity;
                                        }
                                    }

                                    if (!reselected)
                                    {
                                        // allow one emission but ban further repeats of this id
                                        perChunkBanned[bestTokenId] = perChunkBanned.TryGetValue(bestTokenId, out var c) ? c + 1 : 1;
                                        await StaticLogger.LogAsync($"  Step {step}: Emitting symbol-like token id={bestTokenId} (banned count={perChunkBanned[bestTokenId]}).\n");
                                    }
                                }

                                tokens.Add(bestTokenId);
                                recentTokenIds.Add(bestTokenId);

                                // N-gram loop detection: check multiple n-gram sizes (3,4,5,6)
                                if (recentTokenIds.Count >= LoopHistorySize)
                                {
                                    bool loopDetected = false;
                                    for (int ng = LoopNgramSize; ng <= Math.Min(6, recentTokenIds.Count / 2); ng++)
                                    {
                                        if (recentTokenIds.Count < ng * 2) continue;
                                        var last = recentTokenIds.Skip(recentTokenIds.Count - ng).Take(ng).ToList();
                                        var prev = recentTokenIds.Skip(recentTokenIds.Count - ng * 2).Take(ng).ToList();
                                        if (last.SequenceEqual(prev))
                                        {
                                            await StaticLogger.LogAsync($"  Step {step}: {ng}-gram loop detected [{string.Join(',', last)}], aborting chunk.");
                                            chunkFinished = true;
                                            loopDetected = true;
                                            break;
                                        }
                                    }
                                    if (loopDetected) continue;
                                }

                                textToYield = (rawText ?? "").Replace('Ġ', ' ');
                            }
                        }
                        catch (Exception ex)
                        {
                            await StaticLogger.LogAsync($"Chunk {chunkIdx}: decoder step FAILED: {ex.Message}");
                            kvCache.Clear();
                            chunkFinished = true;
                        }

                        if (textToYield != null)
                            yield return textToYield;
                    }

                    await StaticLogger.LogAsync($"Chunk {chunkIdx}: finished, generated {recentTokenIds.Count} content tokens.");

                    position += SamplesPerChunk;
                    chunkIdx++;
                    this.UpdateWhisperProgress((double)Math.Min(position, totalSamples) / totalSamples, progress);
                }

                this.UpdateWhisperProgress(1.0, progress);
            }
            finally
            {
                this.UpdateWhisperProgress(null, progress);
            }
        }

        private void UpdateWhisperProgress(double? value, IProgress<double>? progress)
        {
            this.CurrentWhisperProgress = value;
            if (value.HasValue) progress?.Report(value.Value);
        }
    }
}
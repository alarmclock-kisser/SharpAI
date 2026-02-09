using System;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime.Tensors;
using System.IO;
using System.Text.Json;
using SharpAI.Core;

namespace SharpAI.Runtime
{
    public static class WhisperFeatureExtractor
    {
        // Configurable parameters (defaults match Whisper large-v3)
        public static int SampleRate { get; private set; } = 16000;
        public static int N_FFT { get; private set; } = 400;
        public static int HopLength { get; private set; } = 160;
        public static int NMels { get; private set; } = 80;
        public static int ChunkLengthSamples { get; private set; } = 480000; // 30s * 16000
        public static int NFrames { get; private set; } = 3000;             // ChunkLength / HopLength

        // Cache for mel filterbank and Hann window
        private static float[,]? _melFilters;
        private static float[]? _hannWindow;

        /// <summary>
        /// Configures the feature extractor from a preprocessor_config.json.
        /// </summary>
        public static void ConfigureFromPreprocessor(string? configPath)
        {
            if (string.IsNullOrEmpty(configPath) || !File.Exists(configPath))
            {
                InitializeFilters();
                return;
            }

            try
            {
                var txt = File.ReadAllText(configPath);
                using var doc = JsonDocument.Parse(txt);
                var root = doc.RootElement;

                int GetInt(params string[] keys)
                {
                    foreach (var k in keys)
                    {
                        if (root.TryGetProperty(k, out var prop) && prop.ValueKind == JsonValueKind.Number)
                        {
                            if (prop.TryGetInt32(out int v)) return v;
                        }
                    }
                    return -1;
                }

                var sr = GetInt("sampling_rate", "sample_rate", "sampleRate", "sr");
                if (sr > 0) SampleRate = sr;

                var n_fft = GetInt("n_fft", "nFft");
                if (n_fft > 0) N_FFT = n_fft;

                var hop = GetInt("hop_length", "hopLength", "hop");
                if (hop > 0) HopLength = hop;

                var n_mels = GetInt("feature_size", "n_mels", "nMels", "num_mel_bins");
                if (n_mels > 0) NMels = n_mels;

                var chunk = GetInt("chunk_length");
                if (chunk > 0)
                {
                    // chunk_length in preprocessor_config is in SECONDS, not samples
                    ChunkLengthSamples = chunk * SampleRate;
                }

                var n_frames = GetInt("n_frames", "nFrames", "nb_max_frames");
                if (n_frames > 0) NFrames = n_frames;
                else if (HopLength > 0)
                {
                    NFrames = Math.Max(1, ChunkLengthSamples / HopLength);
                }

                _melFilters = null;
                _hannWindow = null;
                InitializeFilters();
            }
            catch
            {
                _melFilters = null;
                _hannWindow = null;
                InitializeFilters();
            }
        }

        /// <summary>
        /// Sets parameters at runtime and reinitializes filters/window.
        /// </summary>
        public static void SetParameters(int? nMels = null, int? nFrames = null, int? nFft = null, int? hopLength = null, int? sampleRate = null, int? chunkLengthSamples = null)
        {
            if (nMels.HasValue && nMels.Value > 0) NMels = nMels.Value;
            if (nFrames.HasValue && nFrames.Value > 0) NFrames = nFrames.Value;
            if (nFft.HasValue && nFft.Value > 0) N_FFT = nFft.Value;
            if (hopLength.HasValue && hopLength.Value > 0) HopLength = hopLength.Value;
            if (sampleRate.HasValue && sampleRate.Value > 0) SampleRate = sampleRate.Value;
            if (chunkLengthSamples.HasValue && chunkLengthSamples.Value > 0) ChunkLengthSamples = chunkLengthSamples.Value;

            if (!chunkLengthSamples.HasValue && nFrames.HasValue && nFrames.Value > 0 && HopLength > 0)
            {
                ChunkLengthSamples = nFrames.Value * HopLength;
            }

            _melFilters = null;
            _hannWindow = null;
            InitializeFilters();
        }

        /// <summary>
        /// Computes the log-mel spectrogram matching OpenAI Whisper's implementation.
        /// Uses center=True STFT with reflection padding, Slaney mel scale, Slaney normalization,
        /// and dynamic log clamping (max - 8.0).
        /// Output shape: [1, NMels, NFrames]
        /// </summary>
        public static DenseTensor<float> ComputeLogMelSpectrogram(float[] audio)
        {
            if (_melFilters == null || _hannWindow == null)
                InitializeFilters();

            // 1. Reflection padding (center=True): pad N_FFT/2 on each side
            int pad = N_FFT / 2;
            int paddedLen = audio.Length + 2 * pad;
            float[] padded = new float[paddedLen];

            // Left reflection padding
            for (int i = 0; i < pad; i++)
            {
                int reflectIdx = pad - i; // reflect around index 0
                padded[i] = (reflectIdx < audio.Length) ? audio[reflectIdx] : 0f;
            }

            // Copy original audio
            Array.Copy(audio, 0, padded, pad, audio.Length);

            // Right reflection padding
            for (int i = 0; i < pad; i++)
            {
                int reflectIdx = audio.Length - 2 - i; // reflect around last sample
                padded[pad + audio.Length + i] = (reflectIdx >= 0) ? audio[reflectIdx] : 0f;
            }

            // 2. Compute STFT frames
            // Number of STFT frames (Whisper drops the last frame)
            int totalFrames = (paddedLen - N_FFT) / HopLength + 1;
            int stftFrames = totalFrames - 1; // drop last frame like Whisper

            // Compute power spectrogram in parallel
            int freqBins = N_FFT / 2 + 1;
            float[][] powerSpec = new float[stftFrames][];

            // Pre-compute DFT twiddle factors for exact N_FFT-point transform
            // (NOT padded to power-of-2, which would shift all frequency bins!)
            int nfft = N_FFT;
            double[] cosTable = new double[freqBins * nfft];
            double[] sinTable = new double[freqBins * nfft];
            for (int k = 0; k < freqBins; k++)
            {
                double baseAngle = -2.0 * Math.PI * k / nfft;
                for (int t = 0; t < nfft; t++)
                {
                    double angle = baseAngle * t;
                    cosTable[k * nfft + t] = Math.Cos(angle);
                    sinTable[k * nfft + t] = Math.Sin(angle);
                }
            }

            Parallel.For(0, stftFrames, frameIdx =>
            {
                int startSample = frameIdx * HopLength;

                // Apply window directly to float samples (no Complex needed)
                float[] windowed = new float[nfft];
                for (int i = 0; i < nfft; i++)
                {
                    int sampleIdx = startSample + i;
                    float sample = (sampleIdx < paddedLen) ? padded[sampleIdx] : 0f;
                    windowed[i] = sample * _hannWindow![i];
                }

                // Exact N_FFT-point DFT for first freqBins bins
                // This matches torch.stft(n_fft=400) which does NOT pad to 512
                float[] magnitudes = new float[freqBins];
                for (int k = 0; k < freqBins; k++)
                {
                    double real = 0, imag = 0;
                    int offset = k * nfft;
                    for (int t = 0; t < nfft; t++)
                    {
                        real += windowed[t] * cosTable[offset + t];
                        imag += windowed[t] * sinTable[offset + t];
                    }
                    magnitudes[k] = (float)(real * real + imag * imag);
                }

                powerSpec[frameIdx] = magnitudes;
            });

            // 3. Apply mel filterbank and compute log-mel spectrogram
            int outputFrames = Math.Min(stftFrames, NFrames);
            float[,] logMelSpec = new float[NMels, NFrames];

            // Initialize with silence value (will be overwritten for valid frames)
            float silenceVal = (float)Math.Log10(1e-10);

            for (int m = 0; m < NMels; m++)
            {
                for (int t = 0; t < outputFrames; t++)
                {
                    double melEnergy = 0;
                    var frameMags = powerSpec[t];
                    for (int k = 0; k < freqBins; k++)
                    {
                        melEnergy += _melFilters![m, k] * frameMags[k];
                    }

                    logMelSpec[m, t] = (float)Math.Log10(Math.Max(melEnergy, 1e-10));
                }

                // Fill remaining frames with silence
                for (int t = outputFrames; t < NFrames; t++)
                {
                    logMelSpec[m, t] = silenceVal;
                }
            }

            // 4. Dynamic clamping: clamp to (global_max - 8.0), then normalize
            // This matches OpenAI Whisper: log_spec = torch.clamp(log_spec, min=log_spec.max() - 8.0)
            float globalMax = float.NegativeInfinity;
            for (int m = 0; m < NMels; m++)
                for (int t = 0; t < NFrames; t++)
                    if (logMelSpec[m, t] > globalMax) globalMax = logMelSpec[m, t];

            float clampMin = globalMax - 8.0f;

            // Build output tensor with normalization: (clamped + 4.0) / 4.0
            var tensor = new DenseTensor<float>(new[] { 1, NMels, NFrames });
            // Compute basic stats for logging and detect NaN/Inf
            double sum = 0; long count = 0; float minF = float.MaxValue; float maxF = float.MinValue; bool anyNaN = false; bool anyInf = false;
            for (int m = 0; m < NMels; m++)
            {
                for (int t = 0; t < NFrames; t++)
                {
                    float val = Math.Max(logMelSpec[m, t], clampMin);
                    tensor[0, m, t] = (val + 4.0f) / 4.0f;
                    // stats
                    if (float.IsNaN(val)) anyNaN = true;
                    if (float.IsInfinity(val)) anyInf = true;
                    if (!float.IsNaN(val) && !float.IsInfinity(val))
                    {
                        sum += val; count++;
                        if (val < minF) minF = val;
                        if (val > maxF) maxF = val;
                    }
                }
            }

            try
            {
                var meanF = count > 0 ? sum / count : 0.0;
                StaticLogger.Log($"WhisperFeatureExtractor: mel stats min={minF:F6} max={maxF:F6} mean={meanF:F6} NaN={anyNaN} Inf={anyInf} dims=[1,{NMels},{NFrames}]");
            }
            catch { }

            return tensor;
        }

        // --- Helper Methods ---

        private static void InitializeFilters()
        {
            // 1. Periodic Hann window (matches torch.hann_window)
            _hannWindow = new float[N_FFT];
            for (int i = 0; i < N_FFT; i++)
            {
                _hannWindow[i] = 0.5f * (1.0f - (float)Math.Cos(2.0 * Math.PI * i / N_FFT));
            }

            // 2. Mel filterbank (Slaney mel scale + Slaney normalization, matching librosa defaults)
            _melFilters = CreateMelFilterBank(NMels, N_FFT, SampleRate);
        }

        // --- Slaney Mel Scale (matching librosa defaults) ---

        private static double HzToMel(double hz)
        {
            // Slaney mel scale: linear below 1000 Hz, logarithmic above
            const double fMin = 0.0;
            const double fSp = 200.0 / 3.0;
            const double minLogHz = 1000.0;
            double minLogMel = (minLogHz - fMin) / fSp;
            double logStep = Math.Log(6.4) / 27.0;

            if (hz >= minLogHz)
                return minLogMel + Math.Log(hz / minLogHz) / logStep;
            else
                return (hz - fMin) / fSp;
        }

        private static double MelToHz(double mel)
        {
            const double fMin = 0.0;
            const double fSp = 200.0 / 3.0;
            const double minLogHz = 1000.0;
            double minLogMel = (minLogHz - fMin) / fSp;
            double logStep = Math.Log(6.4) / 27.0;

            if (mel >= minLogMel)
                return minLogHz * Math.Exp(logStep * (mel - minLogMel));
            else
                return fMin + fSp * mel;
        }

        /// <summary>
        /// Creates mel filterbank matching librosa.filters.mel with Slaney mel scale and Slaney normalization.
        /// This matches OpenAI Whisper's mel_filters() function.
        /// </summary>
        private static float[,] CreateMelFilterBank(int nMels, int nFft, int sampleRate)
        {
            int freqBins = nFft / 2 + 1;
            float[,] weights = new float[nMels, freqBins];

            // FFT frequencies
            double[] fftFreqs = new double[freqBins];
            for (int i = 0; i < freqBins; i++)
            {
                fftFreqs[i] = (double)i * sampleRate / nFft;
            }

            // Mel points: nMels + 2 equally spaced points in mel space
            double minMel = HzToMel(0.0);
            double maxMel = HzToMel(sampleRate / 2.0);

            double[] melPoints = new double[nMels + 2];
            for (int i = 0; i < nMels + 2; i++)
            {
                melPoints[i] = minMel + (maxMel - minMel) * i / (nMels + 1);
            }

            // Convert mel points back to Hz
            double[] hzPoints = new double[nMels + 2];
            for (int i = 0; i < nMels + 2; i++)
            {
                hzPoints[i] = MelToHz(melPoints[i]);
            }

            // Create triangular filters
            for (int i = 0; i < nMels; i++)
            {
                double lower = hzPoints[i];
                double center = hzPoints[i + 1];
                double upper = hzPoints[i + 2];

                for (int j = 0; j < freqBins; j++)
                {
                    double freq = fftFreqs[j];

                    if (freq >= lower && freq <= center && center > lower)
                    {
                        weights[i, j] = (float)((freq - lower) / (center - lower));
                    }
                    else if (freq > center && freq <= upper && upper > center)
                    {
                        weights[i, j] = (float)((upper - freq) / (upper - center));
                    }
                }

                // Slaney normalization: divide each filter by its bandwidth (in Hz)
                double bandwidth = hzPoints[i + 2] - hzPoints[i];
                if (bandwidth > 0)
                {
                    float norm = (float)(2.0 / bandwidth);
                    for (int j = 0; j < freqBins; j++)
                    {
                        weights[i, j] *= norm;
                    }
                }
            }

            return weights;
        }
    }
}
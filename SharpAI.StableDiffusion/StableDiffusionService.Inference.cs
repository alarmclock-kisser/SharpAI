using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using NAudio.Utils;
using SharpAI.Core;

namespace SharpAI.StableDiffusion
{
    public partial class StableDiffusionService
    {
        private async Task<DenseTensor<float>> EncodeTextAsync(int[] textTokens, int[] uncondTokens)
        {
            if (this._textEncoderSession == null)
            {
                await StaticLogger.LogAsync("Text Encoder session is not initialized.");
                throw new InvalidOperationException("Text Encoder session is null.");
            }

            return await Task.Run(async () =>
            {
                var textInput = new DenseTensor<int>(new[] { 1, 77 });
                var uncondInput = new DenseTensor<int>(new[] { 1, 77 });

                for (int i = 0; i < 77; i++)
                {
                    textInput[0, i] = textTokens[i];
                    uncondInput[0, i] = uncondTokens[i];
                }

                var inputsText = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor("input_ids", textInput) };
                var inputsUncond = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor("input_ids", uncondInput) };

                using var resultText = this._textEncoderSession.Run(inputsText);
                using var resultUncond = this._textEncoderSession.Run(inputsUncond);

                // Explizit als DenseTensor behandeln, um auf Buffer zuzugreifen
                var textEmbeddings = resultText.First().AsTensor<float>().ToDenseTensor();
                var uncondEmbeddings = resultUncond.First().AsTensor<float>().ToDenseTensor();

                var combined = new DenseTensor<float>(new[] { 2, 77, 768 });

                // Wir nutzen GetReadOnlySpan() oder Buffer.Span (bei DenseTensor verfügbar)
                var uncondSpan = uncondEmbeddings.Buffer.Span;
                var textSpan = textEmbeddings.Buffer.Span;
                var combinedSpan = combined.Buffer.Span;

                uncondSpan.CopyTo(combinedSpan);
                textSpan.CopyTo(combinedSpan.Slice(uncondSpan.Length));

                return combined;
            });
        }

        private async Task<DenseTensor<float>> RunUnetInferenceAsync(DenseTensor<float> latents, DenseTensor<float> textEmbeddings, int timestep)
        {
            if (this._unetSession == null)
            {
                await StaticLogger.LogAsync("UNet session is not initialized.");
                throw new InvalidOperationException("UNet session is null.");
            }

            return await Task.Run(() =>
            {
                // ÄNDERUNG: Von DenseTensor<long> zu DenseTensor<float>
                // Manche Modelle nutzen den Namen "timestep", andere "t"
                var timestepTensor = new DenseTensor<float>(new[] { (float) timestep }, new[] { 1 });

                var inputs = new List<NamedOnnxValue>
                {
                    // Prüfe ggf. die Namen "sample" vs "latent_model_input" 
                    // und "timestep" vs "t", falls weitere Fehler kommen
                    NamedOnnxValue.CreateFromTensor("sample", latents),
                    NamedOnnxValue.CreateFromTensor("timestep", timestepTensor),
                    NamedOnnxValue.CreateFromTensor("encoder_hidden_states", textEmbeddings)
                };

                using var results = this._unetSession.Run(inputs);
                return results.First().AsTensor<float>().ToDenseTensor();
            });
        }

        private async Task<float[]> DecodeLatentsAsync(DenseTensor<float> latents)
        {
            if (this._vaeDecoderSession == null) throw new InvalidOperationException("VAE Session null");

            return await Task.Run(() =>
            {
                // 1. Skalierung mit dem VAE-Faktor 0.18215f
                var scaledLatents = new DenseTensor<float>(latents.Dimensions);
                var spanIn = latents.Buffer.Span;
                var spanOut = scaledLatents.Buffer.Span;
                for (int i = 0; i < spanIn.Length; i++) spanOut[i] = spanIn[i] / 0.18215f;

                var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor("latent_sample", scaledLatents) };
                using var results = this._vaeDecoderSession.Run(inputs);
                var rawData = results.First().AsTensor<float>().ToArray(); // [3, 512, 512]

                // 2. Umwandlung Planar -> Interleaved
                int pixelCount = 512 * 512;
                float[] interleavedData = new float[pixelCount * 3];

                for (int i = 0; i < pixelCount; i++)
                {
                    // Normalisierung von [-1, 1] auf [0, 1] und Umsortierung der Kanäle
                    // rawData[i] ist Rot, rawData[i + pixelCount] ist Grün, rawData[i + pixelCount * 2] ist Blau
                    interleavedData[i * 3 + 0] = Math.Clamp((rawData[i] / 2.0f) + 0.5f, 0f, 1f);              // R
                    interleavedData[i * 3 + 1] = Math.Clamp((rawData[i + pixelCount] / 2.0f) + 0.5f, 0f, 1f);  // G
                    interleavedData[i * 3 + 2] = Math.Clamp((rawData[i + pixelCount * 2] / 2.0f) + 0.5f, 0f, 1f);// B
                }
                return interleavedData;
            });
        }
    }
}
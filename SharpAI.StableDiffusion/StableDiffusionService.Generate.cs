using Microsoft.ML.OnnxRuntime.Tensors;
using SharpAI.Core;

namespace SharpAI.StableDiffusion
{
    public partial class StableDiffusionService
    {
        public async Task<float[]> GenerateInternalAsync(string prompt, string negativePrompt, int steps, float guidanceScale, long seed, IProgress<double>? progress)
        {
            if (this._config == null) throw new InvalidOperationException("Model config is null.");

            try
            {
                await StaticLogger.LogAsync("Starting generation process...");

                // FIX: Sicherstellen, dass alles geladen ist
                if (this._alphasCumulativeProducts.Length == 0) await LoadSchedulerAsync();
                if (this._vocab.Count == 0) await LoadTokenizerAsync();

                this.PrepareScheduler(steps);

                var promptTokens = await this.TokenizeAsync(prompt);
                var negativeTokens = await this.TokenizeAsync(negativePrompt);
                var textEmbeddings = await this.EncodeTextAsync(promptTokens, negativeTokens);

                var latents = this.PrepareLatents(seed);
                var timesteps = this.CreateTimesteps(steps).ToArray();

                for (int i = 0; i < timesteps.Length; i++)
                {
                    int timestep = timesteps[i];
                    float sigma = this._sigmas[i];

                    await StaticLogger.LogAsync($"Step {i + 1} / {steps} (Sigma: {sigma:F4})");

                    float invMagicNumber = 1.0f / (float) Math.Sqrt(sigma * sigma + 1);
                    var latentModelInput = this.DuplicateLatents(latents);
                    var inputSpan = latentModelInput.Buffer.Span;
                    for (int j = 0; j < inputSpan.Length; j++) inputSpan[j] *= invMagicNumber;

                    var noisePred = await this.RunUnetInferenceAsync(latentModelInput, textEmbeddings, timestep);

                    var noisePredSpan = noisePred.Buffer.Span;
                    int halfLength = noisePredSpan.Length / 2;
                    var guidedNoise = new DenseTensor<float>(new[] { 1, 4, 64, 64 });
                    var guidedSpan = guidedNoise.Buffer.Span;
                    var lSpan = latents.Buffer.Span;

                    for (int j = 0; j < halfLength; j++)
                    {
                        float uncondNoise = noisePredSpan[j];
                        float textNoise = noisePredSpan[j + halfLength];
                        float noise = uncondNoise + guidanceScale * (textNoise - uncondNoise);

                        // EULER FIX: Die Ableitung korrekt berechnen
                        guidedSpan[j] = (lSpan[j] - (noise * sigma)) / sigma;
                    }

                    latents = this.ApplySchedulerStep(latents, guidedNoise, i);
                    progress?.Report(0.2 + (0.8 * (i / (double) steps)));
                }

                return await this.DecodeLatentsAsync(latents);
            }
            catch (Exception ex)
            {
                await StaticLogger.LogAsync(ex);
                throw;
            }
        }


        private DenseTensor<float> DuplicateLatents(DenseTensor<float> latents)
        {
            var duplicated = new DenseTensor<float>(new[] { 2, 4, 64, 64 });
            latents.Buffer.Span.CopyTo(duplicated.Buffer.Span);
            latents.Buffer.Span.CopyTo(duplicated.Buffer.Span.Slice(latents.Buffer.Span.Length));
            return duplicated;
        }

        // In StableDiffusionService.Generate.cs

        private DenseTensor<float> PrepareLatents(long seed)
        {
            var random = seed == -1 ? new Random() : new Random((int) seed);
            var latents = new DenseTensor<float>(new[] { 1, 4, 64, 64 });
            var span = latents.Buffer.Span;

            for (int i = 0; i < span.Length; i++)
            {
                double u1 = 1.0 - random.NextDouble();
                double u2 = 1.0 - random.NextDouble();
                float randStdNormal = (float) (Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2));
                span[i] = randStdNormal;
            }
            return latents;
        }

        private IEnumerable<int> CreateTimesteps(int steps)
        {
            // Einfache lineare Verteilung für den Anfang
            int totalSteps = 1000;
            int interval = totalSteps / steps;
            for (int i = steps - 1; i >= 0; i--) yield return i * interval;
        }
    }
}
using Microsoft.ML.OnnxRuntime.Tensors;
using SharpAI.Core;
using System.Text.Json;

namespace SharpAI.StableDiffusion
{
    public partial class StableDiffusionService
    {
        private float[] _alphasCumulativeProducts = Array.Empty<float>();
        private float[] _sigmas = Array.Empty<float>();

        public async Task LoadSchedulerAsync()
        {
            // Diese Werte sind Standard für Stable Diffusion v1.5
            float betaStart = 0.00085f;
            float betaEnd = 0.012f;
            int trainSteps = 1000;

            // Betas berechnen
            var betas = Enumerable.Range(0, trainSteps)
                .Select(i => betaStart + (betaEnd - betaStart) * i / (trainSteps - 1))
                .ToArray();

            // Alphas berechnen
            var alphas = betas.Select(b => 1.0f - b).ToArray();
            this._alphasCumulativeProducts = new float[trainSteps];
            float currentProduct = 1.0f;
            for (int i = 0; i < trainSteps; i++)
            {
                currentProduct *= alphas[i];
                this._alphasCumulativeProducts[i] = currentProduct;
            }
        }

        public void PrepareScheduler(int steps)
        {
            // Sigmas für Euler Discrete berechnen: sigma = sqrt((1 - alpha) / alpha)
            var allSigmas = this._alphasCumulativeProducts
                .Select(a => (float) Math.Sqrt((1.0 - a) / a))
                .ToArray();

            this._sigmas = new float[steps + 1];
            double stepSize = (double) (allSigmas.Length - 1) / (steps - 1);

            for (int i = 0; i < steps; i++)
            {
                // Mappen der Schritte auf die 1000 Trainingsschritte
                int idx = (int) Math.Round((steps - 1 - i) * stepSize);
                this._sigmas[i] = allSigmas[idx];
            }
            this._sigmas[steps] = 0.0f; // Letzter Schritt ist rauschfrei
        }

        private DenseTensor<float> ApplySchedulerStep(DenseTensor<float> latents, DenseTensor<float> noisePred, int stepIndex)
        {
            var newLatents = new DenseTensor<float>(latents.Dimensions);
            var lSpan = latents.Buffer.Span;
            var nSpan = noisePred.Buffer.Span;
            var resSpan = newLatents.Buffer.Span;

            // Euler Methode: x_next = x + dt * derivative
            float sigma = this._sigmas[stepIndex];
            float nextSigma = this._sigmas[stepIndex + 1];
            float dt = nextSigma - sigma;

            for (int i = 0; i < lSpan.Length; i++)
            {
                // noisePred fungiert hier als die Ableitung (derivative)
                resSpan[i] = lSpan[i] + (nSpan[i] * dt);
            }
            return newLatents;
        }
    }
}
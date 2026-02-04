using LLama;
using LLama.Common;
using SharpAI.Core;
using SharpAI.Shared;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SharpAI.Runtime
{
    public partial class LlamaService
    {



        public IAsyncEnumerable<string>? GenerateAsync(LlamaGenerationRequest generationRequest, CancellationToken ct = default)
        {
            // Validate model is loaded, else return null
            if (!this.IsModelLoaded || this.llamaExecutor == null)
            {
                StaticLogger.Log("GenerateAsync called but no model is loaded.");
                return null;
            }

            if (generationRequest == null)
            {
                StaticLogger.Log("GenerateAsync called with a null request.");
                return null;
            }

            // Prompt is in request, base64-images are in request too (default empty)
            // MaxTokens, Temperature, TopP, Stream are in request

            // Call the Llama model to generate text based on the prompt and parameters
            return this.GenerateInternalAsync(generationRequest, ct);
        }



        // Wrappers for non-streaming generation
        public async Task<string?> GenerateTextAsync(LlamaGenerationRequest generationRequest, CancellationToken ct = default)
        {
            // Calls GenerateAsync and collects the full text into a single string to return
            var stream = this.GenerateAsync(generationRequest, ct);
            if (stream == null)
            {
                return null;
            }

            var builder = new StringBuilder();
            await foreach (var chunk in stream.WithCancellation(ct).ConfigureAwait(false))
            {
                builder.Append(chunk);
            }

            return builder.ToString();
        }

        private async IAsyncEnumerable<string> GenerateInternalAsync(LlamaGenerationRequest generationRequest, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            if (this.llamaContext == null)
            {
                var errorText = "[Error] Llama context is not initialized.";
                StaticLogger.Log(errorText);
                yield return errorText;
                yield break;
            }

            var stopwatch = Stopwatch.StartNew();

            this.CurrentContext ??= new LlamaContextData();

            var prompt = generationRequest.Prompt ?? string.Empty;
            var shouldReplayHistory = this.primedContext == null
                || this.primedContext != this.CurrentContext
                || this.CurrentContext.Messages.Count < this.primedMessageCount;
            var shouldResetExecutor = this.primedContext != null
                && (this.primedContext != this.CurrentContext || this.CurrentContext.Messages.Count < this.primedMessageCount);

            if (shouldResetExecutor && !this.TryResetExecutor())
            {
                var errorText = "[Error] Failed to reset Llama context.";
                StaticLogger.Log(errorText);
                yield return errorText;
                yield break;
            }

            var fullPrompt = shouldReplayHistory
                ? BuildPrompt(this.CurrentContext, prompt)
                : BuildTurnPrompt(prompt);

            var promptTokens = this.llamaContext.Tokenize(fullPrompt, addBos: true, special: false).Length;
            var contextSize = (int)this.llamaContext.ContextSize;
            var availableTokens = Math.Max(0, contextSize - promptTokens);
            var maxTokens = Math.Min(generationRequest.MaxTokens, availableTokens);
            if (maxTokens <= 0)
            {
                var errorText = "[Error] Prompt exceeds context size.";
                StaticLogger.Log(errorText);
                yield return errorText;
                yield break;
            }

            var inferenceParams = new InferenceParams
            {
                MaxTokens = maxTokens
            };

            var responseBuilder = new StringBuilder();
            var executor = this.llamaExecutor;
            if (executor == null)
            {
                var errorText = "[Error] Llama executor is not initialized.";
                StaticLogger.Log(errorText);
                yield return errorText;
                yield break;
            }

            await foreach (var chunk in executor.InferAsync(fullPrompt, inferenceParams, ct).WithCancellation(ct).ConfigureAwait(false))
            {
                responseBuilder.Append(chunk);
                if (generationRequest.Stream)
                {
                    yield return chunk;
                }
            }

            var responseText = responseBuilder.ToString();
            stopwatch.Stop();

            var tokens = responseText.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var stats = new LlamaContextStats
            {
                TokensUsed = tokens.Length,
                SecondsElapsed = stopwatch.Elapsed.TotalSeconds
            };

            var statsText = $"[Stats] tokens={stats.TokensUsed}, elapsed={stats.SecondsElapsed:F3}s, tokens/s={stats.TokensPerSecond:F2}";
            if (generationRequest.Stream)
            {
                yield return statsText;
            }
            else
            {
                yield return responseText + statsText;
            }

            this.CurrentContext.Messages.Add(new LlamaContextMessage
            {
                Role = "user",
                Content = prompt,
                Timestamp = DateTime.UtcNow
            });

            this.CurrentContext.Messages.Add(new LlamaContextMessage
            {
                Role = "assistant",
                Content = responseText,
                Timestamp = DateTime.UtcNow,
                Stats = stats
            });

            this.primedContext = this.CurrentContext;
            this.primedMessageCount = this.CurrentContext.Messages.Count;

            StaticLogger.Log($"Generated {tokens.Length} tokens in {stopwatch.Elapsed.TotalSeconds:F2}s.");

            if (!string.IsNullOrWhiteSpace(this.CurrentContext.FilePath))
            {
                await this.SaveContextAsync(this.CurrentContext.FilePath).ConfigureAwait(false);
            }
        }

        private static string BuildPrompt(LlamaContextData context, string prompt)
        {
            var sb = new StringBuilder();
            var history = context.Messages
                .Where(message => !string.IsNullOrWhiteSpace(message.Content))
                .TakeLast(10)
                .ToList();

            foreach (var message in history)
            {
                sb.Append(message.Role).Append(": ").AppendLine(message.Content);
            }

            sb.Append("user: ").AppendLine(prompt);
            sb.Append("assistant: ");
            return sb.ToString();
        }

        private static string BuildTurnPrompt(string prompt)
        {
            var sb = new StringBuilder();
            sb.Append("user: ").AppendLine(prompt);
            sb.Append("assistant: ");
            return sb.ToString();
        }



    }
}

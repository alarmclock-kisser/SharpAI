using SharpAI.Core;
using SharpAI.Shared;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace SharpAI.Runtime
{
    public partial class LlamaService
    {
        public static string AssemblyName => System.Reflection.Assembly.GetExecutingAssembly().GetName().Name?.Split('.').FirstOrDefault() ?? "LlamaSharpProject";
        public readonly string ContextsDirectory = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AssemblyName, "Contexts");


        public string? SystemPrompt { get; private set; } = "You are a helpful assistant that provides information and answers questions to the best of your ability. Always try to be helpful, concise, and accurate in your responses. If you don't know the answer to something, it's okay to say you don't know. Answer in the language the user is using, if possible.";

        public LlamaContextData? CurrentContext { get; private set; } = null;


        public async Task<List<LlamaContextData>?> GetAllContextsAsync()
        {
            // Loads all context json files from the default dir
            // Returns list of loaded contexts
            if (!Directory.Exists(this.ContextsDirectory))
            {
                return null;
            }

            var contextFiles = Directory.GetFiles(this.ContextsDirectory, "*.json");
            var contexts = new List<LlamaContextData>();
            foreach (var filePath in contextFiles)
            {
                try
                {
                    var json = await File.ReadAllTextAsync(filePath).ConfigureAwait(false);
                    var context = JsonSerializer.Deserialize<LlamaContextData>(json) ?? new LlamaContextData();
                    context.FilePath = filePath;
                    contexts.Add(context);
                }
                catch (Exception ex)
                {
                    StaticLogger.Log($"Failed to load context from '{filePath}': {ex.Message}");
                }
            }
            return contexts;
        }


        public async Task<string?> CreateNewContextAsync(bool use = true, bool saveJson = false)
        {
            // Creates a new empty context

            // If use is true, sets CurrentContext to the new context
            // If saveJson is true, saves the context to a new json file in the default dir (enables autosave)
            var newContext = new LlamaContextData
            {
                SystemPrompt = this.SystemPrompt
            };

            if (use)
            {
                this.CurrentContext = newContext;
            }

            if (!saveJson)
            {
                return null;
            }

            Directory.CreateDirectory(this.ContextsDirectory);
            var fileName = $"context_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.json";
            var filePath = Path.Combine(this.ContextsDirectory, fileName);
            newContext.FilePath = filePath;
            var json = JsonSerializer.Serialize(newContext, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(filePath, json).ConfigureAwait(false);

            StaticLogger.Log($"Created new context at '{filePath}'.");

            // Returns json file path if created successfully
            return filePath;
        }

        public async Task<string?> SaveContextAsync(string? differentNameOrPath = null)
        {
            // If no current context, create one
            // If differentNameOrPath is provided, use that to save the context (use file name under default dir or if it's a full path, use that)
            // Updates or sets CurrentContext's file path if saved successfully (enables autosave from since it has a file path)
            this.CurrentContext ??= new LlamaContextData();
            this.CurrentContext.SystemPrompt ??= this.SystemPrompt;

            Directory.CreateDirectory(this.ContextsDirectory);

            string filePath;
            if (!string.IsNullOrWhiteSpace(differentNameOrPath))
            {
                filePath = Path.IsPathRooted(differentNameOrPath)
                    ? differentNameOrPath
                    : Path.Combine(this.ContextsDirectory, differentNameOrPath);

                if (!Path.HasExtension(filePath))
                {
                    filePath = Path.ChangeExtension(filePath, ".json");
                }
            }
            else if (!string.IsNullOrWhiteSpace(this.CurrentContext.FilePath))
            {
                filePath = this.CurrentContext.FilePath;
            }
            else
            {
                filePath = Path.Combine(this.ContextsDirectory, $"context_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.json");
            }

            var json = JsonSerializer.Serialize(this.CurrentContext, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(filePath, json).ConfigureAwait(false);
            this.CurrentContext.FilePath = filePath;

            StaticLogger.Log($"Saved context to '{filePath}'.");

            // Returns json file path if saved successfully
            return filePath;
        }

        public async Task<LlamaContextData?> LoadContextAsync(string contextNameOrFilePath = "/recent")
        {
            if (contextNameOrFilePath.StartsWith("/recent"))
            {
                var recentContexts = await this.GetAllContextsAsync();
                var mostRecentContext = recentContexts?.OrderByDescending(c => c.LatestActivityDate ?? DateTime.MinValue).FirstOrDefault();
                if (mostRecentContext != null)
                {
                    contextNameOrFilePath = mostRecentContext.FilePath;
                    StaticLogger.Log($"Loading most recent context: '{contextNameOrFilePath}'.");
                }
                else
                {
                    StaticLogger.Log("No recent contexts found to load.");
                    return null;
                }
            }
            if (contextNameOrFilePath.StartsWith("/new"))
            {
                StaticLogger.Log("Creating and loading new context.");
                var newContextPath = await this.CreateNewContextAsync(use: true, saveJson: false).ConfigureAwait(false);
                if (newContextPath != null)
                {
                    StaticLogger.Log($"New context created and loaded from '{newContextPath}'.");
                    return this.CurrentContext;
                }
                else
                {
                    StaticLogger.Log("Failed to create new context.");
                    return null;
                }
            }

            if (string.IsNullOrWhiteSpace(contextNameOrFilePath))
            {
                return null;
            }

            var filePath = Path.IsPathRooted(contextNameOrFilePath)
                ? contextNameOrFilePath
                : Path.Combine(this.ContextsDirectory, contextNameOrFilePath);

            if (!Path.HasExtension(filePath))
            {
                filePath = Path.ChangeExtension(filePath, ".json");
            }

            if (!File.Exists(filePath))
            {
                StaticLogger.Log($"Context file not found: {filePath}");
                return null;
            }

            var json = await File.ReadAllTextAsync(filePath).ConfigureAwait(false);
            var context = JsonSerializer.Deserialize<LlamaContextData>(json) ?? new LlamaContextData();
            context.FilePath = filePath;
            this.CurrentContext = context;
            if (!string.IsNullOrWhiteSpace(context.SystemPrompt))
            {
                this.SystemPrompt = context.SystemPrompt;
            }

            StaticLogger.Log($"Loaded context from '{filePath}'.");
            return context;
        }

        public async Task<string?> RenameContextAsync(string? contextNameOrPath = null, string? newName = null)
        {
            newName ??= Guid.NewGuid().ToString();
            if (string.IsNullOrWhiteSpace(contextNameOrPath))
            {
                contextNameOrPath = this.CurrentContext?.FilePath;
                if (string.IsNullOrWhiteSpace(contextNameOrPath))
                {
                    StaticLogger.Log("No context specified to rename and no current context set.");
                    return null;
                }
            }

            var filePath = Path.IsPathRooted(contextNameOrPath)
                ? contextNameOrPath
                : Path.Combine(this.ContextsDirectory, contextNameOrPath);
            if (!File.Exists(filePath))
            {
                StaticLogger.Log($"Context file not found for renaming: {filePath}");
                return null;
            }

            var newFilePath = Path.Combine(Path.GetDirectoryName(filePath) ?? this.ContextsDirectory, $"{Path.GetFileNameWithoutExtension(newName)}.json");
            try
            {
                File.Move(filePath, newFilePath);
                if (this.CurrentContext != null && string.Equals(this.CurrentContext.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
                {
                    this.CurrentContext.FilePath = newFilePath;
                }
                StaticLogger.Log($"Renamed context from '{filePath}' to '{newFilePath}'.");
                return newFilePath;
            }
            catch (Exception ex)
            {
                StaticLogger.Log($"Failed to rename context file: {ex.Message}");
                return null;
            }
        }

        public Task<bool> DeleteContextAsync(string? contextNameOrFilePath = null)
        {
            // If no contextNameOrFilePath provided, delete CurrentContext if set and create a new one + use it
            // If contextNameOrFilePath is just a name, look for it in the default dir
            // If it's a full path, use that
            // Deletes the context file, returns true if successful
            string? filePath = null;
            if (!string.IsNullOrWhiteSpace(contextNameOrFilePath))
            {
                filePath = Path.IsPathRooted(contextNameOrFilePath)
                    ? contextNameOrFilePath
                    : Path.Combine(this.ContextsDirectory, contextNameOrFilePath);

                if (!Path.HasExtension(filePath))
                {
                    filePath = Path.ChangeExtension(filePath, ".json");
                }
            }
            else if (this.CurrentContext != null && !string.IsNullOrWhiteSpace(this.CurrentContext.FilePath))
            {
                filePath = this.CurrentContext.FilePath;
            }

            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                if (string.IsNullOrWhiteSpace(contextNameOrFilePath))
                {
                    this.CurrentContext = new LlamaContextData();
                }

                return Task.FromResult(false);
            }

            File.Delete(filePath);

            if (this.CurrentContext != null && string.Equals(this.CurrentContext.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
            {
                this.CurrentContext = new LlamaContextData();
            }

            StaticLogger.Log($"Deleted context '{filePath}'.");
            return Task.FromResult(true);
        }

        public async Task SetSystemPromptAsync(string? systemPrompt, bool updateCurrentContext = true)
        {
            this.SystemPrompt = string.IsNullOrWhiteSpace(systemPrompt) ? null : systemPrompt;
            if (updateCurrentContext)
            {
                this.CurrentContext ??= new LlamaContextData();
                this.CurrentContext.SystemPrompt = this.SystemPrompt;
                if (!string.IsNullOrWhiteSpace(this.CurrentContext.FilePath))
                {
                    await this.SaveContextAsync(this.CurrentContext.FilePath).ConfigureAwait(false);
                }
            }
        }

    }
}

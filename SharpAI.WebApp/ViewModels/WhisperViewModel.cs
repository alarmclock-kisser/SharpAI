using Radzen;
using SharpAI.Client;
using SharpAI.Shared;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SharpAI.WebApp.ViewModels
{
    public class WhisperViewModel
    {
        private readonly ApiClient api;
        private CancellationTokenSource? whisperCts;

        public WhisperViewModel(ApiClient api)
        {
            this.api = api;
        }

        public List<AudioObjInfo> AudioInfos { get; } = new();
        public Guid SelectedAudioId { get; set; } = Guid.Empty;

        public List<string> WhisperModels { get; set; } = new();
        public string? SelectedWhisperModel { get; set; } = null;

        public string[] Languages { get; set; } = new[] { "", "en", "es", "fr", "de", "zh", "ja", "ru", "it", "ko", "pt", "ar", "nl", "sv", "no", "fi", "da", "pl", "cs", "hu", "ro", "el", "tr", "vi", "th", "id", "ms", "he", "fa", "ur", "bg" };

        public bool IsInitialized { get; private set; } = false;
        public bool CanRunWhisper => this.IsInitialized && this.SelectedAudioId != Guid.Empty;
        public bool InitializeRunning { get; private set; } = false;
        public string InitializeButtonText => this.InitializeRunning ? "Initializing..." : this.IsInitialized ? "Dispose" : "Initialize";
        public string InitializeButtonColorHex => this.InitializeRunning ? "#6c757d" : this.IsInitialized ? "#dc3545" : "#28a745";
        public string InitializeButtonStyle => $"background-color: {this.InitializeButtonColorHex}; color: white; border: none; padding: 0.375rem 0.75rem; border-radius: 0.25rem; cursor: {(this.InitializeRunning ? "not-allowed" : "pointer")};";

        public string? Language { get; set; } = "en";
        public bool StreamResponse { get; set; } = true;
        public bool UseCuda { get; set; } = false;

        public bool WhisperRunning { get; private set; } = false;
        public string RunWhisperButtonText => this.WhisperRunning ? "Cancel" : "Run Whisper";

        public string? WhisperOutput { get; private set; } = null;
        public string? StatusMessage { get; private set; } = null;

        public Func<Task>? NotifyStateChanged { get; set; }

        private bool FirstRender { get; set; } = true;

        public Task InitializeAsync() => this.RefreshAsync();

        public async Task RefreshAsync()
        {
            this.AudioInfos.Clear();
            var audioInfos = await this.api.GetAudiosAsync();
            if (audioInfos != null)
            {
                this.AudioInfos.AddRange(audioInfos.OrderByDescending(a => a.CreatedAt));
            }

            if (this.SelectedAudioId != Guid.Empty && this.AudioInfos.All(a => a.Id != this.SelectedAudioId))
            {
                this.SelectedAudioId = Guid.Empty;
            }

            var models = await this.api.GetWhisperNetModelsAsync();
            if (models != null)
            {
                this.WhisperModels = models.OrderByDescending(m => m.Length).ToList();
            }

            var status = await this.api.GetWhisperNetStatusAsync();
            if (string.IsNullOrWhiteSpace(status))
            {
                this.IsInitialized = false;
            }
            else
            {
                this.IsInitialized = true;
                // store selected model if none chosen
                if (string.IsNullOrWhiteSpace(this.SelectedWhisperModel) && !string.IsNullOrWhiteSpace(status))
                {
                    this.SelectedWhisperModel = status;
                }
            }

            if (this.FirstRender)
            {
                var appsettings = await this.api.GetAppsettingsAsync();
                if (appsettings != null && !string.IsNullOrWhiteSpace(appsettings.DefaultWhistperModel) && this.WhisperModels.Count > 0)
                {
                    this.SelectedWhisperModel = this.WhisperModels.FirstOrDefault(m => m.Contains(appsettings.DefaultWhistperModel, StringComparison.OrdinalIgnoreCase)) ?? this.SelectedWhisperModel;
                }
                this.FirstRender = false;
            }

            if (this.NotifyStateChanged != null) await this.NotifyStateChanged();
        }

        public async Task InitializeWhisperAsync()
        {
            this.StatusMessage = null;
            this.InitializeRunning = true;
            await this.RefreshAsync();

            var result = await this.api.LoadWhisperNetModelAsync(this.SelectedWhisperModel, this.UseCuda);
            if (result.HasValue && result.Value)
            {
                this.StatusMessage = "Whisper (whisper.net) initialized.";
                this.IsInitialized = true;
            }
            else
            {
                this.StatusMessage = "Failed to initialize Whisper (whisper.net).";
            }

            this.InitializeRunning = false;
            await this.RefreshAsync();
            if (this.NotifyStateChanged != null) await this.NotifyStateChanged();
        }

        public async Task DisposeWhisperAsync()
        {
            this.StatusMessage = null;
            var result = await this.api.DisposeWhisperNetAsync();
            if (result)
            {
                this.StatusMessage = "Whisper disposed.";
                this.IsInitialized = false;
            }
            else
            {
                this.StatusMessage = "Failed to dispose Whisper.";
            }

            this.InitializeRunning = false;
            await this.RefreshAsync();
            if (this.NotifyStateChanged != null) await this.NotifyStateChanged();
        }

        public async Task RunWhisperAsync()
        {
            this.StatusMessage = null;
            if (this.SelectedAudioId == Guid.Empty)
            {
                this.StatusMessage = "Select an audio first.";
                return;
            }

            this.WhisperRunning = true;
            this.whisperCts = new CancellationTokenSource();
            this.WhisperOutput = string.Empty;

            try
            {
                if (this.StreamResponse)
                {
                    await foreach (var chunk in this.api.RunWhisperNetStreamAsync(this.SelectedAudioId.ToString(), this.Language, this.UseCuda, this.whisperCts.Token))
                    {
                        if (string.IsNullOrEmpty(chunk)) continue;
                        this.WhisperOutput += chunk;
                        if (this.NotifyStateChanged != null) await this.NotifyStateChanged();
                    }
                }
                else
                {
                    var result = await this.api.RunWhisperNetAsync(this.SelectedAudioId.ToString(), this.Language, this.UseCuda, this.whisperCts.Token);
                    if (!string.IsNullOrWhiteSpace(result))
                    {
                        this.WhisperOutput = result;
                    }
                }

                if (string.IsNullOrWhiteSpace(this.WhisperOutput)) this.StatusMessage = "No result from whisper.net transcription."; else this.StatusMessage = "Whisper transcription completed.";
            }
            catch (OperationCanceledException)
            {
                this.StatusMessage = "Whisper transcription cancelled.";
            }
            finally
            {
                this.WhisperRunning = false;
                this.InitializeRunning = false;
                if (this.NotifyStateChanged != null) await this.NotifyStateChanged();
                await this.RefreshAsync();
                if (this.NotifyStateChanged != null) await this.NotifyStateChanged();
            }
        }

        public async Task CancelWhisperAsync()
        {
            this.StatusMessage = null;
            this.whisperCts?.Cancel();
            var result = await this.api.CancelOnnxWhisperAsync();
            if (result.HasValue && result.Value)
            {
                this.StatusMessage = "Whisper transcription cancelled.";
            }
            else
            {
                this.StatusMessage = "Failed to cancel Whisper transcription.";
            }
            this.WhisperRunning = false;
            this.InitializeRunning = false;
            await this.RefreshAsync();
            if (this.NotifyStateChanged != null) await this.NotifyStateChanged();
        }

        public async Task DeleteAsync(Guid id)
        {
            var ok = await this.api.DeleteAudioAsync(id);
            this.StatusMessage = ok ? "Audio deleted." : "Delete failed.";
            if (ok && this.SelectedAudioId == id)
            {
                this.SelectedAudioId = Guid.Empty;
            }
            await this.RefreshAsync();
            if (this.NotifyStateChanged != null) await this.NotifyStateChanged();
        }
    }
}

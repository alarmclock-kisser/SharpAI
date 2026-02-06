using Radzen;
using SharpAI.Client;
using SharpAI.Shared;
using System.IO;
using System.Linq;
using System.Threading;

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

        public List<WhisperModelInfo> WhisperModels { get; set; } = new();
        public WhisperModelInfo? SelectedWhisperModel { get; set; } = null;
        public WhisperModelInfo? LoadedWhisperModel { get; set; } = null;

        public string[] Languages { get; set; } = new[]
        {
            "", "en", "es", "fr", "de", "zh", "ja", "ru", "it", "ko", "pt", "ar", "nl", "sv", "no", "fi", "da", "pl", "cs", "hu", "ro", "el", "tr", "vi", "th", "id", "ms", "he", "fa", "ur", "bg"
        };
        public bool IsInitialized => this.LoadedWhisperModel != null;
        public bool CanRunWhisper => this.IsInitialized && this.SelectedAudioId != Guid.Empty;
        public bool InitializeRunning { get; private set; } = false;
        public string InitializeButtonText => this.InitializeRunning ? "Initializing..." : this.IsInitialized ? "Dispose" : "Initialize";
        public string InitializeButtonColorHex => this.InitializeRunning ? "#6c757d" : this.IsInitialized ? "#dc3545" : "#28a745";

        public string InitializeButtonStyle => $"background-color: {this.InitializeButtonColorHex}; color: white; border: none; padding: 0.375rem 0.75rem; border-radius: 0.25rem; cursor: {(this.InitializeRunning ? "not-allowed" : "pointer")};";

        // Default to first entry (empty string) so dropdown shows the empty/default option
        public string? Language { get; set; } = "";

        public bool Transcribe { get; set; } = false;

        public bool UseTimestamps { get; set; } = true;

        public bool StreamResponse { get; set; } = true;

        public double ChunkDuration { get; set; } = 20;

        public bool WhisperRunning { get; private set; } = false;
        public string RunWhisperButtonText => this.WhisperRunning ? "Cancel" : "Run Whisper";

        public string? WhisperOutput { get; private set; } = null;

        public double? WhisperProgress { get; private set; } = null;

        public string? StatusMessage { get; private set; } = null;

        public Func<Task>? NotifyStateChanged { get; set; }

        // Upload helpers
        public Stream? SelectedFileStream { get; private set; }
        public string? SelectedFileName { get; private set; }
        public string? SelectedFileContentType { get; private set; }

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

            if (this.SelectedAudioId != Guid.Empty &&
                this.AudioInfos.All(a => a.Id != this.SelectedAudioId))
            {
                this.SelectedAudioId = Guid.Empty;
            }

            var selectedModelName = this.SelectedWhisperModel?.ModelName;
            var whisperModels = await this.api.GetWhisperModelsAsync();
            if (whisperModels != null)
            {
                this.WhisperModels = whisperModels.OrderBy(m => m.SizeInMb).ToList();
            }

            if (!string.IsNullOrWhiteSpace(selectedModelName))
            {
                this.SelectedWhisperModel = this.WhisperModels.FirstOrDefault(m => m.ModelName == selectedModelName) ?? this.SelectedWhisperModel;
            }

            // FirstOrDefault model
            if (this.SelectedWhisperModel == null && this.WhisperModels.Count > 0)
            {
                this.SelectedWhisperModel = this.WhisperModels.First();
            }

            // FirstOrDefault audio info
            if (this.AudioInfos.Count > 0 && this.SelectedAudioId == Guid.Empty)
            {
                this.SelectedAudioId = this.AudioInfos.First().Id;
            }

            WhisperModelInfo? loadedModel = await this.api.GetCurrentOnnxWhisperModel();
            if (loadedModel == null)
            {
                this.StatusMessage = "No Whisper Onnx is initialized or loaded.";
            }
            else
            {
                this.LoadedWhisperModel = loadedModel;
            }

            if (this.FirstRender)
            {
                var appsettings = await this.api.GetAppsettingsAsync();

                if (appsettings != null)
                {
                    this.SelectedWhisperModel = this.WhisperModels.FirstOrDefault(m => m.ModelName == appsettings.DefaultWhistperModel) ?? this.SelectedWhisperModel;
                }

                this.FirstRender = false;
            }
        }

        public async Task InitializeWhisperAsync()
        {
            this.StatusMessage = null;
            this.InitializeRunning = true;
            await this.RefreshAsync();

            var result = await this.api.LoadWhisperModelAsync(this.SelectedWhisperModel);
            if (result.HasValue && result.Value)
            {
                this.StatusMessage = "Whisper model initialized.";
            }
            else
            {
                this.StatusMessage = "Failed to initialize Whisper model.";
                if (this.WhisperModels.Count <= 0)
                {
                    this.StatusMessage += " No Whisper models available. Please add ONNX Whisper models to the Models folder.";
                }
            }

            var statusCheck = await this.api.GetCurrentOnnxWhisperModel();
            if (statusCheck == null)
            {
                this.StatusMessage += " Failed to check model status after initialization.";
            }
            else
            {
                this.LoadedWhisperModel = statusCheck;
            }

            this.InitializeRunning = false;
            await this.RefreshAsync();
        }

        public async Task DisposeWhisperAsync()
        {
            this.StatusMessage = null;
            var result = await this.api.DisposeWhisperAsync();
            if (result.HasValue && result.Value)
            {
                var status = await this.api.GetCurrentOnnxWhisperModel();
                if (status == null)
                {
                    this.LoadedWhisperModel = null;
                }

                this.StatusMessage = "Whisper model disposed.";
            }
            else
            {
                this.StatusMessage = "Failed to dispose Whisper model.";
            }

            this.InitializeRunning = false;
            await this.RefreshAsync();
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
            this.WhisperProgress = null;
            this.WhisperOutput = string.Empty;
            var progressTask = TrackWhisperProgressAsync(this.whisperCts.Token);
            try
            {
                if (this.StreamResponse)
                {
                    await foreach (var chunk in this.api.RunWhisperStreamAsync(this.SelectedAudioId.ToString(), this.Language, this.Transcribe, this.UseTimestamps, true, this.ChunkDuration, this.whisperCts.Token))
                    {
                        if (string.IsNullOrEmpty(chunk))
                        {
                            continue;
                        }

                        if (chunk == "\\n")
                        {
                            this.WhisperOutput += "\n";
                        }
                        else
                        {
                            this.WhisperOutput += chunk;
                        }

                        if (this.NotifyStateChanged != null)
                        {
                            await this.NotifyStateChanged();
                        }
                    }

                    if (string.IsNullOrWhiteSpace(this.WhisperOutput))
                    {
                        this.StatusMessage = "No result from whisper transcription via ONNX.";
                    }
                    else
                    {
                        this.StatusMessage = "Whisper transcription completed.";
                    }
                }
                else
                {
                    var result = await this.api.RunWhisperAsync(this.SelectedAudioId.ToString(), this.Language, this.Transcribe, this.UseTimestamps, this.ChunkDuration, this.whisperCts.Token);
                    if (string.IsNullOrWhiteSpace(result))
                    {
                        this.StatusMessage = "No result from whisper transcription via ONNX.";
                    }
                    else
                    {
                        this.WhisperOutput = result;
                        this.StatusMessage = "Whisper transcription completed.";
                    }
                }
            }
            catch (OperationCanceledException)
            {
                this.StatusMessage = "Whisper transcription cancelled.";
            }
            finally
            {
                // Final progress read before cancelling the tracking task
                try
                {
                    this.WhisperProgress = await this.api.GetWhisperTaskProgressAsync(CancellationToken.None);
                }
                catch { /* ignore */ }

                if (this.whisperCts != null)
                {
                    this.whisperCts.Cancel();
                }
                await progressTask;
                this.WhisperRunning = false;
                this.InitializeRunning = false;
                if (this.NotifyStateChanged != null) await this.NotifyStateChanged();
                await this.RefreshAsync();
            }
        }

        public async Task CancelWhisperAsync()
        {
            this.StatusMessage = null;
            this.whisperCts?.Cancel();
            var result = await this.api.CancelWhisperAsync();
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
        }

        private async Task TrackWhisperProgressAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    this.WhisperProgress = await this.api.GetWhisperTaskProgressAsync(ct);
                    if (this.NotifyStateChanged != null)
                    {
                        await this.NotifyStateChanged();
                    }
                    await Task.Delay(2000, ct);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        public async Task OnFileSelectedAsync(UploadChangeEventArgs args)
        {
            var file = args.Files?.FirstOrDefault();
            if (file == null)
            {
                this.SelectedFileStream = null;
                this.SelectedFileName = null;
                this.SelectedFileContentType = null;
                return;
            }

            this.SelectedFileName = file.Name;
            this.SelectedFileContentType = file.ContentType;
            this.SelectedFileStream = file.OpenReadStream(maxAllowedSize: 104857600); // 100 MB
            await this.UploadAsync();
        }

        public async Task UploadAsync()
        {
            this.StatusMessage = null;
            if (this.SelectedFileStream == null || string.IsNullOrWhiteSpace(this.SelectedFileName))
            {
                return;
            }

            await using var stream = this.SelectedFileStream;
            var fileParam = new FileParameter(stream, this.SelectedFileName, this.SelectedFileContentType);
            var uploaded = await this.api.UploadAudioAsync(fileParam);
            this.StatusMessage = uploaded != null ? "Audio uploaded." : "Upload failed.";
            this.SelectedFileStream = null;
            this.SelectedFileName = null;
            this.SelectedFileContentType = null;
            await this.RefreshAsync();
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
        }
    }
}

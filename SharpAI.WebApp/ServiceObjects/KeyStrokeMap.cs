using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.JSInterop;
using SharpAI.Core;

namespace SharpAI.Client
{
    public class KeyStrokeMap : IAsyncDisposable
    {
        private readonly DateTime CreatedAt = DateTime.UtcNow;
        private readonly CancellationToken _cancellationToken;

        private readonly ConcurrentDictionary<long, string> _keyStrokes = new();

        private IJSObjectReference? _module;
        private DotNetObjectReference<KeyStrokeMap>? _dotNetRef;
        private string? _elementId;
        private bool _started;
        private CancellationTokenRegistration? _ctrRegistration;

        public KeyStrokeMap(IJSRuntime jsRuntime, CancellationToken ct = default)
        {
            this._cancellationToken = ct;

            if (ct.CanBeCanceled)
            {
                // When cancellation requested, stop monitoring
                _ctrRegistration = ct.Register(async () => await StopAsync().ConfigureAwait(false));
            }

            _ = StartAsync(jsRuntime, "inputTextArea");
        }

        /// <summary>
        /// Start monitoring keydown events on the input element with the given id.
        /// The library expects a static web asset at ./_content/SharpAI.Client/keystrokemap.js that exposes an attach and detach function.
        /// </summary>
        /// <param name="jsRuntime">IJSRuntime instance from Blazor</param>
        /// <param name="elementId">Id attribute of the input element to observe</param>
        public async Task StartAsync(IJSRuntime jsRuntime, string elementId)
        {
            if (_started)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(elementId))
            {
                throw new ArgumentException("elementId must be provided", nameof(elementId));
            }

            _elementId = elementId;

            // Import the JS module (static web asset path)
            _module = await jsRuntime.InvokeAsync<IJSObjectReference>("import", "./_content/SharpAI.Client/keystrokemap.js");
            _dotNetRef = DotNetObjectReference.Create(this);

            await _module.InvokeVoidAsync("attachToInput", elementId, _dotNetRef);

            _started = true;
        }

        /// <summary>
        /// Stops monitoring and detaches JS listeners.
        /// </summary>
        public async Task StopAsync()
        {
            if (!_started)
            {
                return;
            }

            try
            {
                if (_module != null && !string.IsNullOrEmpty(_elementId))
                {
                    await _module.InvokeVoidAsync("detachFromInput", _elementId);
                }
            }
            catch
            {
                // ignore JS errors on detach
            }

            _dotNetRef?.Dispose();
            _dotNetRef = null;

            if (_module != null)
            {
                try
                {
                    await _module.DisposeAsync();
                }
                catch { }
                _module = null;
            }

            _started = false;
        }

        /// <summary>
        /// Called from JS when a keydown happens. Stores milliseconds from CreatedAt and the key string.
        /// </summary>
        [JSInvokable]
        public Task NotifyKeyAsync(string key)
        {
            if (key == null) key = string.Empty;

            var ms = (long)(DateTime.UtcNow - CreatedAt).TotalMilliseconds;

            // Ensure unique key for ms collisions by incrementing
            while (!_keyStrokes.TryAdd(ms, key))
            {
                ms++;
            }

            return Task.CompletedTask;
        }

        public Dictionary<long, string> GetMap()
        {
            return new Dictionary<long, string>(_keyStrokes);
        }

        public async ValueTask DisposeAsync()
        {
            _ctrRegistration?.Dispose();
            await StopAsync().ConfigureAwait(false);
        }
    }
}

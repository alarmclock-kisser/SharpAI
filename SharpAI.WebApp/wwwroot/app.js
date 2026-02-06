window.sharpAiScrollToBottom = (element) => {
    if (!element) {
        return;
    }
    element.scrollTop = element.scrollHeight;
};

// waveform resize observer
window.sharpai = window.sharpai || {};
(function () {
    let observer = null;
    const debounceMap = new WeakMap();

    function register(dotNetRef) {
        try {
            unregister();
            observer = new ResizeObserver(entries => {
                for (const entry of entries) {
                    const el = entry.target;
                    const w = Math.round(entry.contentRect.width);
                    const h = Math.round(entry.contentRect.height);
                    const audioId = el.getAttribute('data-audio-id') || null;

                    const existing = debounceMap.get(el);
                    if (existing) {
                        clearTimeout(existing);
                    }

                    const handle = setTimeout(() => {
                        if (dotNetRef) {
                            dotNetRef.invokeMethodAsync('OnWaveformContainerResized', audioId, w, h).catch(e => console.error(e));
                        }
                    }, 250);

                    debounceMap.set(el, handle);
                }
            });

            // observe existing elements
            document.querySelectorAll('.waveform-resizable').forEach(el => observer.observe(el));

            // monitor DOM additions to observe new elements
            const mo = new MutationObserver(muts => {
                muts.forEach(m => {
                    m.addedNodes.forEach(n => {
                        if (n.nodeType === 1) {
                            if (n.classList && n.classList.contains('waveform-resizable')) {
                                observer.observe(n);
                            }
                            n.querySelectorAll && n.querySelectorAll('.waveform-resizable').forEach(el => observer.observe(el));
                        }
                    });
                });
            });
            mo.observe(document.body, { childList: true, subtree: true });

            window.sharpai._mo = mo;
            window.sharpai._observer = observer;
        }
        catch (e) {
            console.error('sharpai.register error', e);
        }
    }

    function unregister() {
        try {
            if (window.sharpai._observer) {
                window.sharpai._observer.disconnect();
                window.sharpai._observer = null;
            }
            if (window.sharpai._mo) {
                window.sharpai._mo.disconnect();
                window.sharpai._mo = null;
            }
        }
        catch (e) { console.error(e); }
    }

    window.sharpai.registerWaveformResizeObserver = register;
    window.sharpai.unregisterWaveformResizeObserver = unregister;
})();

window.sharpai.downloadUrl = async function (url, filename) {
    try {
        const resp = await fetch(url, { credentials: 'same-origin' });
        if (!resp.ok) throw new Error('Network response was not ok: ' + resp.statusText);
        const blob = await resp.blob();
        const blobUrl = URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = blobUrl;
        a.download = filename || '';
        document.body.appendChild(a);
        a.click();
        a.remove();
        setTimeout(() => URL.revokeObjectURL(blobUrl), 5000);
    }
    catch (e) {
        console.error('downloadUrl error', e);
        // fallback: open url in new tab
        window.open(url, '_blank');
    }
};

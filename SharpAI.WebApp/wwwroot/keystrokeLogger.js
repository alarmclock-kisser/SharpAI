window.keystrokeLogger = {
    log: [],
    startTime: 0,
    isLogging: false,
    _el: null,
    _handler: null,

    startLogging: function (elementId) {
        // If already logging, keep going
        if (this.isLogging) return;
        this.log = [];
        this.startTime = performance.now();

        // Find the real textarea (Radzen sometimes wraps it)
        const container = document.getElementById(elementId);
        const el = container && container.tagName === 'TEXTAREA' ? container : (container ? container.querySelector('textarea') : document.getElementById(elementId));

        if (!el) return;

        const self = this;
        this._el = el;
        this._handler = function (e) {
            try {
                const pos = (typeof el.selectionStart === 'number') ? el.selectionStart : (el.value ? el.value.length : 0);
                self.log.push({
                    k: e.key,
                    t: Math.round((performance.now() - self.startTime) * 100) / 100,
                    p: pos,
                    type: 'down'
                });
            } catch (err) {
                // ignore
            }
        };

        el.addEventListener('keydown', this._handler);
        // Also listen to input events as a fallback for some IMEs
        this._inputHandler = function () {
            try {
                const pos = (typeof el.selectionStart === 'number') ? el.selectionStart : (el.value ? el.value.length : 0);
                self.log.push({ k: '[input]', t: Math.round((performance.now() - self.startTime) * 100) / 100, p: pos, type: 'input' });
            } catch (err) { }
        };
        el.addEventListener('input', this._inputHandler);

        this.isLogging = true;
    },

    stopLogging: function () {
        if (!this.isLogging) return;
        try {
            if (this._el && this._handler) this._el.removeEventListener('keydown', this._handler);
            if (this._el && this._inputHandler) this._el.removeEventListener('input', this._inputHandler);
        } catch (err) { }
        this._el = null;
        this._handler = null;
        this._inputHandler = null;
        this.isLogging = false;
    },

    getLog: function () {
        return JSON.stringify(this.log);
    },

    // Returns the current log; if empty, produce a best-effort snapshot from the element's current value
    getLogWithSnapshot: function (elementId) {
        try {
            const existing = this.getLog();
            if (existing && existing !== "[]") return existing;

            const container = document.getElementById(elementId);
            const el = container && container.tagName === 'TEXTAREA' ? container : (container ? container.querySelector('textarea') : document.getElementById(elementId));
            if (!el) return JSON.stringify([]);

            const text = el.value || '';
            const now = performance.now();
            const entries = [];
            for (let i = 0; i < text.length; i++) {
                entries.push({ k: text[i], t: Math.round((now - this.startTime + i * 10) * 100) / 100, p: i + 1, type: 'snapshot' });
            }
            return JSON.stringify(entries);
        } catch (e) {
            return JSON.stringify([]);
        }
    },

    clearLog: function () {
        this.log = [];
        this.startTime = performance.now();
    }
    ,
    attachFocusStart: function (elementId) {
        try {
            const container = document.getElementById(elementId);
            const el = container && container.tagName === 'TEXTAREA' ? container : (container ? container.querySelector('textarea') : document.getElementById(elementId));
            if (!el) return;
            // Attach once
            if (el.__keystroke_logger_attached) return;
            el.addEventListener('focus', function () {
                try {
                    window.keystrokeLogger.startLogging(elementId);
                } catch (e) { }
            }, true);
            el.__keystroke_logger_attached = true;
        } catch (e) { }
    }
};

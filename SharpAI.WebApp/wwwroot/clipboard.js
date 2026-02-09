window.clipboardWriteText = async function (text) {
    try {
        if (navigator && navigator.clipboard && typeof navigator.clipboard.writeText === 'function') {
            return await navigator.clipboard.writeText(text);
        }

        // Fallback for older browsers: use a hidden textarea + execCommand
        return new Promise((resolve, reject) => {
            try {
                const textarea = document.createElement('textarea');
                textarea.value = text;
                // Avoid scrolling to bottom
                textarea.style.position = 'fixed';
                textarea.style.left = '-9999px';
                document.body.appendChild(textarea);
                textarea.focus();
                textarea.select();

                const successful = document.execCommand('copy');
                document.body.removeChild(textarea);

                if (successful) resolve();
                else reject(new Error('execCommand("copy") failed'));
            }
            catch (err) {
                reject(err);
            }
        });
    }
    catch (err) {
        return Promise.reject(err);
    }
};

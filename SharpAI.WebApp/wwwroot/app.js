window.sharpAiScrollToBottom = (element) => {
    if (!element) {
        return;
    }
    element.scrollTop = element.scrollHeight;
};

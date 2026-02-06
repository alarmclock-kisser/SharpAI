(function(){
    let isResizing = false;
    let startX = 0;
    let startWidth = 0;

    window.sidebarStartResize = function(startClientX, dotNetRef){
        const resizer = document.querySelector('.sidebar-resizer-overlay');
        isResizing = true;
        startX = startClientX || 0;
        const sidebar = document.querySelector('.rz-sidebar');
        startWidth = sidebar ? sidebar.offsetWidth : 260;

        function onMouseMove(e){
            if(!isResizing) return;
            const dx = e.clientX - startX;
            const newWidth = Math.max(160, startWidth + dx);
            const px = newWidth + 'px';
            document.documentElement.style.setProperty('--sidebar-width', px);
            const sb = document.querySelector('.rz-sidebar');
            if (sb) sb.style.width = px;
            if (resizer) resizer.style.left = px;
        }

        function onMouseUp(e){
            if(!isResizing) return;
            isResizing = false;
            document.removeEventListener('mousemove', onMouseMove);
            document.removeEventListener('mouseup', onMouseUp);
            document.body.style.cursor = '';
            // persist
            const sb = document.querySelector('.rz-sidebar');
            if (sb) {
                const w = sb.style.width || getComputedStyle(sb).width;
                localStorage.setItem('sidebarWidth', w);
                if (dotNetRef) {
                    dotNetRef.invokeMethodAsync('OnSidebarResized', w);
                }
            }
        }

        document.body.style.cursor = 'col-resize';
        document.addEventListener('mousemove', onMouseMove);
        document.addEventListener('mouseup', onMouseUp);
    }

    window.sidebarGetWidth = function(){
        return localStorage.getItem('sidebarWidth') || '';
    }

    window.sidebarApplyWidth = function(width){
        const px = width || '260px';
        document.documentElement.style.setProperty('--sidebar-width', px);
        const sb = document.querySelector('.rz-sidebar');
        if (sb) sb.style.width = px;
        const resizer = document.querySelector('.sidebar-resizer');
        if (resizer) resizer.style.left = px;
    }

    // Adjust the log entries container max-height based on actual sidebar height
    window.sidebarAdjustLogHeight = function(elementId){
        try{
            const el = document.getElementById(elementId);
            if(!el) return;
            // prefer the sidebar element for height calculations
            const sidebar = document.querySelector('.rz-sidebar');
            const sidebarRect = sidebar ? sidebar.getBoundingClientRect() : document.documentElement.getBoundingClientRect();
            const elRect = el.getBoundingClientRect();
            // compute available space below the element's top until the bottom of the sidebar
            let available = Math.max(100, sidebarRect.bottom - elRect.top - 50); // leave 50px margin
            // set max-height
            el.style.boxSizing = 'border-box';
            el.style.maxHeight = available + 'px';
        }catch(e){
            // ignore
        }
    }

    window.sidebarScrollToBottom = function(elementId){
        const el = document.getElementById(elementId);
        if (!el) return;
        try {
            // small timeout to allow rendering
            setTimeout(() => {
                // scroll so bottom has a 50px margin from the bottom of viewport
                const target = el.scrollHeight - el.clientHeight + 50;
                el.scrollTop = target > 0 ? target : el.scrollHeight;
            }, 50);
        } catch (e) {
            // ignore
        }
    }
})();

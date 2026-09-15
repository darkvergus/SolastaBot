setInterval(() => {
    if (!document.hidden && !document.querySelector('details[open]') && !['INPUT', 'SELECT', 'TEXTAREA'].includes(document.activeElement?.tagName)) {
        window.location.reload();
    }
}, 15000);

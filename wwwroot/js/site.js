// Please see documentation at https://docs.microsoft.com/aspnet/core/client-side/bundling-and-minification
// for details on configuring this project to bundle and minify static web assets.

// Write your JavaScript code.
document.addEventListener("DOMContentLoaded", () => {
    const toggleBtn = document.getElementById("darkModeToggle");
    if (!toggleBtn) return;

    function setDarkMode(enabled) {
        document.body.classList.toggle("dark-mode", enabled);
        localStorage.setItem("darkMode", enabled ? "1" : "0");
        toggleBtn.textContent = enabled ? "☀️" : "🌙";
    }

    const isDark = localStorage.getItem("darkMode") === "1";
    setDarkMode(isDark);

    toggleBtn.addEventListener("click", () => {
        setDarkMode(!document.body.classList.contains("dark-mode"));
    });
});

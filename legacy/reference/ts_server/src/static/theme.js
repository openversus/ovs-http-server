(() => {
  const root = document.documentElement;
  const media = window.matchMedia("(prefers-color-scheme: dark)");
  let saved = null;
  try { saved = localStorage.getItem("ovs-theme"); } catch { /* private mode */ }

  const apply = (theme) => {
    root.dataset.theme = theme;
    const button = document.querySelector(".theme-toggle");
    if (button) {
      button.textContent = theme === "dark" ? "☀" : "☾";
      button.setAttribute("aria-label", `Switch to ${theme === "dark" ? "light" : "dark"} mode`);
    }
  };

  apply(saved === "dark" || saved === "light" ? saved : (media.matches ? "dark" : "light"));
  media.addEventListener?.("change", (event) => {
    if (!saved) apply(event.matches ? "dark" : "light");
  });

  window.addEventListener("DOMContentLoaded", () => {
    const button = document.createElement("button");
    button.type = "button";
    button.className = "theme-toggle";
    button.addEventListener("click", () => {
      saved = root.dataset.theme === "dark" ? "light" : "dark";
      try { localStorage.setItem("ovs-theme", saved); } catch { /* private mode */ }
      apply(saved);
    });
    document.body.appendChild(button);
    apply(root.dataset.theme || "light");
  });
})();

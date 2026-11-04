(function () {
  if (typeof window === "undefined" || typeof document === "undefined") return;
  if (window.__DARIBASE_WIDGET_MOUNTED__) return;

  var scriptEl =
    document.currentScript ||
    document.querySelector("script[data-workspace-id]");
  if (!scriptEl) return;

  var workspaceId = scriptEl.getAttribute("data-workspace-id");
  if (!workspaceId) return;

  window.__DARIBASE_WIDGET_MOUNTED__ = true;

  var srcUrl = scriptEl.getAttribute("src") || "";
  var origin = window.location.origin;
  try {
    origin = new URL(srcUrl, window.location.href).origin;
  } catch (e) {
    // fallback to current origin
  }

  var iframeUrl =
    origin + "/c/" + encodeURIComponent(workspaceId) + "?embed=1";

  var container = document.createElement("div");
  container.id = "daribase-widget-container";
  container.style.cssText =
    "position:fixed;bottom:max(20px,env(safe-area-inset-bottom));right:20px;z-index:2147483647;font-family:system-ui,-apple-system,sans-serif;";

  var iframe = document.createElement("iframe");
  iframe.src = iframeUrl;
  iframe.title = "Daribase Canli Destek";
  iframe.style.cssText =
    "width:400px;max-width:calc(100vw - 32px);height:640px;max-height:calc(100dvh - 108px);border:none;border-radius:24px;box-shadow:0 20px 60px -15px rgba(0,0,0,0.22);display:none;margin-bottom:12px;background:#ffffff;";

  function updateSize() {
    var mobile = window.matchMedia("(max-width: 600px)").matches;
    container.style.right = mobile ? "8px" : "20px";
    container.style.bottom = mobile
      ? "max(8px, env(safe-area-inset-bottom))"
      : "max(20px, env(safe-area-inset-bottom))";
    iframe.style.width = mobile ? "calc(100vw - 16px)" : "400px";
    iframe.style.maxWidth = mobile ? "calc(100vw - 16px)" : "calc(100vw - 32px)";
    iframe.style.height = mobile ? "calc(100dvh - 88px)" : "640px";
    iframe.style.maxHeight = mobile ? "calc(100dvh - 88px)" : "calc(100dvh - 108px)";
    iframe.style.borderRadius = mobile ? "16px" : "24px";
  }
  updateSize();
  window.addEventListener("resize", updateSize);

  var button = document.createElement("button");
  button.type = "button";
  button.setAttribute("aria-label", "Canli Destek");
  button.style.cssText =
    "width:56px;height:56px;border-radius:9999px;background:#7c3aed;color:#ffffff;border:none;cursor:pointer;box-shadow:0 12px 30px -6px rgba(124,58,237,0.55);display:flex;align-items:center;justify-content:center;margin-left:auto;transition:transform 0.15s ease;";
  button.innerHTML =
    '<svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M7.9 20A9 9 0 1 0 4 16.1L2 22Z"/></svg>';

  var isOpen = false;
  button.addEventListener("click", function () {
    isOpen = !isOpen;
    iframe.style.display = isOpen ? "block" : "none";
  });

  container.appendChild(iframe);
  container.appendChild(button);
  document.body.appendChild(container);
})();

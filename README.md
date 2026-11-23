# WebUI-Unity

Transparent Chromium HTML/CSS/JavaScript overlays for Unity, with mouse/keyboard input, click-through areas and asynchronous JSON communication between C# and JavaScript. Windows x64, Unity 6 (6000.0+); Direct3D 11/12 GPU rendering with CPU fallback.

## Install

In **Window > Package Manager**, select **+ > Install package from git URL** and enter:

```text
https://github.com/Saphirah/WebUI-Unity.git?path=/Packages/com.webui.chromium
```

Alternatively, clone this repository and use **Install package from disk** to select `Packages/com.webui.chromium/package.json`.

Dependencies install automatically. The first import downloads the Chromium runtime (~197 MB); wait for `WebUI: Chromium runtime ready` in the Console. Retry with **WebUI > Install browser runtime** if needed. No native compilation is required.

## Scene setup

1. In **Project Settings > Player > Other Settings > Active Input Handling**, select **Input Manager (Old)** or **Both** and restart if prompted. The default input adapter uses Unity's legacy input API.
2. Keep an enabled Camera in the scene. Create a **UI > Canvas** with **Screen Space - Overlay**, then a child **UI > Raw Image**. Stretch its RectTransform across the Canvas with zero offsets; keep the Canvas above other UI using its Sort Order.
3. Add **Chromium Overlay** (`WebUI.ChromiumOverlay`) to the Raw Image. Keep **Auto Size** enabled. Use an EventSystem with a Standalone Input Module for other uGUI controls.
4. Place your web build, including assets, in `Assets/StreamingAssets/WebUIContent/`. Set **Initial URL** to `https://ue.local/index.html`. Leave **Content Root** and **Runtime Root** empty for the default paths. HTML controls are the widgets; no Unity component is needed for each button.
5. For a development server, set **Initial URL** to e.g. `http://localhost:5173` and add `http://localhost:5173` to **Trusted Origins** before entering Play mode. Keep `https://ue.local` for local pages. Only trusted origins can send bridge messages.
6. Press **Play**. For a ready-made example, **WebUI > Create demo scene** creates and saves a new scene, copies sample HTML and attaches `ExampleBridge`; save your current scene first.

For transparent overlays, use `html,body{margin:0;background:transparent}`. Use `pointer-events:none` on decorative elements that should pass clicks through. To suppress your own gameplay input over UI, check `overlay.IsPointerOverUI(Input.mousePosition)` and `overlay.BrowserFocused`. With a custom input system, disable **Forward Legacy Input** and forward input through `Session.Mouse`, `Key` and `Focus`.

## Independent Windows presentation

Enable **Independent Composition** on a full-screen `ChromiumOverlay` before it starts. Use a windowed or borderless Windows x64 player with Direct3D 11 or 12 and one composition overlay per player window. The visual fills the client area and follows window resize. Its position, opacity and clipping are independent of the Raw Image. The texture path remains the default and is available in the Editor.

DirectComposition submits transparent browser frames on a native worker, independently of Unity rendering. The worker picks the newest available frame and returns shared textures after the GPU copy. Input and bridge callbacks still run through Unity. **Browser Frame Rate** requests 1–360 FPS from CEF and can be changed at runtime. CEF, the GPU and the display can limit actual throughput.

Use `IndependentPresentationActive` and `IndependentCompositionStatus` to check whether the visual is attached. An initialization failure is reported without switching presentation mode. `PresentedFrames` counts successful DXGI Present calls in this mode. `BrowserTexture` is empty, and Unity screenshots or backbuffer capture can omit the layer.

When a Steam overlay or capture flow opens, call `SetIndependentPresentationSuppressed(true)` on the Unity main thread. Pass `false` when it closes. Suppression hides the layer and releases browser input focus while retaining the page. On restoration and application focus gain, Unity requests a fresh paint. Verify compatibility with the capture or overlay tool you use.

Hit testing uses DOM rectangles. Elements at zero opacity and their descendants pass input through. A transparent-background button still receives input, and a child with `pointer-events:auto` can be interactive inside a parent with `pointer-events:none`.

## Bridge

Create `GameBridge.cs` and attach it to the Raw Image. Use this component as the session's request handler:

```csharp
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;
using WebUI;

[RequireComponent(typeof(ChromiumOverlay))]
public sealed class GameBridge : MonoBehaviour
{
    BrowserSession current;

    void Update()
    {
        var session = GetComponent<ChromiumOverlay>().Session;
        if (session == null || session == current) return;
        current = session;
        session.RequestHandler = (name, data) => Task.FromResult<JToken>(data);
        session.Message += async (name, data) =>
        {
            if (name != "ready") return;
            try { await session.CallAsync("setStatus", new JValue("Connected to Unity")); }
            catch (System.Exception error) { Debug.LogException(error); }
        };
    }
}
```

Save this page as `Assets/StreamingAssets/WebUIContent/index.html`:

```html
<!doctype html>
<html><head><meta charset="utf-8">
<style>html,body{margin:0;background:transparent}button{margin:24px}</style>
<script src="https://ue.local/__webui/webui.js"></script>
</head><body><button id="send">Send JSON</button>
<script>
webui.ready.then(async () => {
  ue.interface.setStatus = text => { document.querySelector('#send').textContent = text; };
  document.querySelector('#send').onclick = async () => {
    const reply = await webui.request('echo', {value: 42}, 5);
    console.log(reply);
  };
  await webui.broadcast('ready', {});
}).catch(console.error);
</script></body></html>
```

`webui.broadcast(name, data)` raises `Session.Message`; `webui.request(name, data, timeoutSeconds)` also invokes `RequestHandler` and resolves with its result. `Session.CallAsync(name, JToken)` calls `ue.interface[name]` and returns its result. Register page functions before sending `ready`; a session starting does not mean the page is ready.

To navigate at runtime, call `overlay.Session.LoadURL("https://ue.local/other.html")`; `LoadHTML(html)` loads an HTML string. Set startup fields before enabling the component; changing **Initial URL** during Play does not navigate the current session.

### Frequent state updates

Use `Session.TryCallStateAsync("update", delta)` with a `JObject` to publish absolute fields to a trusted, loaded page. The call snapshots the object and returns whether it accepted the update locally. The pipe writer serializes pending state off Unity's main thread and sends ordinary messages first. Pending updates to the same function merge by top-level field, with the newest value replacing an entire nested object or array.

Send a full state with `"uiReset": true` after each page-ready handshake. Omit that field on deltas. Use `CallAsync` for commands or requests with a reply. If state submission returns `false`, retain the changed fields and retry. Navigation discards pending state associated with the previous document.

## Build

Run `Scripts/BuildPlayer.ps1 -Composition` and `Scripts/TestPlayer.ps1 -Composition -Resize` to exercise the independent layer in the Windows test player. The smoke test checks resize without reloading the page, overlay suppression and restoration, and continued presentation during a brief Unity main-thread pause.

Build for **Windows x86_64** after runtime installation completes. The build hook copies Chromium automatically; files under `StreamingAssets/WebUIContent` are included by Unity. Use local web assets for a build that runs without a development server.

## License

This project is MIT.

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

## Build

Build for **Windows x86_64** after runtime installation completes. The build hook copies Chromium automatically; files under `StreamingAssets/WebUIContent` are included by Unity. Use local web assets for a build that runs without a development server.

## License

This project is MIT.

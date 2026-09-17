using System;
using System.Threading.Tasks;

namespace WebUI {
    // The Editor assembly supplies provisioning; players use staged StreamingAssets.
    public static class RuntimePreparation {
        public static Func<Task<string>> PrepareEditorRuntime;
    }
}

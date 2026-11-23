using System;
using System.Runtime.InteropServices;
namespace WebUI {
    internal static class Native {
        const string Dll="UnityWebUINative";
        [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern int UW_CreateComposition(int width,int height,out int error);
        [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern int UW_ResizeComposition(int id,int width,int height);
        [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern void UW_ShowComposition(int id,int visible);
        [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern int UW_CompositionActive(int id);
        [DllImport(Dll,CallingConvention=CallingConvention.Cdecl,CharSet=CharSet.Unicode)] internal static extern IntPtr UW_CreatePipe(string name);
        [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern int UW_CreateGPU(IntPtr texture);
        [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern int UW_CreateGPU12(IntPtr texture);
        [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern int UW_CreateError();
        [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern IntPtr UW_Output(int id);
        [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern int UW_CreateCPU(int width,int height);
        [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern int UW_Duplicate(int id,int pid,int slot,out ulong handle);
        [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern int UW_TrackProcess(int id,int pid);
        [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern int UW_Copies(int id);
        [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern void UW_RequestProbe(int id);
        [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern int UW_ProbeAlpha(int id);
        [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern int UW_Adapter(int id,out uint high,out uint low);
        [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern void UW_Frame(int id,int slot,ulong sequence);
        [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern int UW_Release(int id);
        [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern IntPtr UW_GetRenderEvent();
        [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern int UW_ReadCPU(int id,[Out] byte[] pixels,int capacity);
        [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern int UW_Error(int id);
        [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern void UW_Close(int id);
    }
}

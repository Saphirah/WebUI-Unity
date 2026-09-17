#pragma once
#include <windows.h>
#include <cstdint>
// Three SPSC-owned slots. 0=free, 1=writer-owned, 2=ready, 3=reader-owned.
// State transitions are interlocked; metadata/pixels are accessed only while owned.
struct CpuFrame { volatile LONG state; uint32_t sequence,width,height; };
inline size_t CpuStride(int width,int height){return sizeof(CpuFrame)+size_t(width)*height*4;}
inline CpuFrame* CpuSlot(void* data,int width,int height,int slot){return reinterpret_cast<CpuFrame*>(static_cast<char*>(data)+CpuStride(width,height)*slot);}

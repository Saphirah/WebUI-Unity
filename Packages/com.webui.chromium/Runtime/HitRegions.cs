using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Newtonsoft.Json.Linq;
namespace WebUI {
    // Reader-thread updates; immutable snapshots keep Unity raycasts non-blocking.
    internal sealed class HitRegions {
        sealed class Snapshot { internal float[][] Rects; internal long Time; internal bool Complete; internal int Width,Height; }
        readonly Dictionary<int,float[][]> entries=new Dictionary<int,float[][]>();
        Snapshot snapshot=new Snapshot {Rects=Array.Empty<float[]>()};
        bool valid;
        internal void Clear(){entries.Clear();valid=false;Volatile.Write(ref snapshot,new Snapshot{Rects=Array.Empty<float[]>()});}
        internal void Update(JObject packet){
            try {
                bool reset=(bool?)packet["reset"]??false;
                if(reset){entries.Clear();valid=true;}if(!valid)return;
                var removed=(JArray)packet["removed"];var updates=(JArray)packet["updates"];
                if(removed==null||updates==null||removed.Count>8192||updates.Count>8192)throw new FormatException();
                foreach(var id in removed)entries.Remove((int)id);
                foreach(var item in updates){
                    int id=(int)item["id"];var list=(JArray)item["rects"];
                    if(id<1||list==null||list.Count>8192)throw new FormatException();
                    var rects=new List<float[]>();foreach(JArray r in list){
                        if(r.Count!=4)throw new FormatException();var a=r.ToObject<float[]>();
                        if(!(a[0]>=0&&a[1]>=0&&a[2]<=1&&a[3]<=1&&a[0]<a[2]&&a[1]<a[3]))throw new FormatException();rects.Add(a);
                    }entries[id]=rects.ToArray();if(entries.Count>8192)throw new FormatException();
                }
                var all=new List<float[]>();foreach(var value in entries.Values){all.AddRange(value);if(all.Count>8192)throw new FormatException();}
                Volatile.Write(ref snapshot,new Snapshot{Rects=all.ToArray(),Complete=(bool?)packet["complete"]==true,Time=Stopwatch.GetTimestamp(),Width=(int?)packet["width"]??0,Height=(int?)packet["height"]??0});
            }catch{Clear();}
        }
        internal bool Hit(float u,float v,int width,int height){
            var s=Volatile.Read(ref snapshot);
            if(!s.Complete||s.Width!=width||s.Height!=height||(Stopwatch.GetTimestamp()-s.Time)/(double)Stopwatch.Frequency>.25)return true;
            foreach(var r in s.Rects)if(u>=r[0]&&v>=r[1]&&u<r[2]&&v<r[3])return true;
            return false;
        }
    }
}

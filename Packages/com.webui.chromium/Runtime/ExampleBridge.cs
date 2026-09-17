using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;
namespace WebUI {
    [RequireComponent(typeof(ChromiumOverlay))]
    [DisallowMultipleComponent]
    public sealed class ExampleBridge : MonoBehaviour {
        BrowserSession current;
        void Update(){var session=GetComponent<ChromiumOverlay>().Session;if(session==null||session==current)return;current=session;
            session.RequestHandler=(name,data)=>Task.FromResult<JToken>(new JObject{["ok"]=true,["echo"]=data});
            session.Message+=(name,data)=>{if(name=="clicked")Debug.Log("HTML button clicked: "+data,this);};
        }
    }
}
